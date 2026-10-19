using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CriticalCommonLib.Models;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Microsoft.Extensions.Hosting;

namespace CriticalCommonLib.Services;

public class OdrScanner : IOdrScanner, IDisposable
{
    private const byte Xor8 = 0x73;
    private const ushort Xor16 = 0x7373;
    private const uint Xor32 = 0x73737373;

    private readonly IFramework _framework;
    private readonly IPluginLog _pluginLog;
    private readonly IGameInteropProvider _gameInteropProvider;
    private readonly IClientState _clientState;
    private readonly ICharacterMonitor _characterMonitor;
    private bool _initialBootCheck;
    private Hook<UserFileManager.UserFileEvent.Delegates.WriteFile>? _writeFileHook;
    private Hook<UserFileManager.UserFileEvent.Delegates.ReadFile>? _readFileHook;
    private readonly Dictionary<ulong, InventorySortOrder> _sortOrders;
    private InventorySortOrder? _lastSortOrder;

    // 解析時暫放一個背包的格子；用 Exchange 借還，同時有第二個解析（卸載期 RunOnFrameworkThread 就地執行）時另配一份。
    private (int slotIndex, int containerIndex)[]? _slotScratch;

    public delegate void SortOrderChangedDelegate(InventorySortOrder sortOrder);
    public event SortOrderChangedDelegate? OnSortOrderChanged;

    public OdrScanner(IFramework framework, IPluginLog pluginLog, IGameInteropProvider gameInteropProvider,
        IClientState clientState, ICharacterMonitor characterMonitor)
    {
        _framework = framework;
        _pluginLog = pluginLog;
        _gameInteropProvider = gameInteropProvider;
        _clientState = clientState;
        _characterMonitor = characterMonitor;
        _sortOrders = new Dictionary<ulong, InventorySortOrder>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pluginLog.Verbose("Starting the ODR scanner.");
        _framework.Update += FrameworkOnUpdate;
        return Task.CompletedTask;
    }

    public InventorySortOrder? GetSortOrder(ulong characterId)
    {
        return _sortOrders.GetValueOrDefault(characterId);
    }

    private unsafe void FrameworkOnUpdate(IFramework framework1)
    {
        var itemOrderModule = ItemOrderModule.Instance();
        if (itemOrderModule == null) return;

        if (_readFileHook == null)
        {
            _readFileHook = _gameInteropProvider.HookFromAddress<UserFileManager.UserFileEvent.Delegates.ReadFile>(
                itemOrderModule->UserFileEvent.VirtualTable->ReadFile,
                ReadFile);
            _readFileHook.Enable();
        }

        if (_writeFileHook == null)
        {
            _writeFileHook = _gameInteropProvider.HookFromAddress<UserFileManager.UserFileEvent.Delegates.WriteFile>(
                itemOrderModule->UserFileEvent.VirtualTable->WriteFile,
                WriteFile);
            _writeFileHook.Enable();
        }

        if (!_initialBootCheck && _clientState.IsLoggedIn)
        {
            _initialBootCheck = true;
            QueueInitialOdrParse();

        }
    }

    // lambda 留在每幀都跑的 FrameworkOnUpdate 裡時，閉包物件在方法開頭每幀配置一個。
    // 2 秒後才執行，所以模組指標在執行當下重取、判空（原生指標不跨幀保存）。
    private unsafe void QueueInitialOdrParse()
    {
        _framework.RunOnTick(() =>
        {
            _pluginLog.Verbose("Marking ODR as modified so we can parse it.");
            var itemOrderModule = ItemOrderModule.Instance();
            if (itemOrderModule == null) return;
            itemOrderModule->UserFileEvent.IsSavePending = true;
        }, TimeSpan.FromSeconds(2));
    }

    private unsafe bool ReadFile(UserFileManager.UserFileEvent* thisPtr, bool decrypt, byte* ptr, ushort version, uint length)
    {
        var result = _readFileHook!.Original(thisPtr, decrypt, ptr, version, length);
        try
        {
            if (thisPtr != null && ptr != null && result)
            {
                var buffer = new byte[length];
                if (length == 0)
                {
                    return result;
                }
                Marshal.Copy((IntPtr)ptr, buffer, 0, (int)length);
                _framework.RunOnFrameworkThread(
                    () =>
                    {
                        try
                        {
                            var contentId = _characterMonitor.LocalContentId;
                            var sortOrder = ParseItemOrder(buffer, PreviousSortOrder(contentId), true);
                            _sortOrders[contentId] = sortOrder;
                            _lastSortOrder = sortOrder;
                            OnSortOrderChanged?.Invoke(sortOrder);
                            _pluginLog.Verbose("Parsed the ODR from memory after a read.");
                        }
                        catch (Exception e)
                        {
                            _pluginLog.Error("Failed to parse odr from memory.", e);
                        }
                    });
            }
        }
        catch (Exception e)
        {
            _pluginLog.Error("Failed to parse odr from memory.", e);
        }

        return result;
    }

    private unsafe uint WriteFile(UserFileManager.UserFileEvent* thisPtr, byte* ptr, uint length)
    {
        var result = _writeFileHook!.Original(thisPtr, ptr, length);

        try
        {
            if (thisPtr != null && ptr != null)
            {
                var buffer = new byte[length];
                Marshal.Copy((IntPtr)ptr, buffer, 0, (int)length);
                _framework.RunOnFrameworkThread(() =>
                {
                    try
                    {
                        var contentId = _characterMonitor.LocalContentId;
                        var sortOrder = ParseItemOrder(buffer, PreviousSortOrder(contentId));
                        _sortOrders[contentId] = sortOrder;
                        _lastSortOrder = sortOrder;
                        OnSortOrderChanged?.Invoke(sortOrder);
                        _pluginLog.Verbose("Parsed the ODR from memory after a write.");
                    }
                    catch (Exception e)
                    {
                        _pluginLog.Error("Failed to parse odr from memory.", e);
                    }
                });
            }
        }
        catch (Exception e)
        {
            _pluginLog.Error("Failed to parse odr from memory.", e);
        }

        return result;
    }

    private InventorySortOrder? PreviousSortOrder(ulong contentId) =>
        _sortOrders.GetValueOrDefault(contentId) ?? _lastSortOrder;

    // previous 裡同名背包／同 id 僱員的格子逐筆相同時沿用它的清單（RetainerSortOrder 整個沿用），否則配新的。
    // 契約：交出去的清單沒有人改（InventoryScanner 只讀）。
    private InventorySortOrder ParseItemOrder(byte[] buffer, InventorySortOrder? previous, bool read = false)
    {
        var scratch = Interlocked.Exchange(ref _slotScratch, null) ?? new (int slotIndex, int containerIndex)[256];
        try
        {
            return ParseItemOrder(buffer, previous, read, ref scratch);
        }
        finally
        {
            Volatile.Write(ref _slotScratch, scratch);
        }
    }

    private InventorySortOrder ParseItemOrder(byte[] buffer, InventorySortOrder? previous, bool read,
        ref (int slotIndex, int containerIndex)[] scratch)
    {
        using (var reader = new MemoryStream(buffer))
        {
            var retainerInventories = new Dictionary<ulong, RetainerSortOrder>();
            var normalInventories =
                new Dictionary<string, List<(int SlotIndex, int ContainerIndex)>>();

            if (!read) Advance(reader, 1); // Unknown Byte, Appears to be the main inventory size, but that is

            var inventoryIndex = 0;
            try
            {
                while (true)
                {
                    var identifier = read ? ReadUInt8(reader) : ReadUInt8(reader) ^ Xor8;
                    switch (identifier)
                    {
                        case 0x56:
                        {
                            // Unknown
                            Advance(reader, read ? ReadUInt8(reader) : ReadUInt8(reader) ^ Xor8);
                            break;
                        }
                        case 110:
                        {
                            // Start of an inventory
                            var slotCount = ReadInventory(reader, read, ref scratch);

                            var i = inventoryIndex++;
                            if (i >= 0 && _inventoryNames.Length > i)
                            {
                                var inventoryName = _inventoryNames[i];
                                List<(int slotIndex, int containerIndex)>? previousInventory = null;
                                previous?.NormalInventories.TryGetValue(inventoryName, out previousInventory);
                                normalInventories.Add(inventoryName, ReuseOrCopy(previousInventory, scratch, slotCount));
                            }

                            break;
                        }

                        case 78:
                        {
                            var retainers = ReadRetainer(reader, read, previous, ref scratch);
                            retainerInventories = retainers;
                            break;
                        }

                        case 115:
                        {
                            return new InventorySortOrder(retainerInventories, normalInventories);
                        }
                        default:
                        {
                            throw new Exception("Unexpected Identifier: " + identifier);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _pluginLog.Verbose(e.Message);
            }

            return new InventorySortOrder(retainerInventories, normalInventories);
        }
    }

    private void UnexpectedSizeError(int expected, int real)
    {
        throw new Exception("Incorrect Size - Expected " + expected + " but got " + real);
    }

    private void UnexpectedIdentifierError(int expected, int real)
    {
        throw new Exception("Unexpected Identifier - Expected " + expected + " but got " + real);
    }

    private (int slotIndex, int containerIntex) ReadSlot(MemoryStream reader, bool read)
    {
        var s = read ? ReadUInt8(reader) : ReadUInt8(reader) ^ Xor8;
        if (s != 4) UnexpectedSizeError(4, s);
        return (read ? ReadUInt16(reader) : ReadUInt16(reader) ^ Xor16,
            read ? ReadUInt16(reader) : ReadUInt16(reader) ^ Xor16);
    }

    // 格子寫進 scratch[0..回傳值)，不夠大就加倍。
    private int ReadInventory(MemoryStream reader, bool read, ref (int slotIndex, int containerIndex)[] scratch)
    {
        var s = read ? ReadUInt8(reader) : ReadUInt8(reader) ^ Xor8;
        if (s != 4) UnexpectedSizeError(4, s);
        var slotCount = read ? ReadUInt32(reader) : ReadUInt32(reader) ^ Xor32;

        var count = 0;
        for (var i = 0; i < slotCount; i++)
        {
            var x = read ? ReadUInt8(reader) : ReadUInt8(reader) ^ Xor8;
            if (x != 0x69) UnexpectedIdentifierError(0x69, x);
            var slot = ReadSlot(reader, read);
            if (count == scratch.Length) Array.Resize(ref scratch, scratch.Length * 2);
            scratch[count++] = slot;
        }

        return count;
    }

    private static List<(int slotIndex, int containerIndex)> ReuseOrCopy(
        List<(int slotIndex, int containerIndex)>? previousInventory, (int slotIndex, int containerIndex)[] scratch, int count)
    {
        var slots = new ReadOnlySpan<(int slotIndex, int containerIndex)>(scratch, 0, count);
        if (previousInventory != null && CollectionsMarshal.AsSpan(previousInventory).SequenceEqual(slots))
        {
            return previousInventory;
        }

        var inventory = new List<(int slotIndex, int containerIndex)>(count);
        inventory.AddRange(slots);
        return inventory;
    }

    private Dictionary<ulong, RetainerSortOrder> ReadRetainer(MemoryStream reader, bool read, InventorySortOrder? previous,
        ref (int slotIndex, int containerIndex)[] scratch)
    {
        var s = read ? ReadUInt8(reader) : ReadUInt8(reader) ^ Xor8;
        if (s != 4) UnexpectedSizeError(4, s);
        var retainerCount = read ? ReadUInt32(reader) : ReadUInt32(reader) ^ Xor32;
        var retainers = new Dictionary<ulong, RetainerSortOrder>();

        for (var i = 0; i < retainerCount; i++)
        {
            var x = read ? ReadUInt8(reader) : ReadUInt8(reader) ^ Xor8;
            if (x != 0x52) UnexpectedIdentifierError(0x52, x);
            var retainer = ReadRetainerSlot(reader, read, previous, ref scratch);
            retainers.Add(retainer.Id, retainer);
        }

        return retainers;
    }

    private RetainerSortOrder ReadRetainerSlot(MemoryStream reader, bool read, InventorySortOrder? previous,
        ref (int slotIndex, int containerIndex)[] scratch)
    {
        var s = read ? ReadUInt8(reader) : ReadUInt8(reader) ^ Xor8;
        if (s != 8) UnexpectedSizeError(8, s);

        // 8 個位元組依序是 id 的低位到高位（小端序）。
        ulong actualId = 0;
        for (var b = 0; b < 8; b++)
        {
            var idByte = (byte)(read ? ReadUInt8(reader) : ReadUInt8(reader) ^ Xor8);
            actualId |= (ulong)idByte << (8 * b);
        }

        var x = read ? ReadUInt8(reader) : ReadUInt8(reader) ^ Xor8;
        if (x != 0x6E) UnexpectedIdentifierError(0x6E, x);
        var slotCount = ReadInventory(reader, read, ref scratch);

        RetainerSortOrder? previousRetainer = null;
        previous?.RetainerInventories.TryGetValue(actualId, out previousRetainer);
        var inventory = ReuseOrCopy(previousRetainer?.InventoryCoords, scratch, slotCount);
        return previousRetainer != null && ReferenceEquals(inventory, previousRetainer.InventoryCoords)
            ? previousRetainer
            : new RetainerSortOrder(actualId, inventory);
    }

    private string[] _inventoryNames =
    {
        "PlayerInventory",
        "ArmouryMainHand",
        "ArmouryHead",
        "ArmouryBody",
        "ArmouryHands",
        "ArmouryLegs",
        "ArmouryFeet",
        "ArmouryOffHand",
        "ArmouryEars",
        "ArmouryNeck",
        "ArmouryWrists",
        "ArmouryRings",
        "ArmourySoulCrystals",
        "SaddleBag",
        "SaddleBagPremium"
    };


    private byte ReadUInt8(MemoryStream reader)
    {
        return (byte)reader.ReadByte();
    }

    // 小端序；C# 由左到右求值，先讀的是低位。
    private ushort ReadUInt16(MemoryStream reader)
    {
        return (ushort)(ReadUInt8(reader) | ReadUInt8(reader) << 8);
    }

    private uint ReadUInt32(MemoryStream reader)
    {
        return (uint)(ReadUInt8(reader) | ReadUInt8(reader) << 8 | ReadUInt8(reader) << 16 | ReadUInt8(reader) << 24);
    }

    private void Advance(MemoryStream reader, int amount)
    {
        reader.Position += amount;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pluginLog.Verbose("Stopping service {type} ({this})", GetType().Name, this);
        _writeFileHook?.Dispose();
        _readFileHook?.Dispose();
        _framework.Update -= FrameworkOnUpdate;
        _pluginLog.Verbose("Stopped service {type} ({this})", GetType().Name, this);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _pluginLog.Verbose("Disposing {type} ({this})", GetType().Name, this);
        _writeFileHook?.Dispose();
        _readFileHook?.Dispose();
    }
}
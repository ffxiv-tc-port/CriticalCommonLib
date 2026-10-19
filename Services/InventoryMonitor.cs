using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CriticalCommonLib.Crafting;
using CriticalCommonLib.Models;
using CriticalCommonLib.Extensions;
using CriticalCommonLib.GameStructs;
using Dalamud.Plugin.Services;
using static FFXIVClientStructs.FFXIV.Client.Game.InventoryItem;
using InventoryItem = CriticalCommonLib.Models.InventoryItem;
using InventoryType = CriticalCommonLib.Enums.InventoryType;

namespace CriticalCommonLib.Services
{
    public class InventoryMonitor : IInventoryMonitor
    {
        public delegate void InventoryChangedDelegate(List<InventoryChange> inventoryChanges, ItemChanges? itemChanges = null);

        private IEnumerable<InventoryItem> _allItems;
        private ICharacterMonitor _characterMonitor;
        private Dictionary<ulong, Inventory> _inventories;
        private Dictionary<(uint, ItemFlags, ulong), int> _characterItemCounts = new();
        private Dictionary<(uint, ItemFlags), int> _itemCounts = new();
        private int _modelVersion;
        private Dictionary<(uint, ItemFlags, ulong), int>? _lastRoundCounts;
        private int _lastRoundCountsVersion;
        private Dictionary<InventoryType, bool> _loadedInventories;
        private Queue<DateTime> _scheduledUpdates = new ();
        private Dictionary<uint, ItemMarketBoardInfo> _retainerMarketPrices = new();
        private IInventoryScanner _inventoryScanner;
        private ICraftMonitor _craftMonitor;
        private IFramework _frameworkService;
        private readonly IPluginLog _pluginLog;
        private readonly Inventory.Factory _inventoryFactory;

        public InventoryMonitor(ICharacterMonitor monitor, ICraftMonitor craftMonitor, IInventoryScanner scanner, IFramework frameworkService, IPluginLog pluginLog, Inventory.Factory inventoryFactory)
        {
            _characterMonitor = monitor;
            _craftMonitor = craftMonitor;
            _inventoryScanner = scanner;
            _frameworkService = frameworkService;
            _pluginLog = pluginLog;
            _inventoryFactory = inventoryFactory;

            _inventories = new Dictionary<ulong, Inventory>();
            _allItems = new List<InventoryItem>();
            _loadedInventories = new Dictionary<InventoryType, bool>();

            _inventoryScanner.BagsChanged += InventoryScannerOnBagsChanged;
            _characterMonitor.OnCharacterRemoved += CharacterMonitorOnOnCharacterRemoved;
        }

        private void InventoryScannerOnBagsChanged(List<BagChange> changes)
        {
            _pluginLog.Verbose("Bags changed, generating inventory");
            GenerateInventories(InventoryGenerateReason.ScheduledUpdate);
        }

        private void CharacterMonitorOnOnCharacterRemoved(ulong characterId)
        {
            if (_inventories.ContainsKey(characterId))
            {
                ClearCharacterInventories(characterId);

                _frameworkService.RunOnFrameworkThread(() =>
                {
                    OnInventoryChanged?.Invoke(new List<InventoryChange>());
                });
            }
        }

        public Dictionary<ulong, Inventory> Inventories => _inventories;

        public IEnumerable<InventoryItem> AllItems => _allItems;

        public Dictionary<(uint, ItemFlags, ulong), int> CharacterItemCounts => _characterItemCounts;
        public Dictionary<(uint, ItemFlags), int> ItemCounts => _itemCounts;

        public event InventoryChangedDelegate? OnInventoryChanged;

        public List<InventoryItem> GetSpecificInventory(ulong characterId, InventoryCategory category)
        {
            if (_inventories.ContainsKey(characterId))
            {
                return _inventories[characterId].GetItemsByCategory(category);
            }

            return new List<InventoryItem>();
        }

        public List<InventoryItem> GetSpecificInventory(ulong characterId, InventoryType inventoryType)
        {
            if (_inventories.ContainsKey(characterId))
            {
                return _inventories[characterId].GetItemsByType(inventoryType);
            }

            return new List<InventoryItem>();
        }

        public void ClearCharacterInventories(ulong characterId)
        {
            if (_inventories.ContainsKey(characterId))
            {
                _inventoryScanner.ClearRetainerCache(characterId);
                _inventories[characterId].ClearInventories();
                InvalidateRoundCounts();

                _frameworkService.RunOnFrameworkThread(() =>
                {
                    OnInventoryChanged?.Invoke(new List<InventoryChange>());
                });
            }
        }
        public void LoadExistingData(List<InventoryItem> inventories)
        {
            var groupedInventories = inventories.GroupBy(c => c.RetainerId);

            foreach (var characterKvp in groupedInventories)
            {
                var characterId = characterKvp.Key;
                var character = _characterMonitor.GetCharacterById(characterId);
                if (character != null)
                {
                    if (!_inventories.ContainsKey(characterId))
                    {
                        _inventories[characterId] = _inventoryFactory.Invoke(character.CharacterType, characterId);
                    }

                    _inventories[characterId].LoadItemsWithoutChanges(characterKvp.ToArray());
                }
                else
                {
                    _pluginLog.Warning("Could not find character with ID " + characterId + " while trying to load in existing data.");
                }
            }

            InvalidateRoundCounts();
            FillEmptySlots();
            GenerateAllItems();
            _frameworkService.RunOnFrameworkThread(() =>
            {
                OnInventoryChanged?.Invoke(new List<InventoryChange>());
            });
        }

        public void SignalRefresh()
        {
            _frameworkService.RunOnFrameworkThread(() =>
            {
                OnInventoryChanged?.Invoke(new List<InventoryChange>());
            });
        }

        public bool Started => _started;

        public void Start()
        {
            _started = true;
        }

        public void FillEmptySlots()
        {
            foreach (var inventory in _inventories)
            {
                inventory.Value.FillSlots();
            }
            InvalidateRoundCounts();
        }

        // 不經 GenerateInventoriesTask 改模型的路徑改完都要呼叫：上一輪結尾的計數就不再當下一輪的比對基準。
        private void InvalidateRoundCounts()
        {
            Interlocked.Increment(ref _modelVersion);
        }

        public void GenerateItemCounts()
        {
            var characterItemCounts = CountCharacterItems();
            // 由各角色的筆數加總而不是逐格再算一次：各鍵第一次出現的順序與總和都相同。
            var itemCounts = new Dictionary<(uint, ItemFlags), int>(_itemCounts.Count);
            foreach (var kv in characterItemCounts)
            {
                CollectionsMarshal.GetValueRefOrAddDefault(itemCounts, (kv.Key.Item1, kv.Key.Item2), out _) += kv.Value;
            }
            _characterItemCounts = characterItemCounts;
            _itemCounts = itemCounts;
        }

        /// <summary>只重算並交出 <see cref="CharacterItemCounts"/>；<see cref="ItemCounts"/> 不動。</summary>
        private void GenerateCharacterItemCounts()
        {
            _characterItemCounts = CountCharacterItems();
        }

        private Dictionary<(uint, ItemFlags, ulong), int> CountCharacterItems()
        {
            // 已交出去的字典消費端可能還拿著，永遠不再改 ⇒ 每次都 new；容量取上一份的筆數只為了省掉擴容。
            var characterItemCounts = new Dictionary<(uint, ItemFlags, ulong), int>(_characterItemCounts.Count);
            foreach (var inventory in _inventories)
            {
                // 與 GetAllInventories() 同樣的袋子、同樣的順序，只是不另建清單。
                foreach (var inventoryType in Inventory.AllInventoryTypes)
                {
                    var itemList = inventory.Value.GetInventoryByType(inventoryType);
                    if (itemList == null) continue;
                    foreach (var item in itemList)
                    {
                        if (item == null) continue;
                        CollectionsMarshal.GetValueRefOrAddDefault(characterItemCounts, (item.ItemId, item.Flags, item.RetainerId), out _) += (int)item.Quantity;
                    }
                }
            }
            return characterItemCounts;
        }
        private ItemChangesItem ConvertHashedItem((uint, ItemFlags, ulong) itemHash, int quantity)
        {
            return new ItemChangesItem()
            {
                ItemId = itemHash.Item1,
                Flags = itemHash.Item2,
                CharacterId = itemHash.Item3,
                Quantity = quantity,
                Date = DateTime.Now
            };
        }

        private ItemChanges CompareItemCounts(Dictionary<(uint, ItemFlags, ulong), int> oldItemCounts, Dictionary<(uint, ItemFlags, ulong), int> newItemCounts)
        {
            Dictionary<(uint, ItemFlags, ulong), int> newItems = new Dictionary<(uint, ItemFlags, ulong), int>();
            Dictionary<(uint, ItemFlags, ulong), int> removedItems = new Dictionary<(uint, ItemFlags, ulong), int>();

            // Iterate through the oldItems dictionary
            foreach (var oldItem in oldItemCounts)
            {
                // Check if the item is present in the newItems dictionary
                if (newItemCounts.TryGetValue(oldItem.Key, out var newCount))
                {
                    // Check if the quantity has changed
                    if (newCount != oldItem.Value)
                    {
                        // Add the item to the updatedItems dictionary
                        var relativeCount = newCount - oldItem.Value;
                        if (relativeCount > 0)
                        {
                            newItems.Add(oldItem.Key, relativeCount);
                        }
                        else
                        {
                            removedItems.Add(oldItem.Key, Math.Abs(relativeCount));
                        }
                    }
                }
                else
                {
                    // Add the item to the removedItems dictionary
                    removedItems.Add(oldItem.Key, oldItem.Value);
                }
            }

            // Iterate through the newItems dictionary
            foreach (var newItem in newItemCounts)
            {
                // Check if the item is not present in the oldItems dictionary
                if (!oldItemCounts.ContainsKey(newItem.Key))
                {
                    // Add the item to the newItems dictionary
                    newItems.Add(newItem.Key, newItem.Value);
                }
            }

            List<ItemChangesItem> actualAddedItems = new(newItems.Count);
            List<ItemChangesItem> actualDeletedItems = new(removedItems.Count);

            foreach (var newItem in newItems)
            {
                actualAddedItems.Add(ConvertHashedItem(newItem.Key, newItem.Value));
            }

            foreach (var removedItem in removedItems)
            {
                actualDeletedItems.Add(ConvertHashedItem(removedItem.Key, removedItem.Value));
            }

            return new ItemChanges( actualAddedItems, actualDeletedItems);
        }

        private void GenerateAllItems()
        {
            // 舊清單交出去後不再改，同樣每次 new；容量取上一份的件數。
            List<InventoryItem> newItems = new List<InventoryItem>(_allItems.TryGetNonEnumeratedCount(out var lastCount) ? lastCount : 0);

            foreach (var characterInventory in _inventories)
            {
                foreach (var inventoryType in Inventory.AllInventoryTypes)
                {
                    var inventory = characterInventory.Value.GetInventoryByType(inventoryType);
                    if (inventory == null) continue;
                    foreach (var item in inventory)
                    {
                        if (item == null || item.ItemId == 0) continue;
                        newItems.Add(item);
                    }
                }
            }
            _allItems = newItems;
        }

        public enum InventoryGenerateReason
        {
            InventoryChanged,
            ScheduledUpdate,
            NetworkUpdate,
            WindowOpened,
            MonitorStarted,
        }

        // 0＝閒置、1＝有一輪在排隊或在跑、2＝同上而且之後又有變動（這輪跑完要再補一輪）。
        private int _generateState;

        // 變動停了 QuietMs 才開始一輪；一直有變動時，從這批第一次變動起最多等 MaxDelayMs。
        // 掃描端兩次觸發至少隔 500 ms，QuietMs 要比它長才併得起來。
        private const int QuietMs = 650;
        private const int MaxDelayMs = 750;
        private long _lastChangeTimestamp;
        private long _batchStartTimestamp;
        private readonly CancellationTokenSource _disposeCts = new();

        private unsafe void GenerateInventories(InventoryGenerateReason generateReason)
        {
            if (!_started || Volatile.Read(ref _disposed))
            {
                return;
            }
            var now = Stopwatch.GetTimestamp();
            Volatile.Write(ref _lastChangeTimestamp, now);
            // 同一時間最多一輪：只有從閒置轉出去的那次起工作，其餘只標記「要補一輪」。
            while (true)
            {
                var state = Volatile.Read(ref _generateState);
                if (state == 2) return;
                if (Interlocked.CompareExchange(ref _generateState, state + 1, state) != state) continue;
                if (state == 0)
                {
                    Volatile.Write(ref _batchStartTimestamp, now);
                    Task.Run(GenerateInventoriesLoop);
                }
                return;
            }
        }

        private async Task GenerateInventoriesLoop()
        {
            while (true)
            {
                try
                {
                    await WaitForQuiet().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                // 等完才收掉「要補一輪」：等待期間的變動併進這輪；之後才進來的變動一定會讓下面的 CAS 失敗、再跑一輪。
                Interlocked.Exchange(ref _generateState, 1);
                if (Volatile.Read(ref _disposed))
                {
                    Volatile.Write(ref _generateState, 0);
                    return;
                }
                try
                {
                    GenerateInventoriesTask();
                }
                catch (Exception e)
                {
                    _pluginLog.Error(e, "Generating inventories failed.");
                }
                if (Interlocked.CompareExchange(ref _generateState, 0, 1) == 1) return;
                // 補跑那批的變動都在剛才那輪期間進來，從這一刻起算上限。
                Volatile.Write(ref _batchStartTimestamp, Stopwatch.GetTimestamp());
            }
        }

        private async Task WaitForQuiet()
        {
            var deadline = Volatile.Read(ref _batchStartTimestamp) + MaxDelayMs * Stopwatch.Frequency / 1000;
            while (!Volatile.Read(ref _disposed))
            {
                var due = Math.Min(Volatile.Read(ref _lastChangeTimestamp) + QuietMs * Stopwatch.Frequency / 1000, deadline);
                var remaining = due - Stopwatch.GetTimestamp();
                if (remaining <= 0) return;
                await Task.Delay((int)(remaining * 1000 / Stopwatch.Frequency) + 1, _disposeCts.Token).ConfigureAwait(false);
            }
        }

        private void GenerateInventoriesTask()
        {
            var characterId = _characterMonitor.LocalContentId;
            if (characterId == 0)
            {
                _pluginLog.Debug("Not generating inventory, not logged in.");
                return;
            }


            // 開頭只需要改動前的各角色筆數當比對基準；兩輪之間沒有別人改過模型就沿用上一輪結尾那份。
            // 先取走再算：這輪中途擲例外時模型可能已改一半，下一輪不得沿用。
            var lastCounts = _lastRoundCounts;
            _lastRoundCounts = null;
            Dictionary<(uint, ItemFlags, ulong), int> oldItemCounts;
            if (lastCounts != null && _lastRoundCountsVersion == Volatile.Read(ref _modelVersion))
            {
                oldItemCounts = lastCounts;
            }
            else
            {
                GenerateCharacterItemCounts();
                oldItemCounts = _characterItemCounts;
            }

            if (!_inventories.ContainsKey(characterId))
            {
                _inventories[characterId] = _inventoryFactory.Invoke(CharacterType.Character, characterId);
            }

            var inventory = _inventories[characterId];
            List<InventoryChange> inventoryChanges = new List<InventoryChange>();

            GenerateCharacterInventories(inventory, inventoryChanges);
            GenerateSaddleInventories(inventory, inventoryChanges);
            GenerateArmouryChestInventories(inventory, inventoryChanges);
            GenerateEquippedItems(inventory, inventoryChanges);
            GenerateFreeCompanyInventories(inventoryChanges);
            GenerateHousingInventories(inventoryChanges);
            GenerateRetainerInventories(inventoryChanges);
            GenerateGlamourInventories(inventory, inventoryChanges);
            GenerateArmoireInventories(inventory, inventoryChanges);
            GenerateCurrencyInventories(inventory, inventoryChanges);
            GenerateCrystalInventories(inventory, inventoryChanges);

            // 版本要在計數「之前」讀：計數途中有人改模型，版本就對不上、下一輪重算。
            var version = Volatile.Read(ref _modelVersion);
            GenerateItemCounts();
            var newItemCounts = _characterItemCounts;
            _lastRoundCountsVersion = version;
            _lastRoundCounts = newItemCounts;
            var itemChanges = CompareItemCounts(oldItemCounts, newItemCounts);
            GenerateAllItems();
            if (Volatile.Read(ref _disposed)) return;
            _frameworkService.RunOnFrameworkThread(() =>
            {
                OnInventoryChanged?.Invoke(inventoryChanges, itemChanges);
            });
        }

        private unsafe void GenerateCharacterInventories(Inventory inventory, List<InventoryChange> inventoryChanges)
        {
            if (_inventoryScanner.InMemory.Contains(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2) &&
                _inventoryScanner.InMemory.Contains(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3) &&
                _inventoryScanner.InMemory.Contains(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1) &&
                _inventoryScanner.InMemory.Contains(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4))
            {
                var bag1 = _inventoryScanner.CharacterBag1;
                var bag2 = _inventoryScanner.CharacterBag2;
                var bag3 = _inventoryScanner.CharacterBag3;
                var bag4 = _inventoryScanner.CharacterBag4;
                inventory.LoadGameItems(bag1, InventoryType.Bag0, InventoryCategory.CharacterBags, false, inventoryChanges);
                inventory.LoadGameItems(bag2, InventoryType.Bag1, InventoryCategory.CharacterBags, false, inventoryChanges);
                inventory.LoadGameItems(bag3, InventoryType.Bag2, InventoryCategory.CharacterBags, false, inventoryChanges);
                inventory.LoadGameItems(bag4, InventoryType.Bag3, InventoryCategory.CharacterBags, false, inventoryChanges);
            }
        }

        private unsafe void GenerateSaddleInventories(Inventory inventory, List<InventoryChange> inventoryChanges)
        {
            if (_inventoryScanner.InMemory.Contains(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.SaddleBag1) &&
                _inventoryScanner.InMemory.Contains(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.SaddleBag2))
            {
                var bag1 = _inventoryScanner.SaddleBag1;
                var bag2 = _inventoryScanner.SaddleBag2;
                inventory.LoadGameItems(bag1, InventoryType.SaddleBag0, InventoryCategory.CharacterSaddleBags, false, inventoryChanges);
                inventory.LoadGameItems(bag2, InventoryType.SaddleBag1, InventoryCategory.CharacterSaddleBags, false, inventoryChanges);
            }

            if (_inventoryScanner.InMemory.Contains(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.PremiumSaddleBag1) &&
                _inventoryScanner.InMemory.Contains(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.PremiumSaddleBag2))
            {
                var bag1 = _inventoryScanner.PremiumSaddleBag1;
                var bag2 = _inventoryScanner.PremiumSaddleBag2;
                inventory.LoadGameItems(bag1, InventoryType.PremiumSaddleBag0, InventoryCategory.CharacterPremiumSaddleBags, false, inventoryChanges);
                inventory.LoadGameItems(bag2, InventoryType.PremiumSaddleBag1, InventoryCategory.CharacterPremiumSaddleBags, false, inventoryChanges);
            }
        }

        private unsafe void GenerateArmouryChestInventories(Inventory inventory, List<InventoryChange> inventoryChanges)
        {
            HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType> inventoryTypes = new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>();
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryMainHand);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryHead);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryBody);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryHands);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryLegs);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryFeets);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryOffHand);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryEar);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryNeck);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryWrist);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryRings);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmorySoulCrystal);
            foreach (var inventoryType in inventoryTypes)
            {
                if (!_inventoryScanner.InMemory.Contains(inventoryType))
                {
                    return;
                }
            }

            var gearSets = _inventoryScanner.GetGearSetMembership();
            foreach (var inventoryType in inventoryTypes)
            {
                if (!_inventoryScanner.InMemory.Contains(inventoryType))
                {
                    continue;
                }
                var armoryItems = _inventoryScanner.GetInventoryByType(inventoryType);
                inventory.LoadGameItems(armoryItems, inventoryType.Convert(), InventoryCategory.CharacterArmoryChest, false, inventoryChanges,
                    (newItem,_) => ApplyGearSets(gearSets, newItem));
            }
        }

        // 同一個 ID 的格子共用同一組陣列（GearSetMembership 的契約：不可原地修改）。
        private static void ApplyGearSets(GearSetMembership gearSets, InventoryItem newItem)
        {
            if (gearSets.TryGet(newItem.ItemId, out var gearSetIds, out var gearSetNames) ||
                gearSets.TryGet(newItem.ItemId + 1_000_000, out gearSetIds, out gearSetNames))
            {
                newItem.GearSets = gearSetIds;
                newItem.GearSetNames = gearSetNames;
            }
            else
            {
                newItem.GearSets = Array.Empty<uint>();
            }
        }

        private void GenerateEquippedItems(Inventory inventory, List<InventoryChange> inventoryChanges)
        {
            if (_inventoryScanner.InMemory.Contains(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems))
            {
                var bag1 = _inventoryScanner.CharacterEquipped;
                var gearSets = _inventoryScanner.GetGearSetMembership();
                inventory.LoadGameItems(bag1, InventoryType.GearSet0, InventoryCategory.CharacterEquipped, false, inventoryChanges,
                    (newItem,_) => ApplyGearSets(gearSets, newItem));
            }
        }

        private void GenerateFreeCompanyInventories(List<InventoryChange> inventoryChanges)
        {
            var freeCompanyId = _characterMonitor.ActiveFreeCompanyId;
            if (freeCompanyId == 0) return;

            if (!_inventories.ContainsKey(freeCompanyId))
            {
                _inventories[freeCompanyId] = _inventoryFactory.Invoke(CharacterType.FreeCompanyChest, freeCompanyId);
            }

            var inventory = _inventories[freeCompanyId];

            HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType> inventoryTypes = new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>();
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.FreeCompanyPage1);
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.FreeCompanyPage2);
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.FreeCompanyPage3);
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.FreeCompanyPage4);
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.FreeCompanyPage5);
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.FreeCompanyCrystals);
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.FreeCompanyGil);
            inventoryTypes.Add( (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)InventoryType.FreeCompanyCurrency);
            foreach (var inventoryType in inventoryTypes)
            {
                if (!_inventoryScanner.InMemory.Contains(inventoryType))
                {
                    continue;
                }

                var inventoryCategory = inventoryType.Convert().ToInventoryCategory();
                var items = _inventoryScanner.GetInventoryByType(inventoryType);
                inventory.LoadGameItems(items,inventoryType.Convert(), inventoryCategory, false, inventoryChanges);
            }
        }

        private Dictionary<InventoryCategory, HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>> _housingMap =
            new()
            {
                {InventoryCategory.HousingInteriorItems, new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>()
                {
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorPlacedItems1,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorPlacedItems2,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorPlacedItems3,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorPlacedItems4,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorPlacedItems5,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorPlacedItems6,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorPlacedItems7,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorPlacedItems8
                }},
                {InventoryCategory.HousingInteriorAppearance, new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>()
                {
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorAppearance
                }},
                {InventoryCategory.HousingInteriorStoreroom, new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>()
                {
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorStoreroom1,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorStoreroom2,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorStoreroom3,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorStoreroom4,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorStoreroom5,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorStoreroom6,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorStoreroom7,
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingInteriorStoreroom8
                }},
                {InventoryCategory.HousingExteriorItems, new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>()
                {
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingExteriorPlacedItems
                }},
                {InventoryCategory.HousingExteriorAppearance, new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>()
                {
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingExteriorAppearance
                }},
                {InventoryCategory.HousingExteriorStoreroom, new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>()
                {
                    FFXIVClientStructs.FFXIV.Client.Game.InventoryType.HousingExteriorStoreroom
                }},
            };

        private void GenerateHousingInventories(List<InventoryChange> inventoryChanges)
        {
            var activeHouseId = _characterMonitor.ActiveHouseId;
            if (activeHouseId == 0) return;
            var house = _characterMonitor.GetCharacterById(activeHouseId);
            if (house == null)
            {
                return;
            }

            if (!_inventories.ContainsKey(activeHouseId))
            {
                _inventories[activeHouseId] = _inventoryFactory.Invoke(CharacterType.Housing, activeHouseId);
            }

            var inventory = _inventories[activeHouseId];

            var plotSize = house.GetPlotSize();

            foreach (var housingMap in _housingMap)
            {
                var totalMaxItems = 100;
                switch (housingMap.Key)
                {
                    case InventoryCategory.HousingInteriorItems:
                    case InventoryCategory.HousingInteriorStoreroom:
                        totalMaxItems = plotSize.GetInternalSlots();
                        break;
                    case InventoryCategory.HousingExteriorItems:
                    case InventoryCategory.HousingExteriorStoreroom:
                        totalMaxItems = plotSize.GetExternalSlots();
                        break;

                }

                HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType> inventoryTypes = housingMap.Value;
                foreach (var inventoryType in inventoryTypes)
                {
                    if (!_inventoryScanner.InMemory.Contains(inventoryType))
                    {
                        continue;
                    }

                    var inventoryCategory = inventoryType.Convert().ToInventoryCategory();
                    var items = _inventoryScanner.GetInventoryByType(inventoryType).Take(totalMaxItems).ToArray();
                    inventory.LoadGameItems(items, inventoryType.Convert(), inventoryCategory,false, inventoryChanges);
                }
            }
        }

        private unsafe void GenerateRetainerInventories(List<InventoryChange> inventoryChanges)
        {
            var currentRetainer = _characterMonitor.ActiveRetainerId;
            HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType> inventoryTypes = new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>();
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage1);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage2);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage3);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage4);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerPage5);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerEquippedItems);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerMarket);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerCrystals);
            inventoryTypes.Add(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.RetainerGil);
            if (currentRetainer != 0)
            {
                if (!_inventoryScanner.InMemoryRetainers.ContainsKey(currentRetainer))
                {
                    _pluginLog.Debug("Inventory scanner does not have information about this retainer.");
                    return;
                }
                foreach (var inventoryType in inventoryTypes)
                {
                    if (!_inventoryScanner.InMemoryRetainers[currentRetainer].Contains(inventoryType))
                    {
                        _pluginLog.Debug("Inventory scanner does not have information about a retainer's " + inventoryType.ToString());
                        return;
                    }
                }
                if (!_inventories.ContainsKey(currentRetainer))
                {
                    _inventories[currentRetainer] = _inventoryFactory.Invoke(CharacterType.Retainer, currentRetainer);
                }

                var inventory = _inventories[currentRetainer];
                _pluginLog.Verbose("Retainer inventory found in scanner, loading into inventory monitor.");
                foreach (var inventoryType in inventoryTypes)
                {
                    var items = _inventoryScanner.GetInventoryByType(currentRetainer,inventoryType);
                    var inventoryCategory = inventoryType.Convert().ToInventoryCategory();
                    inventory.LoadGameItems(items, inventoryType.Convert(), inventoryCategory, false, inventoryChanges,
                        (newItem, index) =>
                        {
                            if (index >= 0 && index < _inventoryScanner.RetainerMarketPrices[currentRetainer].Length)
                            {
                                if (newItem.ItemId != 0)
                                {
                                    newItem.RetainerMarketPrice =
                                        _inventoryScanner.RetainerMarketPrices[currentRetainer][index];
                                }
                            }
                        });
                }
            }
        }

        private void GenerateArmoireInventories(Inventory inventory, List<InventoryChange> inventoryChanges)
        {
            HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType> inventoryTypes = new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>();
            inventoryTypes.Add( (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)InventoryType.Armoire);
            foreach (var inventoryType in inventoryTypes)
            {
                if (!_inventoryScanner.InMemory.Contains(inventoryType))
                {
                    return;
                }
            }

            foreach (var inventoryType in inventoryTypes)
            {
                var items = _inventoryScanner.GetInventoryByType(inventoryType);
                var inventoryCategory = inventoryType.Convert().ToInventoryCategory();
                inventory.LoadGameItems(items, inventoryType.Convert(), inventoryCategory, false, inventoryChanges);
            }

        }
        private void GenerateCurrencyInventories(Inventory inventory, List<InventoryChange> inventoryChanges)
        {
            HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType> inventoryTypes = new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>();
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Currency);
            foreach (var inventoryType in inventoryTypes)
            {
                if (!_inventoryScanner.InMemory.Contains(inventoryType))
                {
                    return;
                }
            }

            foreach (var inventoryType in inventoryTypes)
            {
                var items = _inventoryScanner.GetInventoryByType(inventoryType);
                var inventoryCategory = inventoryType.Convert().ToInventoryCategory();
                inventory.LoadGameItems(items, inventoryType.Convert(), inventoryCategory, false, inventoryChanges);
            }
        }
        private void GenerateCrystalInventories(Inventory inventory, List<InventoryChange> inventoryChanges)
        {
            HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType> inventoryTypes = new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>();
            inventoryTypes.Add( FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Crystals);
            foreach (var inventoryType in inventoryTypes)
            {
                if (!_inventoryScanner.InMemory.Contains(inventoryType))
                {
                    return;
                }
            }

            foreach (var inventoryType in inventoryTypes)
            {
                var items = _inventoryScanner.GetInventoryByType(inventoryType);
                var inventoryCategory = inventoryType.Convert().ToInventoryCategory();
                inventory.LoadGameItems(items, inventoryType.Convert(), inventoryCategory, false, inventoryChanges);
            }
        }

        private void GenerateGlamourInventories(Inventory inventory, List<InventoryChange> inventoryChanges)
        {
            HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType> inventoryTypes = new HashSet<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>();
            inventoryTypes.Add( (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)InventoryType.GlamourChest);
            foreach (var inventoryType in inventoryTypes)
            {
                if (!_inventoryScanner.InMemory.Contains(inventoryType))
                {
                    _pluginLog.Verbose("in memory does not contain glamour");
                    return;
                }
            }

            foreach (var inventoryType in inventoryTypes)
            {
                var items = _inventoryScanner.GetInventoryByType(inventoryType);
                var inventoryCategory = InventoryCategory.GlamourChest;
                inventory.LoadGameItems(items, inventoryType.Convert(), inventoryCategory, false, inventoryChanges);
            }
        }


        private bool _disposed;
        private bool _started;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if(!_disposed && disposing)
            {
                if (_inventoryScanner != null) _inventoryScanner.BagsChanged -= InventoryScannerOnBagsChanged;
                if (_characterMonitor != null) _characterMonitor.OnCharacterRemoved -= CharacterMonitorOnOnCharacterRemoved;
            }
            // 正在跑的那一輪不等它：跑完不再送事件、也不再補跑（見 GenerateInventoriesLoop）。
            Volatile.Write(ref _disposed, true);
            _disposeCts.Cancel();
        }

        public class ItemChanges
        {
            public List<ItemChangesItem> NewItems;
            public List<ItemChangesItem> RemovedItems;

            public ItemChanges(List<ItemChangesItem> newItems, List<ItemChangesItem> removedItems)
            {
                NewItems = newItems;
                RemovedItems = removedItems;
            }
        }

        public class ItemChangesItem
        {
            public int Quantity;
            public uint ItemId;
            public ulong CharacterId;
            public ItemFlags Flags;
            public DateTime Date;
        }
    }
}
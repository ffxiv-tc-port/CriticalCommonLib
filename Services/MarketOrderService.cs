using System;
using System.Collections.Generic;
using System.Linq;
using AllaganLib.GameSheets.Sheets;
using AllaganLib.GameSheets.Sheets.Rows;
using Autofac.Core;
using CriticalCommonLib.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CriticalCommonLib.Services;

public class MarketOrderService : IMarketOrderService
{
    private readonly IGameGui gameGui;
    private readonly ItemSheet _itemSheet;

    public MarketOrderService(IGameGui gameGui, ItemSheet itemSheet)
    {
        this.gameGui = gameGui;
        _itemSheet = itemSheet;
    }

    /// <summary>
    /// Returns a dictionary that maps the inventory slots to menu indexes, if the slot is missing then it can be assumed there is no item in the list
    /// </summary>
    /// <returns>An array of slot IDs</returns>
    public unsafe Dictionary<int, int>? GetCurrentOrder()
    {
        var retainerSellListPtr = this.gameGui.GetAddonByName("RetainerSellList");
        if (retainerSellListPtr == IntPtr.Zero)
        {
            return null;
        }

        var retainerSellList = (AtkUnitBase*)retainerSellListPtr.Address;
        var atkValues = retainerSellList->AtkValues;
        if (atkValues == null)
        {
            return null;
        }

        var currentOrder = new Dictionary<int, int>();
        var atkIndex = 15;
        for (var i = 0; i < 20; i++)
        {
            if (atkValues[atkIndex].Type == 0)
            {
                continue;
            }

            currentOrder.TryAdd(atkValues[atkIndex].Int, i);
            atkIndex += 13;
        }

        return currentOrder;
    }

    public IEnumerable<InventoryItem> SortByBackupRetainerMarketOrder(IEnumerable<InventoryItem> item)
    {
        return item.OrderBy(c => c.Item.Base.ItemUICategory.ValueNullable?.OrderMajor ?? 0)
            .ThenBy(c => c.Item.Base.ItemUICategory.ValueNullable?.OrderMinor ?? 0)
            .ThenBy(c => c.Flags == FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.None ? 0 : 1)
            .ThenBy(c => c.Item.RowId);
    }
    public IEnumerable<FFXIVClientStructs.FFXIV.Client.Game.InventoryItem> SortByBackupRetainerMarketOrder(IEnumerable<FFXIVClientStructs.FFXIV.Client.Game.InventoryItem> item)
    {
        return item.OrderBy(c => GetItem(c)?.Base.ItemUICategory.ValueNullable?.OrderMajor ?? 0)
            .ThenBy(c => GetItem(c)?.Base.ItemUICategory.ValueNullable?.OrderMinor ?? 0)
            .ThenBy(c => c.Flags == FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.None ? 0 : 1)
            .ThenBy(c => GetItem(c)?.RowId);
    }

    public IEnumerable<FFXIVClientStructs.FFXIV.Client.Game.InventoryItem> SortByRetainerMarketOrder(
        IEnumerable<FFXIVClientStructs.FFXIV.Client.Game.InventoryItem> item)
    {
        return item.OrderBy(c => c.ItemId == 0)
            .ThenBy(c => GetItem(c)?.Base.ItemUICategory.ValueNullable?.OrderMajor ?? 999)
            .ThenBy(c => GetItem(c)?.Base.ItemUICategory.ValueNullable?.OrderMinor ?? 999)
            .ThenBy(c => c.Flags == FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.None ? 0 : 1)
            .ThenBy(c => GetItem(c)?.RowId);
    }

    public void SortByRetainerMarketOrderInPlace(Span<FFXIVClientStructs.FFXIV.Client.Game.InventoryItem> items)
    {
        // 結果必須與 SortByRetainerMarketOrder 逐筆相同：同一組鍵、穩定（同鍵保留原順序）、RowId 為 null 排在有值之前。
        Span<MarketSortKey> keys = items.Length <= 64 ? stackalloc MarketSortKey[items.Length] : new MarketSortKey[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var c = items[i];
            var row = GetItem(c);
            keys[i] = new MarketSortKey(
                c.ItemId == 0,
                row?.Base.ItemUICategory.ValueNullable?.OrderMajor ?? 999,
                row?.Base.ItemUICategory.ValueNullable?.OrderMinor ?? 999,
                c.Flags == FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.None ? 0 : 1,
                row?.RowId);
        }

        // 插入排序只在後者嚴格較小時往前移 ⇒ 穩定；出售欄只有 20 格。
        for (var i = 1; i < items.Length; i++)
        {
            var key = keys[i];
            var item = items[i];
            var j = i - 1;
            while (j >= 0 && keys[j].CompareTo(key) > 0)
            {
                keys[j + 1] = keys[j];
                items[j + 1] = items[j];
                j--;
            }

            keys[j + 1] = key;
            items[j + 1] = item;
        }
    }

    private readonly struct MarketSortKey
    {
        private readonly bool _empty;
        private readonly int _major;
        private readonly int _minor;
        private readonly int _flags;
        private readonly bool _hasRow;
        private readonly uint _rowId;

        public MarketSortKey(bool empty, int major, int minor, int flags, uint? rowId)
        {
            _empty = empty;
            _major = major;
            _minor = minor;
            _flags = flags;
            _hasRow = rowId.HasValue;
            _rowId = rowId.GetValueOrDefault();
        }

        public int CompareTo(in MarketSortKey other)
        {
            var c = _empty.CompareTo(other._empty);
            if (c != 0) return c;
            c = _major.CompareTo(other._major);
            if (c != 0) return c;
            c = _minor.CompareTo(other._minor);
            if (c != 0) return c;
            c = _flags.CompareTo(other._flags);
            if (c != 0) return c;
            // 與 Comparer<uint?>.Default 相同：null 小於任何值、兩個 null 相等。
            if (_hasRow != other._hasRow) return _hasRow ? 1 : -1;
            return _hasRow ? _rowId.CompareTo(other._rowId) : 0;
        }
    }

    private ItemRow? GetItem(FFXIVClientStructs.FFXIV.Client.Game.InventoryItem inventoryItem)
    {
        if (inventoryItem.ItemId == 0)
        {
            return null;
        }

        return _itemSheet.GetRow(inventoryItem.ItemId);
    }
}

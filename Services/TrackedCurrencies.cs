using System;
using AllaganLib.GameSheets.Sheets.Rows;

namespace CriticalCommonLib.Services;

/// <summary>掃進「貨幣」容器的物品；持有量一律經 <c>InventoryManager.GetInventoryItemCount</c> 讀。</summary>
public static class TrackedCurrencies
{
    /// <summary>
    /// 物品分類不是「貨幣」、但遊戲當貨幣用的。🔴 只能往尾端加（槽位接在舊判定之後），加了就把 <see cref="ExtraSetVersion"/> 加 1。
    /// </summary>
    public static readonly uint[] ExtraItemIds = [45690, 45691, 48146, 45043, 45044, 37549, 37550, 31135, 41668];

    public const int ExtraSetVersion = 1;

    /// <summary>舊判定（不含 Gil）；Currency 容器前段槽位照這個條件、依物品表順序排。</summary>
    public static bool IsBaseCurrency(ItemRow item) =>
        item.RowId is >= 20 and <= 60 && item.Base.FilterGroup == 16 || item.Base.ItemUICategory.RowId == 100;

    public static bool IsExtra(uint itemId) => Array.IndexOf(ExtraItemIds, itemId) >= 0;

    public static bool IsTracked(ItemRow item) => item.RowId != 1 && (IsBaseCurrency(item) || IsExtra(item.RowId));
}

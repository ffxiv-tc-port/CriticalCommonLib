using System;
using AllaganLib.GameSheets.Sheets.Rows;

namespace CriticalCommonLib.Services;

/// <summary>貨幣兌換排行認得的貨幣；「貨幣」容器只放舊判定＋<see cref="ExtraItemIds"/>（經 <c>InventoryManager.GetInventoryItemCount</c> 讀）。</summary>
public static class TrackedCurrencies
{
    /// <summary>
    /// 物品分類不是「貨幣」、但遊戲當貨幣用的。🔴 只能往尾端加（槽位接在舊判定之後），加了就把 <see cref="ExtraSetVersion"/> 加 1。
    /// </summary>
    public static readonly uint[] ExtraItemIds = [45690, 45691, 48146, 45043, 45044, 37549, 37550, 31135, 41668];

    public const int ExtraSetVersion = 1;

    /// <summary>放在背包格的貨幣（排行直接數背包）。不可放進「貨幣」容器，否則搜尋與合計會和背包重複算。</summary>
    public static readonly uint[] BagItemIds = [46322, 46179, 33329, 22443, 22444, 26823, 33870, 45040, 46979];

    /// <summary>舊判定（不含 Gil）；Currency 容器前段槽位照這個條件、依物品表順序排。</summary>
    public static bool IsBaseCurrency(ItemRow item) =>
        item.RowId is >= 20 and <= 60 && item.Base.FilterGroup == 16 || item.Base.ItemUICategory.RowId == 100;

    public static bool IsExtra(uint itemId) => Array.IndexOf(ExtraItemIds, itemId) >= 0;

    public static bool IsBagCurrency(uint itemId) => Array.IndexOf(BagItemIds, itemId) >= 0;

    public static bool IsTracked(ItemRow item) => item.RowId != 1 && (IsBaseCurrency(item) || IsExtra(item.RowId) || IsBagCurrency(item.RowId));
}

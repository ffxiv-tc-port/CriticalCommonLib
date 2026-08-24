using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CriticalCommonLib.MarketBoard
{
    public interface IMarketCache : IDisposable
    {
        /// <summary>
        /// 目前的服務區是否有線上市場資料來源(universalis)。台服(繁中服)為 false。
        /// 為 false 時不會送出任何查價請求,顯示端應該把價格畫成「沒有資料」,而不是畫成 0
        /// (會誤導)或永遠不會結束的「loading...」。
        /// </summary>
        bool MarketDataAvailable { get; }

        void LoadExistingCache();
        void ClearCache();
        void SaveCache(bool forceSave = false);
        MarketPricing? GetPricing(uint itemId, uint worldId, bool forceCheck);
        MarketCachePricingResult GetPricing(uint itemId, uint worldId, bool ignoreCache, bool forceCheck, out MarketPricing? pricing);
        List<MarketPricing> GetPricing(uint itemId, List<uint> worldIds, bool forceCheck);
        List<MarketPricing> GetPricing(uint itemId, bool forceCheck);

        ConcurrentDictionary<(uint, uint), MarketPricing> CachedPricing { get; }
        /// <summary>
        ///
        /// </summary>
        /// <param name="itemId"></param>
        /// <param name="worldId"></param>
        /// <returns>Was the request successful</returns>
        bool RequestCheck(uint itemId, uint worldId, bool forceCheck);
        void RequestCheck(List<uint> itemIds, List<uint> worldIds, bool forceCheck);
        void RequestCheck(List<uint> itemIds, uint worldId, bool forceCheck);
        void RequestCheck(uint itemId, List<uint> worldIDs, bool forceCheck);
    }

    public enum MarketCachePricingResult
    {
        Untradable,
        Queued,
        AlreadyQueued,
        Successful,
        NoPricing,
        Disabled
    }
}
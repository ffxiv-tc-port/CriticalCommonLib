using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CriticalCommonLib.MarketBoard
{
    public interface IMarketCache : IDisposable
    {
        /// <summary>
        /// 目前是否還有任何世界查得到線上市場資料(universalis)。逐個世界執行期判定,
        /// 不按客戶端語言一刀切——universalis 收得到台服(繁中服)8 個世界的資料。
        /// 為 false 時代表試過的世界全部被回 404,顯示端應該把價格畫成「沒有資料」,
        /// 而不是畫成 0(會誤導)或永遠不會結束的「loading...」。
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
using System.Collections.Generic;

#pragma warning disable 8618

namespace CriticalCommonLib.MarketBoard
{
    public class MultiRequest
    {
        public string[] itemIDs { internal get; set; }
        public Dictionary<string,PricingAPIResponse>? items { internal get; set; }
    }

    public class PricingAPIResponse
    {
        public uint itemID { internal get; set; }
        public float averagePriceNQ { get; set; }
        public float averagePriceHQ { get; set; }

        /// <summary>
        /// 掛售算出來的均價。與 averagePrice* 不同,它不需要成交歷史,
        /// 所以帶 entries=0 查價時它仍然是有效值。
        /// </summary>
        public float currentAveragePriceNQ { get; set; }

        /// <summary>
        /// 見 <see cref="currentAveragePriceNQ"/>。
        /// </summary>
        public float currentAveragePriceHQ { get; set; }
        public float minPriceNQ { get; set; }
        public float minPriceHQ { get; set; }
        public RecentHistory[]? recentHistory;
        public Listing[]? listings;
    }

    /// <summary>
    /// <c>/api/v2/history/...</c> 多筆查詢的回應。
    ///
    /// ⚠️ 與掛售端點不同:這個端點會把「沒有成交資料」的道具整筆省略,
    ///    不是回一個空物件 ⇒ 對照時一律用 TryGetValue,不要假設鍵在。
    /// </summary>
    public class HistoryMultiRequest
    {
        public Dictionary<string, HistoryAPIResponse>? items { get; set; }
    }

    /// <summary>
    /// 單一道具的成交歷史。欄位名照 API 的小寫拼法。
    /// </summary>
    public class HistoryAPIResponse
    {
        public uint itemID { get; set; }
        public RecentHistory[]? entries { get; set; }
    }

    public class Stacksizehistogram
    {
        public int _1 { get; set; }
    }

    public class Stacksizehistogramnq
    {
        public int _1 { get; set; }
    }

    public class Stacksizehistogramhq
    {
        public int _1 { get; set; }
    }

    public class Listing
    {

        public int lastReviewTime { get; set; }

        public int pricePerUnit { get; set; }

        public int quantity { get; set; }

        public int stainID { get; set; }

        public string creatorName { get; set; }

        public object creatorID { get; set; }

        public bool hq { get; set; }

        public bool isCrafted { get; set; }

        public object listingID { get; set; }

        public object[] materia { get; set; }

        public bool onMannequin { get; set; }

        public int retainerCity { get; set; }

        public string retainerID { get; set; }

        public string retainerName { get; set; }

        public string sellerID { get; set; }

        public int total { get; set; }
    }

    public class RecentHistory
    {
        public bool hq { get; set; }
        public int pricePerUnit { get; set; }
        public int quantity { get; set; }
        public int timestamp { get; set; }
        public string buyerName { get; set; }
        public int total { get; set; }
    }

}

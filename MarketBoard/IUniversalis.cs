using System;

namespace CriticalCommonLib.MarketBoard
{
    /// <summary>查價完成時送出。一律在框架執行緒上叫用。</summary>
    public delegate void ItemPriceRetrievedDelegate(uint itemId, uint worldId, MarketPricing response);

    public interface IUniversalis : IDisposable
    {
        event ItemPriceRetrievedDelegate? ItemPriceRetrieved;
        int QueuedCount { get; }
        void SetSaleHistoryLimit(int limit);
        void QueuePriceCheck(uint itemId, uint worldId);
        public DateTime? LastFailure { get; }
        public bool TooManyRequests { get; }
    }
}
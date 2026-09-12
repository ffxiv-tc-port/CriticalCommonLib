using CriticalCommonLib.Models;
using Dispatch;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using CriticalCommonLib.Extensions;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

#pragma warning disable SYSLIB0014
#pragma warning disable 8618

namespace CriticalCommonLib.MarketBoard
{
    public class Universalis : IUniversalis
    {
        private readonly IFramework _framework;
        private readonly ExcelSheet<World> _worldSheet;
        private readonly IPluginLog _pluginLog;
        private readonly UniversalisUserAgent _userAgent;
        private SerialQueue _apiRequestQueue = new SerialQueue();
        private List<IDisposable> _disposables = new List<IDisposable>();
        private Subject<(uint, uint)> _queuedItems = new Subject<(uint, uint)>();
        private Dictionary<uint, string> _worldNames = new();
        private bool _tooManyRequests;
        private bool _initialised;
        /// <summary>
        /// <c>NextRequestTime</c> 的存放處,存 Ticks + 1(0 = 沒有排定時間)。
        /// 🔴 SerialQueue 的工作執行緒寫、框架執行緒讀,都不拿鎖。
        /// </summary>
        private long _nextRequestTimeTicks;

        private DateTime? NextRequestTime
        {
            get
            {
                var ticks = Volatile.Read(ref _nextRequestTimeTicks);
                return ticks == 0 ? null : new DateTime(ticks - 1, DateTimeKind.Local);
            }
            set => Volatile.Write(ref _nextRequestTimeTicks,
                value is null ? 0L : value.Value.Ticks + 1);
        }

        private readonly int MaxBufferCount = 50;
        private readonly int BufferInterval = 1;
        private int _queuedCount;
        private int _saleHistoryLimit = 7;

        public Universalis(IFramework framework, ExcelSheet<World> worldSheet, IPluginLog pluginLog, UniversalisUserAgent userAgent)
        {
            _framework = framework;
            _worldSheet = worldSheet;
            _pluginLog = pluginLog;
            _userAgent = userAgent;
        }

        public delegate void ItemPriceRetrievedDelegate(uint itemId, uint worldId, MarketPricing response);

        public event ItemPriceRetrievedDelegate? ItemPriceRetrieved;

        public MarketPricing? RetrieveMarketBoardPrice(InventoryItem item, uint worldId)
        {
            if (!item.Item.SpentGilShop)
            {
                return new MarketPricing();
            }
            return RetrieveMarketBoardPrice(item.ItemId, worldId);
        }

        public void SetSaleHistoryLimit(int limit)
        {
            _saleHistoryLimit = limit;
        }

        public void Initialise()
        {
            _pluginLog.Verbose("Setting up universalis buffer.");
            _initialised = true;
            var stepInterval = _queuedItems
                .Buffer(TimeSpan.FromSeconds(BufferInterval), MaxBufferCount)
                .StepInterval(TimeSpan.FromSeconds(BufferInterval));

            _disposables.Add(_queuedItems.Subscribe(_ => _queuedCount++));

            _disposables.Add(stepInterval
                .Subscribe(x =>
                {
                    var itemIds = x.ToList();
                    var queuedCount = itemIds.Count();
                    _queuedCount -= queuedCount;
                    // 只讀一次:分兩次讀的話判斷與取值會落在不同的值上。
                    var nextRequestTime = NextRequestTime;
                    if (_tooManyRequests && nextRequestTime != null && nextRequestTime.Value <= DateTime.Now)
                    {
                        _tooManyRequests = false;
                        NextRequestTime = null;
                    }
                    itemIds = itemIds.Distinct().ToList();
                    if (itemIds.Any())
                    {
                        var byWorld = itemIds.GroupBy(c => c.Item2);
                        foreach (var itemWorldId in byWorld)
                        {
                            RetrieveMarketBoardPrices(itemWorldId.Select(c => c.Item1).ToList(), itemWorldId.Key);
                        }
                    }
                }));
        }

        public void QueuePriceCheck(uint itemId, uint worldId)
        {
            if (!_initialised)
            {
                Initialise();
            }

            if (itemId != 0 && worldId != 0)
            {
                _queuedItems.OnNext((itemId, worldId));
            }
        }

        /// <summary>存放處,存 Ticks + 1(0 = 還沒失敗過)。理由同 HostedUniversalis。</summary>
        private long _lastFailureTicks;

        /// <summary>
        /// 最近一次查價失敗的時間。工作執行緒寫、繪製執行緒讀,兩邊都不拿鎖。
        /// </summary>
        public DateTime? LastFailure
        {
            get
            {
                var ticks = Volatile.Read(ref _lastFailureTicks);
                return ticks == 0 ? null : new DateTime(ticks - 1, DateTimeKind.Local);
            }
            private set => Volatile.Write(ref _lastFailureTicks,
                value is null ? 0L : value.Value.Ticks + 1);
        }

        public bool TooManyRequests => _tooManyRequests;

        public int QueuedCount
        {
            get
            {
                return _queuedCount;
            }
        }

        public int SaleHistoryLimit => _saleHistoryLimit;

        public void RetrieveMarketBoardPrices(IEnumerable<uint> itemIds, uint worldId)
        {
            if (worldId == 0)
            {
                return;
            }
            if (_tooManyRequests)
            {
                _pluginLog.Debug("Too many requests, readding items.");
                foreach (var itemId in itemIds)
                {
                    _queuedItems.OnNext((itemId, worldId));
                }

                return;
            }

            string worldName;
            if (!_worldNames.ContainsKey(worldId))
            {
                var world = _worldSheet.GetRowOrDefault(worldId);
                if (world == null)
                {
                    return;
                }

                _worldNames[worldId] = world.Value.Name.ExtractText();
            }
            worldName = _worldNames[worldId];

            if (itemIds.Count() == 1)
            {
                var dispatch = _apiRequestQueue.DispatchAsync(() =>
                {
                    var itemId = itemIds.First();
                    string url = $"https://universalis.app/api/{worldName}/{itemId}?listings=20&entries=20";

                    HttpWebRequest request = (HttpWebRequest) WebRequest.Create(url);
                    request.Headers["User-Agent"] = $"{_userAgent.PluginName}/{_userAgent.PluginVersion}";
                    request.AutomaticDecompression = DecompressionMethods.GZip;

                    try
                    {
                        using (WebResponse response = request.GetResponse())
                        {
                            HttpWebResponse webresponse = (HttpWebResponse) response;

                            if (webresponse.StatusCode == HttpStatusCode.TooManyRequests)
                            {
                                _pluginLog.Warning("Universalis: too many requests!");
                                _tooManyRequests = true;
                                // sleep for 1 minute if too many requests
                                Thread.Sleep(60000);

                                request = (HttpWebRequest) WebRequest.Create(url);
                                request.Headers["User-Agent"] = $"{_userAgent.PluginName}/{_userAgent.PluginVersion}";
                                webresponse = (HttpWebResponse) request.GetResponse();
                            }
                            _tooManyRequests = false;

                            var reader = new StreamReader(webresponse.GetResponseStream());
                            var value = reader.ReadToEnd();
                            PricingAPIResponse? apiListing = JsonConvert.DeserializeObject<PricingAPIResponse>(value);

                            if (apiListing != null)
                            {
                                var listing = MarketPricing.FromApi(apiListing, worldId, SaleHistoryLimit);
                                _framework.RunOnFrameworkThread(() =>
                                    ItemPriceRetrieved?.Invoke(apiListing.itemID, worldId, listing));
                            }
                            else
                            {
                                LastFailure = DateTime.Now;
                                _pluginLog.Error("Universalis: Failed to parse universalis json data");
                            }

                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Debug(ex.ToString());
                    }
                });
                _disposables.Add(dispatch);
            }
            else
            {
                var dispatch = _apiRequestQueue.DispatchAsync(() =>
                {
                    var itemIdsString = String.Join(",", itemIds.Select(c => c.ToString()).ToArray());
                    _pluginLog.Verbose($"Sending request for items {itemIdsString} to universalis API.");
                    string url =
                        $"https://universalis.app/api/v2/{worldName}/{itemIdsString}?listings=20&entries=20";

                    HttpWebRequest request = (HttpWebRequest) WebRequest.Create(url);
                    request.Headers["User-Agent"] = $"{_userAgent.PluginName}/{_userAgent.PluginVersion}";
                    request.AutomaticDecompression = DecompressionMethods.GZip;

                    try
                    {
                        using (WebResponse response = request.GetResponse())
                        {

                            HttpWebResponse webresponse = (HttpWebResponse) response;

                            if (webresponse.StatusCode == HttpStatusCode.TooManyRequests)
                            {
                                _pluginLog.Warning("Universalis: too many requests!");
                                NextRequestTime = DateTime.Now.AddMinutes(1);
                                _tooManyRequests = true;
                            }

                            var reader = new StreamReader(webresponse.GetResponseStream());
                            var value = reader.ReadToEnd();
                            MultiRequest? multiRequest = JsonConvert.DeserializeObject<MultiRequest>(value);


                            if (multiRequest != null && multiRequest.items != null)
                            {
                                foreach (var item in multiRequest.items.Select(c => c.Value))
                                {
                                    var listing = MarketPricing.FromApi(item, worldId, SaleHistoryLimit);
                                    _framework.RunOnFrameworkThread(() =>
                                        ItemPriceRetrieved?.Invoke(item.itemID, worldId, listing));
                                }
                            }
                            else
                            {
                                LastFailure = DateTime.Now;
                                _pluginLog.Verbose("Universalis: could not parse multi request json data");
                            }

                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Debug(ex.ToString());
                    }
                });
                _disposables.Add(dispatch);
            }
        }

        public MarketPricing? RetrieveMarketBoardPrice(uint itemId, uint worldId)
        {
            if (worldId == 0)
            {
                return null;
            }
            string worldName;
            if (!_worldNames.ContainsKey(worldId))
            {
                var world = _worldSheet.GetRowOrDefault(worldId);
                if (world == null)
                {
                    return null;
                }

                _worldNames[worldId] = world.Value.Name.ExtractText();
            }
            worldName = _worldNames[worldId];

            var dispatch = _apiRequestQueue.DispatchAsync(() =>
                {
                    string url = $"https://universalis.app/api/{worldName}/{itemId}?listings=20&entries=20";

                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.Headers["User-Agent"] = $"{_userAgent.PluginName}/{_userAgent.PluginVersion}";
                    request.AutomaticDecompression = DecompressionMethods.GZip;

                    try
                    {
                        using (WebResponse response = request.GetResponse())
                        {
                            HttpWebResponse webresponse = (HttpWebResponse)response;

                            if (webresponse.StatusCode == HttpStatusCode.TooManyRequests)
                            {
                                _pluginLog.Warning("Universalis: too many requests!");
                                _tooManyRequests = true;
                                // sleep for 1 minute if too many requests
                                Thread.Sleep(60000);

                                request = (HttpWebRequest)WebRequest.Create(url);
                                request.Headers["User-Agent"] = $"{_userAgent.PluginName}/{_userAgent.PluginVersion}";
                                webresponse = (HttpWebResponse)request.GetResponse();
                            }

                            var reader = new StreamReader(webresponse.GetResponseStream());
                            var value = reader.ReadToEnd();
                            PricingAPIResponse? apiListing = JsonConvert.DeserializeObject<PricingAPIResponse>(value);

                            if (apiListing != null)
                            {
                                var listing = MarketPricing.FromApi(apiListing, worldId, SaleHistoryLimit);
                                _framework.RunOnFrameworkThread(() =>
                                    ItemPriceRetrieved?.Invoke(itemId, worldId, listing));
                            }
                            else
                            {
                                LastFailure = DateTime.Now;
                                _pluginLog.Verbose("Universalis: could not parse listing data json");
                            }

                            // Simple way to prevent too many requests
                            Thread.Sleep(500);
                        }
                    }
                    catch (Exception ex)
                    {
                        _pluginLog.Debug(ex.ToString() + ex.InnerException?.ToString());
                    }

                });
            _disposables.Add(dispatch);
            return null;
        }

        private bool _disposed;
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if(!_disposed && disposing)
            {
                _apiRequestQueue.Dispose();
                _queuedItems.Dispose();
                foreach (var disposable in _disposables)
                {
                    disposable.Dispose();
                }
            }
            _disposed = true;
        }
    }


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

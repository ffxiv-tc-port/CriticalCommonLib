using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AllaganLib.Shared.Interfaces;
using AllaganLib.Shared.Services;
using CriticalCommonLib.Interfaces;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CriticalCommonLib.MarketBoard;

public class HostedUniversalis : BackgroundService, IUniversalis
{
    private readonly UniversalisUserAgent _userAgent;
    private readonly ExcelSheet<World> _worldSheet;
    private readonly IFramework _framework;
    private readonly IHostedUniversalisConfiguration _hostedUniversalisConfiguration;

    /// <summary>
    /// 目前客戶端所在的服務區是否有 universalis 資料。台服(繁中服)為 false,整條線上查價
    /// 鏈路(排程、HTTP 請求、退避重試、錯誤紅字)全部不啟動。在建構時判定一次即可:
    /// IClientState.ClientLanguage 由 Dalamud 啟動參數決定,執行期不會變。
    /// </summary>
    public bool MarketApiAvailable { get; }

    public ILogger<HostedUniversalis> Logger { get; }
    public HttpClient HttpClient { get; }
    public BackgroundTaskQueue UniversalisQueue { get; }
    private Dictionary<uint, string> _worldNames = new();
    public uint QueueTime { get; } = 5;
    public uint MaxRetries { get; } = 3;
    public DateTime? LastFailure { get; private set; }
    public bool TooManyRequests { get; private set; }

    public int QueuedCount => _queuedCount;


    public HostedUniversalis(ILogger<HostedUniversalis> logger, UniversalisUserAgent userAgent, HttpClient httpClient, BackgroundTaskQueue.Factory taskQueueFactory, ExcelSheet<World> worldSheet, IFramework framework, IHostedUniversalisConfiguration hostedUniversalisConfiguration, IClientState clientState)
    {
        _userAgent = userAgent;
        _worldSheet = worldSheet;
        _framework = framework;
        _hostedUniversalisConfiguration = hostedUniversalisConfiguration;
        Logger = logger;
        HttpClient = httpClient;
        httpClient.DefaultRequestHeaders.Add("User-Agent", $"AllaganTools/{_userAgent.PluginVersion}");
        UniversalisQueue = taskQueueFactory.Invoke("Universalis Queue", 1);
        MarketApiAvailable = UniversalisAvailability.IsSupportedRegion(clientState);
        if (!MarketApiAvailable)
        {
            // 只在載入時講一次,不要每幀/每次查價都印。使用者的 LogLevel 是 2(Information),
            // 用 Information 才收得到。
            Logger.LogInformation(
                "偵測到繁體中文(台服)客戶端(ClientLanguage={ClientLanguage}),universalis 沒有台服的市場資料,已停用線上查價:不會送出任何 universalis 請求,也不會再出現 backing off 的錯誤訊息。",
                (int)clientState.ClientLanguage);
            return;
        }

        _framework.Update += FrameworkOnUpdate;
    }

    private void FrameworkOnUpdate(IFramework framework)
    {
        foreach (var world in _queueWorldItemIds)
        {
            if (world.Value.Item1 < DateTime.Now)
            {
                _queueWorldItemIds.Remove(world.Key, out var fullList);
                _queuedCount += fullList.Item2.Count;
                UniversalisQueue.QueueBackgroundWorkItemAsync(token => RetrieveMarketBoardPrices(fullList.Item2, world.Key,token));
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.LogTrace("Stopping service {Type} ({This})", GetType().Name, this);
        await base.StopAsync(cancellationToken);
        Logger.LogTrace("Stopped service {Type} ({This})", GetType().Name, this);
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!MarketApiAvailable)
        {
            // 台服:佇列永遠不會有東西進來,連背景排空迴圈都不用起。
            return;
        }

        await BackgroundProcessing(stoppingToken);
    }

    private async Task BackgroundProcessing(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var workItem =
                await UniversalisQueue.DequeueAsync(stoppingToken);

            try
            {
                await workItem(stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "Error occurred executing {WorkItem}.", nameof(workItem));
            }
        }
    }

    public event Universalis.ItemPriceRetrievedDelegate? ItemPriceRetrieved;
    public void SetSaleHistoryLimit(int limit)
    {
    }

    public void Initialise()
    {
    }

    public void QueuePriceCheck(uint itemId, uint worldId)
    {
        if (!MarketApiAvailable)
        {
            // 台服:直接丟掉,不排程也不記錄(這個方法會被表格的每列呼叫,記 log 會洗版)。
            return;
        }

        if (worldId == 0)
        {
            return;
        }
        _queueWorldItemIds.TryAdd(worldId, (DateTime.Now.AddSeconds(QueueTime), []));
        _queueWorldItemIds[worldId].Item2.Add(itemId);
        if (_queueWorldItemIds[worldId].Item2.Count == 50)
        {
            _queueWorldItemIds.Remove(worldId, out var fullList);
            _queuedCount += fullList.Item2.Count;
            UniversalisQueue.QueueBackgroundWorkItemAsync(token => RetrieveMarketBoardPrices(fullList.Item2, worldId,token));
        }
    }

    private ConcurrentDictionary<uint, (DateTime,HashSet<uint>)> _queueWorldItemIds = new();
    private int _queuedCount;


    public async Task RetrieveMarketBoardPrices(IEnumerable<uint> itemIds, uint worldId, CancellationToken token,uint attempt = 0)
    {
        if (!MarketApiAvailable)
        {
            // 台服:即使有人直接呼叫這個公開方法(繞過 QueuePriceCheck),也不送出請求。
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (worldId == 0)
        {
            return;
        }
        var itemIdList = itemIds.ToList();
        if (attempt == MaxRetries)
        {
            _queuedCount -= itemIdList.Count;
            Logger.LogError($"Maximum retries for universalis has been reached, cancelling.");
            return;
        }
        string worldName;
        if (!_worldNames.ContainsKey(worldId))
        {
            var world = _worldSheet.GetRowOrDefault(worldId);
            if (world == null)
            {
                _queuedCount -= itemIdList.Count;
                return;
            }

            _worldNames[worldId] = world.Value.Name.ExtractText();
        }
        worldName = _worldNames[worldId];

        var itemIdsString = String.Join(",", itemIdList.Select(c => c.ToString()).ToArray());
        Logger.LogTrace("Sending request for items {ItemIds} to universalis API.", itemIdsString);
        string url =
            $"https://universalis.app/api/v2/{worldName}/{itemIdsString}?listings=20&entries=20";
        try
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var response = await HttpClient.GetAsync(url, token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Logger.LogWarning("Too many requests to universalis, waiting a minute.");
                TooManyRequests = true;
                await Task.Delay(TimeSpan.FromMinutes(1), token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
                await RetrieveMarketBoardPrices(itemIdList, worldId, token, attempt + 1);
                return;
            }

            TooManyRequests = false;

            var value = await response.Content.ReadAsStringAsync(token);

            if (value == "error code: 504")
            {
                Logger.LogWarning("Gateway timeout to universalis, waiting 30 seconds.");
                LastFailure = DateTime.Now;
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                if (token.IsCancellationRequested)
                {
                    return;
                }
                await RetrieveMarketBoardPrices(itemIdList, worldId, token, attempt + 1);
                return;
            }

            if (itemIdList.Count == 1)
            {
                PricingAPIResponse? apiListing = JsonConvert.DeserializeObject<PricingAPIResponse>(value);

                if (apiListing != null)
                {
                    var listing = MarketPricing.FromApi(apiListing, worldId,
                        _hostedUniversalisConfiguration.SaleHistoryLimit);
                    _ = _framework.RunOnFrameworkThread(() =>
                        ItemPriceRetrieved?.Invoke(apiListing.itemID, worldId, listing));
                }
                else
                {
                    Logger.LogError("Failed to parse universalis json data, backing off 30 seconds.");
                    LastFailure = DateTime.Now;
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
            else
            {
                MultiRequest? multiRequest = JsonConvert.DeserializeObject<MultiRequest>(value);
                if (multiRequest != null && multiRequest.items != null)
                {
                    foreach (var item in multiRequest.items.Select(c => c.Value))
                    {
                        var listing = MarketPricing.FromApi(item, worldId,
                            _hostedUniversalisConfiguration.SaleHistoryLimit);
                        _ = _framework.RunOnFrameworkThread(() =>
                            ItemPriceRetrieved?.Invoke(item.itemID, worldId, listing));
                    }
                }
                else
                {
                    Logger.LogError("Failed to parse universalis multi request json data, backing off 30 seconds.");
                    LastFailure = DateTime.Now;
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }
        catch (TaskCanceledException)
        {

        }
        catch (JsonReaderException readerException)
        {
            Logger.LogError(readerException, "Failed to parse universalis data, backing off 30 seconds");
            LastFailure = DateTime.Now;
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            if (token.IsCancellationRequested)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogTrace(ex, "Unhandled exception in universalis");
        }
        _queuedCount -= itemIdList.Count;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _framework.Update -= FrameworkOnUpdate;
        }
    }

    public sealed override void Dispose()
    {
        Dispose(true);
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
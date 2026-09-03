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
    private readonly UniversalisAvailability _universalisAvailability;
    private readonly ConcurrentDictionary<uint, byte> _notFoundLoggedWorlds = new();

    /// <summary>
    /// 目前是否還有任何世界查得到 universalis 資料。判定細節在 UniversalisAvailability:
    /// 依據 universalis 自己的世界清單逐個世界判定,不再按客戶端語言一刀切。
    /// </summary>
    public bool MarketApiAvailable => _universalisAvailability.MarketDataAvailable;

    public ILogger<HostedUniversalis> Logger { get; }
    public HttpClient HttpClient { get; }
    public BackgroundTaskQueue UniversalisQueue { get; }
    private Dictionary<uint, string> _worldNames = new();
    public uint QueueTime { get; } = 5;
    public uint MaxRetries { get; } = 3;
    public DateTime? LastFailure { get; private set; }
    public bool TooManyRequests { get; private set; }

    public int QueuedCount => _queuedCount;


    public HostedUniversalis(ILogger<HostedUniversalis> logger, UniversalisUserAgent userAgent, HttpClient httpClient, BackgroundTaskQueue.Factory taskQueueFactory, ExcelSheet<World> worldSheet, IFramework framework, IHostedUniversalisConfiguration hostedUniversalisConfiguration, UniversalisAvailability universalisAvailability)
    {
        _userAgent = userAgent;
        _worldSheet = worldSheet;
        _framework = framework;
        _hostedUniversalisConfiguration = hostedUniversalisConfiguration;
        _universalisAvailability = universalisAvailability;
        Logger = logger;
        HttpClient = httpClient;
        httpClient.DefaultRequestHeaders.Add("User-Agent", $"AllaganTools/{_userAgent.PluginVersion}");
        UniversalisQueue = taskQueueFactory.Invoke("Universalis Queue", 1);
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
        await LoadKnownWorldsAsync(stoppingToken);
        await BackgroundProcessing(stoppingToken);
    }

    /// <summary>
    /// 取一次 universalis 認得的世界清單,拿它當「這個世界查不查得到」的判準。
    /// 取不到就什麼都不做:UniversalisAvailability 會維持樂觀模式照樣送請求,
    /// 行為與改動前相同。
    /// </summary>
    private async Task LoadKnownWorldsAsync(CancellationToken token)
    {
        try
        {
            var response = await HttpClient.GetAsync("https://universalis.app/api/v2/worlds", token);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogInformation(
                    "取得 universalis 世界清單失敗(HTTP {StatusCode}),本次改用樂觀模式:照樣送出查價請求。",
                    (int)response.StatusCode);
                return;
            }

            var value = await response.Content.ReadAsStringAsync(token);
            var worlds = JsonConvert.DeserializeObject<List<UniversalisWorld>>(value);
            if (worlds == null || worlds.Count == 0)
            {
                Logger.LogInformation(
                    "universalis 世界清單解析不出內容,本次改用樂觀模式:照樣送出查價請求。");
                return;
            }

            _universalisAvailability.SetKnownWorlds(worlds.Select(c => c.id));
            Logger.LogInformation(
                "universalis 認得 {Count} 個世界,查價會依這份清單過濾。",
                worlds.Count);
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogInformation(
                ex,
                "取得 universalis 世界清單時發生例外,本次改用樂觀模式:照樣送出查價請求。");
        }
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
        // universalis 認不得的世界(以及還沒登入、拿不到世界時的 worldId 0)直接丟掉,
        // 不排程也不記錄(這個方法會被表格的每一列呼叫,記 log 會洗版)。
        if (!_universalisAvailability.IsWorldSupported(worldId))
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
        if (token.IsCancellationRequested)
        {
            return;
        }

        var itemIdList = itemIds.ToList();

        // universalis 認不得的世界(以及 worldId 0)不送出請求。即使有人直接呼叫這個
        // 公開方法繞過 QueuePriceCheck 也一樣。
        if (!_universalisAvailability.IsWorldSupported(worldId))
        {
            _queuedCount -= itemIdList.Count;
            return;
        }
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

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // universalis 對「這批道具它一個都不認得」也回 404(不可交易的道具就會這樣),
                // 世界本身不一定有問題,所以這裡不動世界的可用性判定。這不是暫時性錯誤,重試
                // 不會變好,所以不進退避重試、也不印紅字,每個世界只寫一次 Information。
                if (_notFoundLoggedWorlds.TryAdd(worldId, default))
                {
                    Logger.LogInformation(
                        "universalis 對世界「{WorldName}」(id {WorldId})的查價回了 404(通常是該批道具不可交易或它不認得),已跳過,不重試。",
                        worldName, worldId);
                }

                _queuedCount -= itemIdList.Count;
                return;
            }

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

/// <summary>
/// universalis /api/v2/worlds 的一筆世界。欄位名刻意照 API 的小寫拼法,與這個資料夾裡
/// 其他的 API 回應型別(PricingAPIResponse 等)一致。
/// </summary>
public class UniversalisWorld
{
    public uint id { get; set; }

    public string? name { get; set; }
}
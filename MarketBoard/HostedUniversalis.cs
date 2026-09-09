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
    /// 已經吼過「請求形狀不對」的(世界 id, HTTP 狀態碼)。這一類重試不會變好,所以每種
    /// 只寫一次 Error,不進退避迴圈。
    /// </summary>
    private readonly ConcurrentDictionary<(uint, int), byte> _badRequestLoggedWorlds = new();

    /// <summary>
    /// 已經寫過「第一次查價成功」的世界。讓使用者在 log 裡看得到「有成功」,不只看得到失敗。
    /// </summary>
    private readonly ConcurrentDictionary<uint, byte> _successLoggedWorlds = new();

    /// <summary>
    /// 每個世界目前連續失敗幾次。成功一次就清掉,清掉的時候寫一則「已恢復」。
    /// </summary>
    private readonly ConcurrentDictionary<uint, int> _worldFailureStreak = new();

    /// <summary>
    /// 每個世界目前的批次上限。
    ///
    /// 🔴 這是這次修正的核心之一。2026-09-10 對台服世界「迦樓羅」(4029)逐位元組實測
    /// (.NET HttpClient,與外掛完全相同的請求形狀):
    ///   一次 50 筆 → 23 次裡 13 次拿到 HTTP 504(約 57%)
    ///   一次 30 筆 →  6 次裡  1 次
    ///   一次 20 筆 →  6 次裡  1 次
    ///   一次 10 筆 → 19 次裡  1 次(約 5%)
    /// ⚠️ 最小批不是零失敗,只是低很多——所以拆到最小之後仍然要有退避重試。
    /// 也就是「一次查太多筆」會讓 universalis 的原站來不及回應,邊界(Cloudflare)直接回
    /// 504。所以遇到暫時性錯誤時把批次調小再送,比原地等 30 秒然後整批丟掉有效得多。
    ///
    /// 預設值 MaxBatchSize 與改動前完全一樣(50),只有實際失敗過的世界才會被調小,
    /// 而且調小之後這個 session 內不再自動調回去(避免在 50 附近來回震盪、反覆吐警告)。
    /// 重載外掛就會回到 50。
    /// </summary>
    private readonly ConcurrentDictionary<uint, int> _worldBatchSize = new();

    /// <summary>
    /// 連續失敗太多次的世界,暫停到這個時間點為止。
    ///
    /// 沒有這個閘門的話,最壞情況(universalis 真的掛了)是 50 筆拆成 2x25、4x12、8x10,
    /// 八個最小批各自做兩輪退避,而這條佇列的並行度是 1 ⇒ 一批就能把佇列堵上半小時。
    /// 有了它,一個壞掉的世界最多送出 FailureStreakBeforeCooldown 次失敗請求就安靜下來。
    /// 成功一次就解除。
    /// </summary>
    private readonly ConcurrentDictionary<uint, DateTime> _worldCooldownUntil = new();

    /// <summary>
    /// 連續失敗幾次之後暫停這個世界的查價。
    /// </summary>
    private const int FailureStreakBeforeCooldown = 5;

    /// <summary>
    /// 暫停多久。
    /// </summary>
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(5);
    /// <summary>
    /// 一次最多查幾筆。維持改動前的行為。
    /// </summary>
    private const int MaxBatchSize = 50;

    /// <summary>
    /// 批次調小的下限。實測台服世界在這個大小下失敗率約 5%(不是零),所以拆到這個
    /// 大小之後就不再往下拆,改成指數退避重試。
    /// </summary>
    private const int MinBatchSize = 10;

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

    /// <summary>
    /// 同一批道具最多走幾層處理。拆批也算一層(拆批本身就是「換一個比較小的請求再試」),
    /// 所以這裡比改動前的 3 高一點,讓 50 → 25 → 12 → 10 這條路走得完。
    /// </summary>
    public uint MaxRetries { get; } = 5;

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
                Interlocked.Add(ref _queuedCount, fullList.Item2.Count);
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
        if (_queueWorldItemIds[worldId].Item2.Count == MaxBatchSize)
        {
            _queueWorldItemIds.Remove(worldId, out var fullList);
            Interlocked.Add(ref _queuedCount, fullList.Item2.Count);
            UniversalisQueue.QueueBackgroundWorkItemAsync(token => RetrieveMarketBoardPrices(fullList.Item2, worldId,token));
        }
    }

    private ConcurrentDictionary<uint, (DateTime,HashSet<uint>)> _queueWorldItemIds = new();
    private int _queuedCount;

    private void ReleaseQueued(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _queuedCount, -count);
        }
    }

    private int GetBatchSize(uint worldId)
    {
        return _worldBatchSize.TryGetValue(worldId, out var size) ? size : MaxBatchSize;
    }

    /// <summary>
    /// 這個 HTTP 狀態碼算不算「等一下再試就可能會好」。
    ///
    /// 5xx 一律算(含 Cloudflare 自己的 520/521/522/523/524——universalis 擋在 Cloudflare
    /// 後面,原站慢的時候回的是這些碼而不是 universalis 自己的錯誤)。
    /// 408/425 也算。其餘 4xx 都是「請求本身不對」,重試不會變好。
    /// </summary>
    private static bool IsTransientStatus(HttpStatusCode? statusCode)
    {
        if (statusCode == null)
        {
            // 連線失敗、DNS、TLS、逾時——都當成暫時性。
            return true;
        }

        var code = (int)statusCode.Value;
        return code == 408 || code == 425 || code >= 500;
    }

    /// <summary>
    /// 這段回應內容「看起來」是不是 JSON。
    ///
    /// 🔴 這個守衛是這次修正的另一個核心。改動前的碼是:
    ///       if (value == "error code: 504") { ...退避 30 秒... }
    ///    然後不管三七二十一把 value 丟給 Newtonsoft。
    ///    2026-09-10 逐位元組實測 universalis 對台服世界大批次查價的失敗回應:
    ///       HTTP 504,body = 16 bytes = "error code: 504" + 0x0A
    ///    尾端那個換行讓 == 永遠對不上(實測 String.Equals 序數比對回 false),
    ///    於是純文字被送進 JSON 解析器,得到
    ///       Unexpected character encountered while parsing value: e. Path '', line 0
    ///    ——也就是使用者 log 裡那 116 筆。那則訊息把「對方伺服器暫時掛掉」講成
    ///    「解析失敗」,完全無法歸因。
    ///
    /// 現在改成先看 HTTP 狀態碼,再用這個守衛擋「狀態碼是 2xx 但內容不是 JSON」
    /// (快取層/代理塞回錯誤頁的情況),永遠不會再拿非 JSON 去餵解析器。
    /// </summary>
    private static bool LooksLikeJson(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c == '\uFEFF')
            {
                continue;
            }

            return c == '{' || c == '[';
        }

        return false;
    }

    /// <summary>
    /// 把回應內容截成一段能安全寫進 log 的短字串(單行、最多 120 字)。
    /// 這是「三種失敗分得開」的關鍵:沒有它就只知道解析失敗,不知道對方到底回了什麼。
    /// </summary>
    private static string Snippet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(空白回應)";
        }

        var trimmed = value.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return trimmed.Length > 120 ? trimmed.Substring(0, 120) + "..." : trimmed;
    }

    private static string DescribeStatus(HttpStatusCode? statusCode)
    {
        return statusCode == null ? "連線失敗或逾時" : $"HTTP {(int)statusCode.Value}";
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(token);
        }
        catch (Exception)
        {
            // 只是為了寫進 log 的診斷字串,讀不到就算了,不要因此把整批請求變成別的錯誤。
            return string.Empty;
        }
    }

    private static async Task<bool> DelaySafelyAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return !token.IsCancellationRequested;
    }


    public async Task RetrieveMarketBoardPrices(IEnumerable<uint> itemIds, uint worldId, CancellationToken token,uint attempt = 0)
    {
        var itemIdList = itemIds.ToList();

        if (itemIdList.Count == 0)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            ReleaseQueued(itemIdList.Count);
            return;
        }

        // universalis 認不得的世界(以及 worldId 0)不送出請求。即使有人直接呼叫這個
        // 公開方法繞過 QueuePriceCheck 也一樣。
        if (!_universalisAvailability.IsWorldSupported(worldId))
        {
            ReleaseQueued(itemIdList.Count);
            return;
        }

        // 這個世界剛剛連續失敗太多次,正在冷卻:直接丟掉,不送請求也不寫 log
        // (寫了會變成另一種形式的洗版)。
        if (_worldCooldownUntil.TryGetValue(worldId, out var cooldownUntil) && DateTime.Now < cooldownUntil)
        {
            ReleaseQueued(itemIdList.Count);
            return;
        }

        if (attempt >= MaxRetries)
        {
            ReleaseQueued(itemIdList.Count);
            Logger.LogError(
                "universalis 查價已重試 {MaxRetries} 次仍未成功,放棄這批 {Count} 筆道具(世界 id {WorldId})。",
                MaxRetries, itemIdList.Count, worldId);
            return;
        }

        string worldName;
        if (!_worldNames.ContainsKey(worldId))
        {
            var world = _worldSheet.GetRowOrDefault(worldId);
            if (world == null)
            {
                ReleaseQueued(itemIdList.Count);
                return;
            }

            _worldNames[worldId] = world.Value.Name.ExtractText();
        }
        worldName = _worldNames[worldId];

        // 這個世界目前的批次上限。超過就先拆開再送——拆批不算重試次數,因為那不是
        // 「同一個請求再試一次」,而是換成幾個比較小的請求。
        var batchSize = GetBatchSize(worldId);
        if (itemIdList.Count > batchSize)
        {
            foreach (var chunk in itemIdList.Chunk(batchSize))
            {
                await RetrieveMarketBoardPrices(chunk, worldId, token, attempt);
            }

            return;
        }

        var itemIdsString = String.Join(",", itemIdList.Select(c => c.ToString()).ToArray());
        Logger.LogTrace("Sending request for items {ItemIds} to universalis API.", itemIdsString);
        string url =
            $"https://universalis.app/api/v2/{worldName}/{itemIdsString}?listings=20&entries=20";
        try
        {
            var response = await HttpClient.GetAsync(url, token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // ② 被限流。這是暫時性的,而且對方明確告訴我們「慢一點」。
                Logger.LogWarning(
                    "universalis 回報請求過於頻繁(HTTP 429),等一分鐘後重試這批 {Count} 筆。若你同時開著多個會查市場的外掛,這是最常見的原因。",
                    itemIdList.Count);
                TooManyRequests = true;
                LastFailure = DateTime.Now;
                if (!await DelaySafelyAsync(TimeSpan.FromMinutes(1), token))
                {
                    ReleaseQueued(itemIdList.Count);
                    return;
                }

                await RetrieveMarketBoardPrices(itemIdList, worldId, token, attempt + 1);
                return;
            }

            TooManyRequests = false;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // ① 這批道具沒有資料。universalis 對「這批道具它一個都不認得」也回 404
                // (不可交易的道具就會這樣),世界本身不一定有問題,所以這裡不動世界的
                // 可用性判定。這不是暫時性錯誤,重試不會變好,所以不進退避重試、也不印
                // 紅字,每個世界只寫一次 Information。
                if (_notFoundLoggedWorlds.TryAdd(worldId, default))
                {
                    Logger.LogInformation(
                        "universalis 對世界「{WorldName}」(id {WorldId})的查價回了 404(通常是該批道具不可交易或它不認得),已跳過,不重試。",
                        worldName, worldId);
                }

                ReleaseQueued(itemIdList.Count);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                // 🔴 改動前完全沒有這一段:除了 429 與 404 以外的任何狀態碼(500/502/504/
                //    520/524/400/403…)都會被當成正常回應,內容直接餵進 JSON 解析器。
                var errorBody = await SafeReadBodyAsync(response, token);
                await HandleFailedRequestAsync(itemIdList, worldId, worldName, response.StatusCode, errorBody,
                    url.Length, token, attempt);
                return;
            }

            var value = await response.Content.ReadAsStringAsync(token);

            if (!LooksLikeJson(value))
            {
                // 狀態碼是 2xx 但內容不是 JSON:一樣當成對方那側的暫時性問題,不要餵解析器。
                await HandleFailedRequestAsync(itemIdList, worldId, worldName, response.StatusCode, value,
                    url.Length, token, attempt);
                return;
            }

            if (itemIdList.Count == 1)
            {
                PricingAPIResponse? apiListing = JsonConvert.DeserializeObject<PricingAPIResponse>(value);

                if (apiListing == null)
                {
                    ReportUnexpectedShape(worldName, itemIdList.Count, value);
                    ReleaseQueued(itemIdList.Count);
                    return;
                }

                var listing = MarketPricing.FromApi(apiListing, worldId,
                    _hostedUniversalisConfiguration.SaleHistoryLimit);
                _ = _framework.RunOnFrameworkThread(() =>
                    ItemPriceRetrieved?.Invoke(apiListing.itemID, worldId, listing));
            }
            else
            {
                MultiRequest? multiRequest = JsonConvert.DeserializeObject<MultiRequest>(value);
                if (multiRequest?.items == null)
                {
                    ReportUnexpectedShape(worldName, itemIdList.Count, value);
                    ReleaseQueued(itemIdList.Count);
                    return;
                }

                foreach (var item in multiRequest.items.Select(c => c.Value))
                {
                    var listing = MarketPricing.FromApi(item, worldId,
                        _hostedUniversalisConfiguration.SaleHistoryLimit);
                    _ = _framework.RunOnFrameworkThread(() =>
                        ItemPriceRetrieved?.Invoke(item.itemID, worldId, listing));
                }
            }

            ReportSuccess(worldId, worldName, itemIdList.Count);
            ReleaseQueued(itemIdList.Count);
        }
        catch (OperationCanceledException ex)
        {
            if (token.IsCancellationRequested)
            {
                // 外掛關閉/登出。不是錯誤。
                ReleaseQueued(itemIdList.Count);
            }
            else
            {
                // token 沒被取消的 TaskCanceledException(OperationCanceledException 的子類)
                // 就是 HttpClient 自己的逾時。當成暫時性錯誤處理。
                await HandleFailedRequestAsync(itemIdList, worldId, worldName, null, ex.Message, url.Length,
                    token, attempt);
            }
        }
        catch (HttpRequestException ex)
        {
            // 斷網、DNS、TLS、連線被中斷。
            await HandleFailedRequestAsync(itemIdList, worldId, worldName, ex.StatusCode, ex.Message, url.Length,
                token, attempt);
        }
        catch (JsonReaderException ex)
        {
            // 保險:LooksLikeJson 放行了但內容還是壞的。重試同一批只會拿到同一包壞資料,
            // 所以這裡不退避也不重試,直接放掉。
            Logger.LogError(ex,
                "universalis 的回應以 JSON 開頭但解析失敗(世界「{WorldName}」,{Count} 筆),已跳過這批,不重試。",
                worldName, itemIdList.Count);
            LastFailure = DateTime.Now;
            ReleaseQueued(itemIdList.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "universalis 查價發生未預期的例外(世界「{WorldName}」,{Count} 筆)。",
                worldName, itemIdList.Count);
            LastFailure = DateTime.Now;
            ReleaseQueued(itemIdList.Count);
        }
    }

    /// <summary>
    /// 一次失敗的請求該怎麼處理。這裡是「三種失敗分得開」的分岔點:
    ///   ② 暫時性(5xx / 408 / 連線失敗)→ 把批次調小再送;已經是最小批就指數退避重試。
    ///   ③ 請求形狀不對(其餘 4xx)→ 寫一則帶狀態碼與回應內容的 Error,不重試。
    /// (① 沒有資料 = 404,在呼叫端就先處理掉了,不會走到這裡。)
    /// </summary>
    private async Task HandleFailedRequestAsync(
        List<uint> itemIdList,
        uint worldId,
        string worldName,
        HttpStatusCode? statusCode,
        string? body,
        int urlLength,
        CancellationToken token,
        uint attempt)
    {
        LastFailure = DateTime.Now;
        var statusText = DescribeStatus(statusCode);

        if (!IsTransientStatus(statusCode))
        {
            // ③ 請求本身就不對。退避重試只會用同樣的錯誤形狀再撞一次,所以不重試,
            //    改成把足以歸因的資訊一次寫齊(狀態碼、道具筆數、網址長度、回應內容)。
            if (_badRequestLoggedWorlds.TryAdd((worldId, (int)statusCode!.Value), default))
            {
                Logger.LogError(
                    "universalis 拒絕了查價請求,而且不是暫時性錯誤:世界「{WorldName}」(id {WorldId}),{StatusText},這批 {Count} 筆道具,網址長度 {UrlLength}。不重試。對方回應:{Body}",
                    worldName, worldId, statusText, itemIdList.Count, urlLength, Snippet(body));
            }

            ReleaseQueued(itemIdList.Count);
            return;
        }

        var streak = _worldFailureStreak.AddOrUpdate(worldId, 1, (_, previous) => previous + 1);

        // 把這個世界的批次上限調小。實測「一次查太多筆」正是 504 的成因。
        var previousBatch = GetBatchSize(worldId);
        var reducedBatch = Math.Max(MinBatchSize, previousBatch / 2);
        if (reducedBatch < previousBatch)
        {
            _worldBatchSize[worldId] = reducedBatch;
            Logger.LogInformation(
                "universalis 對世界「{WorldName}」的查價逾時({StatusText}):一次送太多筆會讓對方來不及回應,之後每批的上限由 {OldSize} 降到 {NewSize} 筆。",
                worldName, statusText, previousBatch, reducedBatch);
        }

        // 第一次、以及每 10 次寫一則 Warning,中間的只寫 Debug ——
        // 改動前是每一次都寫一則 Error,實機 log 裡因此出現連續 54 分鐘、每 40 秒一則的紅字。
        if (streak == 1 || streak % 10 == 0)
        {
            Logger.LogWarning(
                "universalis 暫時回不了資料:世界「{WorldName}」,{StatusText},這批 {Count} 筆道具,連續第 {Streak} 次。這是對方伺服器端的暫時性問題,不是你的設定有問題。對方回應:{Body}",
                worldName, statusText, itemIdList.Count, streak, Snippet(body));
        }
        else
        {
            Logger.LogDebug(
                "universalis 暫時回不了資料:世界「{WorldName}」,{StatusText},這批 {Count} 筆道具,連續第 {Streak} 次。對方回應:{Body}",
                worldName, statusText, itemIdList.Count, streak, Snippet(body));
        }

        // 連續失敗太多次:不再拆批也不再退避,整個世界安靜一段時間。
        // 改動前沒有這道閘門,實機 log 才會出現連續 54 分鐘、每 40 秒一則的紅字。
        if (streak >= FailureStreakBeforeCooldown)
        {
            _worldCooldownUntil[worldId] = DateTime.Now + CooldownDuration;
            Logger.LogWarning(
                "universalis 對世界「{WorldName}」已經連續 {Streak} 次回不了資料,暫停對這個世界查價 {Minutes} 分鐘,以免一直重打。",
                worldName, streak, (int)CooldownDuration.TotalMinutes);
            ReleaseQueued(itemIdList.Count);
            return;
        }

        // 拆批重試。只有真的會變小才拆,否則會原地打轉。
        var splitSize = Math.Max(MinBatchSize, Math.Min(reducedBatch, (itemIdList.Count + 1) / 2));
        if (splitSize < itemIdList.Count)
        {
            foreach (var chunk in itemIdList.Chunk(splitSize))
            {
                if (token.IsCancellationRequested)
                {
                    ReleaseQueued(chunk.Length);
                    continue;
                }

                await RetrieveMarketBoardPrices(chunk, worldId, token, attempt + 1);
            }

            return;
        }

        // 已經是最小批了:指數退避(30 / 60 / 120 秒,不再往上)後重試同一批。
        var backoffSeconds = 30 * (1 << (int)Math.Min(attempt, 1u));
        if (!await DelaySafelyAsync(TimeSpan.FromSeconds(backoffSeconds), token))
        {
            ReleaseQueued(itemIdList.Count);
            return;
        }

        await RetrieveMarketBoardPrices(itemIdList, worldId, token, attempt + 1);
    }

    private void ReportUnexpectedShape(string worldName, int count, string value)
    {
        Logger.LogError(
            "universalis 回了一段合法 JSON,但欄位結構不是預期的形狀(世界「{WorldName}」,{Count} 筆),已跳過這批。對方回應:{Body}",
            worldName, count, Snippet(value));
        LastFailure = DateTime.Now;
    }

    /// <summary>
    /// 查價成功。這裡刻意讓「成功」在 log 上也看得見——改動前只有失敗會留下痕跡,
    /// 使用者回報「查價壞了」時完全分不出「一直在失敗」與「根本沒送出請求」。
    /// </summary>
    private void ReportSuccess(uint worldId, string worldName, int count)
    {
        LastFailure = null;
        _worldCooldownUntil.TryRemove(worldId, out _);

        if (_worldFailureStreak.TryRemove(worldId, out var streak) && streak > 0)
        {
            Logger.LogInformation(
                "universalis 對世界「{WorldName}」的查價已恢復正常(先前連續失敗 {Streak} 次),這次取回 {Count} 筆。",
                worldName, streak, count);
        }
        else if (_successLoggedWorlds.TryAdd(worldId, default))
        {
            Logger.LogInformation(
                "universalis 對世界「{WorldName}」(id {WorldId})的查價成功,這次取回 {Count} 筆,之後每批上限 {BatchSize} 筆。",
                worldName, worldId, count, GetBatchSize(worldId));
        }
        else
        {
            Logger.LogDebug(
                "universalis 查價成功:世界「{WorldName}」,{Count} 筆。",
                worldName, count);
        }
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

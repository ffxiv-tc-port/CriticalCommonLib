using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Hosting;

namespace CriticalCommonLib.MarketBoard;

/// <summary>
/// Checks the market cache for items that need to be checked and queues them.
/// 排哪些條目由 <see cref="MarketRefreshPlanner"/> 決定（過期必排、同世界快過期的提早併批）。
/// </summary>
public class MarketRefreshService : BackgroundService
{
    private readonly IMarketCache _marketCache;
    private readonly MarketCacheConfiguration _marketCacheConfiguration;
    private readonly IPluginLog _pluginLog;
    private readonly MarketRefreshPlanner _planner = new();
    private readonly List<(uint ItemId, uint WorldId, DateTime LastUpdate)> _planned = new();
    private bool _failureLogged;

    private const uint RefreshInterval = 60;

    public MarketRefreshService(IMarketCache marketCache, MarketCacheConfiguration marketCacheConfiguration, IPluginLog pluginLog)
    {
        _marketCache = marketCache;
        _marketCacheConfiguration = marketCacheConfiguration;
        _pluginLog = pluginLog;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 停用外掛或關閉遊戲時 stoppingToken 被取消是正常收工，刻意吞掉不記成錯誤。
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _pluginLog.Verbose($"Market refresh service waiting for {RefreshInterval} seconds.");
                await Task.Delay(TimeSpan.FromSeconds(RefreshInterval), stoppingToken);
                uint queuedItems;
                try
                {
                    queuedItems = ProcessOldPrices(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 一輪失敗不該讓背景更新整場停掉；只記第一次，避免每分鐘洗版。
                    if (!_failureLogged)
                    {
                        _failureLogged = true;
                        _pluginLog.Warning(ex, "市場價格背景更新這一輪失敗，下一輪照常再試（之後同類失敗不再記錄）。");
                    }

                    continue;
                }

                if (queuedItems != 0)
                {
                    _pluginLog.Verbose($"Market refresh service has queued {queuedItems} items.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public uint ProcessOldPrices(CancellationToken stoppingToken)
    {
        var now = DateTime.Now;
        _planned.Clear();
        _planner.Plan(
            _marketCache.CachedPricing.Select(static kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.LastUpdate)),
            now, _marketCacheConfiguration.CacheMaxAgeHours, _planned);

        var itemsQueued = 0u;
        foreach (var (itemId, worldId, lastUpdate) in _planned)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            switch (_marketCache.QueueBackgroundRefresh(itemId, worldId))
            {
                case MarketCachePricingResult.Queued:
                    itemsQueued++;
                    _planner.MarkInFlight(itemId, worldId, lastUpdate);
                    break;
                case MarketCachePricingResult.AlreadyQueued:
                    _planner.MarkInFlight(itemId, worldId, lastUpdate);
                    break;
                case MarketCachePricingResult.Untradable:
                    _planner.MarkNeverQueue(itemId, worldId);
                    break;
            }
        }

        return itemsQueued;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _pluginLog.Verbose("Stopping service {Type} ({This})", GetType().Name, this);
        await base.StopAsync(cancellationToken);
        _pluginLog.Verbose("Stopped service {Type} ({This})", GetType().Name, this);
    }
}

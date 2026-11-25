using System;
using System.Collections.Generic;

namespace CriticalCommonLib.MarketBoard;

/// <summary>
/// 背景定時更新這一輪要排哪些（道具, 世界）。純邏輯、不碰 Dalamud；非執行緒安全，只給單一迴圈用。
/// 契約：過期（年齡 &gt; 上限）且不在查詢中的條目，一定在它過期的那一輪排出——與改前相同、不會延後。
/// 併批只會「提早」：某世界有條目過期時，同世界在 <see cref="LookaheadFor"/> 內也會過期的條目一起排；
/// 這種提早整掃只在湊得到 <see cref="HalfBatch"/> 件，或距該世界上次整掃滿 <see cref="MinSweepInterval"/> 時才做，
/// 而且由舊到新只取到整批（<see cref="FullBatch"/> 的倍數）為止。
/// </summary>
public sealed class MarketRefreshPlanner
{
    public static readonly TimeSpan MaxLookahead = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinSweepInterval = TimeSpan.FromMinutes(15);
    public const int FullBatch = 50;
    public const int HalfBatch = FullBatch / 2;

    private readonly Dictionary<uint, DateTime> _lastSweep = new();
    private readonly Dictionary<(uint ItemId, uint WorldId), DateTime> _inFlight = new();
    private readonly HashSet<(uint ItemId, uint WorldId)> _neverQueue = new();
    private readonly HashSet<(uint ItemId, uint WorldId)> _inFlightSeen = new();
    private readonly List<(uint ItemId, uint WorldId)> _inFlightStale = new();
    private readonly Dictionary<uint, List<(uint ItemId, DateTime LastUpdate, bool Due)>> _byWorld = new();

    public int InFlightCount => _inFlight.Count;

    /// <summary>上限的四分之一、最多一小時：提早整掃不會去更新還不到上限 3/4 年齡的條目。</summary>
    public static TimeSpan LookaheadFor(double maxAgeHours)
    {
        if (maxAgeHours <= 0)
        {
            return TimeSpan.Zero;
        }

        var quarter = TimeSpan.FromHours(maxAgeHours / 4);
        return quarter < MaxLookahead ? quarter : MaxLookahead;
    }

    /// <summary>把這一輪該排的條目依世界、由舊到新附加到 <paramref name="output"/>。</summary>
    public void Plan(IEnumerable<(uint ItemId, uint WorldId, DateTime LastUpdate)> entries, DateTime now,
        double maxAgeHours, List<(uint ItemId, uint WorldId, DateTime LastUpdate)> output)
    {
        var lookaheadHours = LookaheadFor(maxAgeHours).TotalHours;
        foreach (var list in _byWorld.Values)
        {
            list.Clear();
        }

        _inFlightSeen.Clear();
        foreach (var (itemId, worldId, lastUpdate) in entries)
        {
            var key = (itemId, worldId);
            if (_inFlight.TryGetValue(key, out var lastUpdateWhenQueued))
            {
                if (lastUpdate == lastUpdateWhenQueued)
                {
                    _inFlightSeen.Add(key);
                    continue;
                }

                _inFlight.Remove(key);
            }

            if (_neverQueue.Contains(key))
            {
                continue;
            }

            // 判斷式與改前相同（TotalHours 嚴格大於），過期的定義不變。
            var ageHours = (now - lastUpdate).TotalHours;
            if (ageHours <= maxAgeHours - lookaheadHours)
            {
                continue;
            }

            if (!_byWorld.TryGetValue(worldId, out var candidates))
            {
                candidates = new List<(uint, DateTime, bool)>();
                _byWorld[worldId] = candidates;
            }

            candidates.Add((itemId, lastUpdate, ageHours > maxAgeHours));
        }

        PruneInFlight();

        foreach (var (worldId, candidates) in _byWorld)
        {
            var dueCount = 0;
            foreach (var c in candidates)
            {
                if (c.Due)
                {
                    dueCount++;
                }
            }

            if (dueCount == 0)
            {
                continue;
            }

            candidates.Sort(static (a, b) => a.LastUpdate.CompareTo(b.LastUpdate));
            var sweep = candidates.Count >= HalfBatch
                        || !_lastSweep.TryGetValue(worldId, out var last)
                        || now - last >= MinSweepInterval;
            if (sweep)
            {
                _lastSweep[worldId] = now;
            }

            // 太年輕的留給下一輪：提早得越多，每件道具的更新頻率就越高。
            var take = dueCount;
            if (sweep)
            {
                var wholeBatches = candidates.Count / FullBatch * FullBatch;
                take = Math.Max(dueCount, wholeBatches > 0 ? wholeBatches : candidates.Count);
            }

            for (var i = 0; i < take; i++)
            {
                output.Add((candidates[i].ItemId, worldId, candidates[i].LastUpdate));
            }
        }
    }

    /// <summary>
    /// 已排出（或對方說已在排）的條目，在它的 LastUpdate 變動前不再列入候選。
    /// 比「是否變動」而不比「是否晚於排入時間」：回應的 LastUpdate 可能早於這一輪的 now。
    /// </summary>
    public void MarkInFlight(uint itemId, uint worldId, DateTime lastUpdateWhenQueued)
    {
        _inFlight[(itemId, worldId)] = lastUpdateWhenQueued;
    }

    /// <summary>永遠不會被排的條目（不可交易），本場之後一律跳過。</summary>
    public void MarkNeverQueue(uint itemId, uint worldId)
    {
        _neverQueue.Add((itemId, worldId));
    }

    private void PruneInFlight()
    {
        if (_inFlightSeen.Count == _inFlight.Count)
        {
            return;
        }

        _inFlightStale.Clear();
        foreach (var key in _inFlight.Keys)
        {
            if (!_inFlightSeen.Contains(key))
            {
                _inFlightStale.Add(key);
            }
        }

        foreach (var key in _inFlightStale)
        {
            _inFlight.Remove(key);
        }
    }
}

/// <summary>
/// 每個世界的批次上限怎麼縮、怎麼回升。純函式。
/// 縮：暫時性失敗就減半，下限 <see cref="MinBatchSize"/>。回升：連續成功 <see cref="GrowAfterSuccesses"/> 批就加倍，上限 <see cref="MaxBatchSize"/>。
/// </summary>
public static class UniversalisBatchSizing
{
    public const int MaxBatchSize = 50;
    public const int MinBatchSize = 10;
    public const int GrowAfterSuccesses = 20;

    public static int Shrink(int current) => Math.Max(MinBatchSize, current / 2);

    /// <summary>記一批成功。回傳新的上限與新的連續成功數；上限有變時連續成功數歸零。</summary>
    public static (int Size, int Streak) OnSuccess(int current, int streak)
    {
        if (current >= MaxBatchSize)
        {
            return (current, 0);
        }

        streak++;
        if (streak < GrowAfterSuccesses)
        {
            return (current, streak);
        }

        return (Math.Min(MaxBatchSize, current * 2), 0);
    }
}

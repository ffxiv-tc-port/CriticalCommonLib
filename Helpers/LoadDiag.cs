using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CriticalCommonLib.Helpers;

/// <summary>
/// 載入期一次性計時：呼叫端執行緒上的耗時與配置量（<see cref="GC.GetAllocatedBytesForCurrentThread"/>）。
/// 只給載入與第一次初始化用，不放進每幀路徑。
/// </summary>
public readonly struct LoadTimer
{
    private readonly long _startTicks;
    private readonly long _startBytes;

    private LoadTimer(long startTicks, long startBytes)
    {
        _startTicks = startTicks;
        _startBytes = startBytes;
    }

    public static LoadTimer Start() => new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    /// <summary>從開始到現在，格式「12.3 ms／4.5 MB」；配置量只算呼叫這個方法的執行緒。</summary>
    public override string ToString()
    {
        var ms = Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;
        var mb = (GC.GetAllocatedBytesForCurrentThread() - _startBytes) / 1048576.0;
        return $"{ms:F1} ms／{mb:F1} MB";
    }
}

/// <summary>
/// 插件建構子執行緒上各段的計時，由插件在建構子結束時彙成一行 <c>[LoadDiag]</c>。
/// 靜態欄位跟著插件的組件載入內容一起換新，熱更新後從空的開始。
/// </summary>
public static class LoadDiag
{
    private static readonly object Gate = new();
    private static readonly List<string> Parts = new();

    public static void Record(string name, LoadTimer timer)
    {
        var part = name + " " + timer;
        lock (Gate)
        {
            Parts.Add(part);
        }
    }

    /// <summary>取出目前記下的各段（以「、」連接）並清空。</summary>
    public static string Take()
    {
        lock (Gate)
        {
            var joined = string.Join("、", Parts);
            Parts.Clear();
            return joined;
        }
    }
}

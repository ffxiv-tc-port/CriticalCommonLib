using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CriticalCommonLib.Helpers;

/// <summary>
/// 每幀走訪同一個 <see cref="ConcurrentDictionary{TKey,TValue}"/> 時重用它的列舉器（它是 class，每次 foreach 配置一個）。
/// 走訪到的內容與順序跟直接 foreach 相同：同一個列舉器，只是用 Reset() 回到起點。
/// 契約：單一執行緒使用、不可重入（走到一半時不可再呼叫 Begin）；字典換成另一個實例時改用新字典的列舉器。
/// </summary>
public sealed class ReusableDictionaryEnumerator<TKey, TValue> where TKey : notnull
{
    private ConcurrentDictionary<TKey, TValue>? _source;
    private IEnumerator<KeyValuePair<TKey, TValue>>? _enumerator;
    private bool _resetUnsupported;

    /// <summary>回傳停在起點的列舉器；呼叫端只用 MoveNext／Current。</summary>
    public IEnumerator<KeyValuePair<TKey, TValue>> Begin(ConcurrentDictionary<TKey, TValue> source)
    {
        if (_resetUnsupported)
        {
            return source.GetEnumerator();
        }

        var enumerator = _enumerator;
        if (enumerator == null || !ReferenceEquals(_source, source))
        {
            enumerator = source.GetEnumerator();
            _enumerator = enumerator;
            _source = source;
            return enumerator;
        }

        try
        {
            enumerator.Reset();
        }
        catch (NotSupportedException)
        {
            // 這個執行環境的列舉器不支援 Reset：退回每次新建，行為同直接 foreach。
            _resetUnsupported = true;
            _enumerator = null;
            _source = null;
            return source.GetEnumerator();
        }

        return enumerator;
    }
}

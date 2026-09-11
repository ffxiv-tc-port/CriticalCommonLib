using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CriticalCommonLib.MarketBoard
{
    /// <summary>
    /// universalis.app 的「這個世界查不查得到」判定。
    ///
    /// 舊做法是按 ClientLanguage 判定,把整個繁體中文(台服)客戶端一律當成沒有資料來源。
    /// 那個前提是錯的:universalis 的 /api/v2/data-centers 逐字回報「陸行鳥 / 繁中服」
    /// 資料中心,底下 8 個世界(RowId 4028-4035)都收得到上傳資料,而且 /api/v2/{世界名}/{道具}
    /// 直接吃繁體中文世界名(2026-09-03 實際打 API 覆核,迦樓羅回 worldID 4029 的真實掛售)。
    ///
    /// 改成執行期按「世界」判定,判定的依據是 universalis 自己的 /api/v2/worlds 世界清單
    /// (由 HostedUniversalis 在啟動時取一次,再灌進來)。
    ///
    /// 🔴 這裡刻意「不」拿 HTTP 404 當作世界不存在的證據:universalis 對「世界正常但道具
    /// 不可交易/不存在」同樣回 404(2026-09-03 實測:/api/v2/4029/1 回 404,而 4029 是活的)。
    /// 拿 404 當判準會在第一次查到不可交易道具時就把一個好世界誤判成不支援。
    ///
    /// 這裡也刻意不用 World.IsPublic 當過濾器:台服陸行鳥資料中心(151)底下的世界
    /// IsPublic 全部是 False,活的 8 個與內部/測試用的那幾個在 IsPublic / UserType /
    /// DataCenter 三個欄位上完全相同,靠 World 表分不出來。
    ///
    /// 這個判定只影響「要不要送出查詢請求」與「顯示端要畫載入中還是畫沒有資料」,
    /// 不牽涉任何掛售、改價或自動接手邏輯。
    /// </summary>
    public sealed class UniversalisAvailability
    {
        /// <summary>
        /// universalis 認得的世界 id。null 代表還沒取到清單(或取失敗)——這種狀態下一律樂觀
        /// 放行,行為與改動前的國際服客戶端相同。
        /// </summary>
        private volatile HashSet<uint>? _knownWorldIds;

        private readonly ConcurrentDictionary<uint, byte> _queriedSupported = new();
        private readonly ConcurrentDictionary<uint, byte> _queriedUnsupported = new();

        /// <summary>
        /// 使用者指定「不要查價」的世界。與上面那份 universalis 世界清單是兩回事:
        /// 那份回答「對方認不認得這個世界」,這份回答「我們要不要查」。
        /// 台服的拉姆(RowId 4034)已停止營運,但它在 World 表與 universalis 的世界
        /// 清單裡都還在,所以只靠上面兩個判準攔不住,必須由使用者設定這一層擋。
        /// null 代表還沒灌進來(等同沒有任何世界被排除)。
        /// </summary>
        private volatile HashSet<uint>? _excludedWorldIds;

        private int _exclusionRevision;

        /// <summary>
        /// 排除清單的版次。有快取世界清單的顯示端拿它判「要不要重算」——排除清單改了
        /// 之後若不重算,使用者會看到「勾了卻沒有變化」直到重開外掛。
        /// </summary>
        public int ExclusionRevision => Volatile.Read(ref _exclusionRevision);

        /// <summary>
        /// 灌入使用者的排除清單。空清單是合法的(代表全部都要查),所以這裡刻意不像
        /// SetKnownWorlds 那樣忽略空集合——忽略的話使用者就永遠清不掉排除清單。
        /// </summary>
        public void SetExcludedWorlds(IEnumerable<uint> worldIds)
        {
            _excludedWorldIds = new HashSet<uint>(worldIds);
            Interlocked.Increment(ref _exclusionRevision);
        }

        /// <summary>
        /// 這個世界是不是被使用者排除了。
        /// </summary>
        public bool IsWorldExcluded(uint worldId)
        {
            var excluded = _excludedWorldIds;
            return excluded != null && excluded.Contains(worldId);
        }

        /// <summary>
        /// 目前被排除的世界,給顯示端與診斷用。
        /// </summary>
        public IReadOnlyList<uint> ExcludedWorlds => _excludedWorldIds?.ToList() ?? new List<uint>();


        /// <summary>
        /// 世界清單是否已經取到。
        /// </summary>
        public bool WorldListLoaded => _knownWorldIds != null;

        /// <summary>
        /// 灌入 universalis 的世界清單。空清單會被忽略(當成沒取到,維持樂觀模式),
        /// 免得對方回一包空的就把整個查價功能鎖死。
        /// </summary>
        public void SetKnownWorlds(IEnumerable<uint> worldIds)
        {
            var set = new HashSet<uint>(worldIds);
            if (set.Count == 0)
            {
                return;
            }

            _knownWorldIds = set;
        }

        /// <summary>
        /// 這個世界是否值得對 universalis 查價。
        /// worldId 為 0(還沒登入、拿不到角色所在世界)一律回 false;
        /// 世界清單還沒取到時一律回 true(樂觀,先送請求再說)。
        /// </summary>
        public bool IsWorldSupported(uint worldId)
        {
            if (worldId == 0)
            {
                return false;
            }

            // 使用者排除的世界:直接不支援,而且刻意「不」記進 _queriedSupported /
            // _queriedUnsupported。那兩份是給 MarketDataAvailable 判「這個客戶端到底有沒有
            // 線上市場資料可用」的,把使用者自己的選擇混進去,會讓「排掉唯一問過的世界」
            // 變成「整個市場功能沒有資料」,顯示端就會把別的世界的載入中畫成沒有資料。
            if (IsWorldExcluded(worldId))
            {
                return false;
            }

            var known = _knownWorldIds;
            if (known == null)
            {
                return true;
            }

            if (known.Contains(worldId))
            {
                _queriedSupported.TryAdd(worldId, default);
                return true;
            }

            _queriedUnsupported.TryAdd(worldId, default);
            return false;
        }

        /// <summary>
        /// 這個客戶端到底有沒有線上市場資料可用。顯示端拿它決定要畫「載入中」還是「沒有資料」。
        ///
        /// 判定規則:①還沒問過任何世界 → true ②問過的世界裡有任何一個在 universalis 的
        /// 清單裡 → true ③問過的世界全部不在清單裡 → false。
        ///
        /// 世界清單取不到(斷網、universalis 掛掉)時不會有任何世界被記成不支援,所以顯示端
        /// 仍然會畫「載入中」,行為與改動前的國際服客戶端一致。
        /// </summary>
        public bool MarketDataAvailable => !_queriedSupported.IsEmpty || _queriedUnsupported.IsEmpty;

        /// <summary>
        /// 目前已知不在 universalis 清單裡的世界,只給診斷用。
        /// </summary>
        public IReadOnlyList<uint> UnsupportedWorlds => _queriedUnsupported.Keys.ToList();
    }
}

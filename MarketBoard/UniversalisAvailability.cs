using Dalamud.Game;
using Dalamud.Plugin.Services;

namespace CriticalCommonLib.MarketBoard
{
    /// <summary>
    /// universalis.app 只收錄國際服(與部分地區服)的市場資料,台服(繁體中文服)完全不在它的
    /// 涵蓋範圍內。台服客戶端照樣送請求的話,universalis 會回傳非 JSON 的錯誤內容,外掛解析
    /// 失敗後進入「backing off 30 seconds」的重試迴圈,錯誤紅字會反覆洗版而且永遠不會成功。
    ///
    /// 這裡做的是「執行期偵測」而不是「改設定預設值」:既有使用者的設定檔已經寫死了舊值,
    /// 改預設值對他們一律無效(而且是靜默無效)。
    /// </summary>
    public static class UniversalisAvailability
    {
        /// <summary>
        /// 判斷目前客戶端所在的服務區是否有 universalis 資料可用。
        /// </summary>
        public static bool IsSupportedRegion(IClientState clientState)
            => IsSupportedRegion(clientState.ClientLanguage);

        /// <summary>
        /// 判斷指定的客戶端語言是否屬於有 universalis 資料的服務區。
        /// </summary>
        public static bool IsSupportedRegion(ClientLanguage clientLanguage)
            => !IsTraditionalChineseClient(clientLanguage);

        /// <summary>
        /// 是否為繁體中文(台服)客戶端。
        /// </summary>
        public static bool IsTraditionalChineseClient(IClientState clientState)
            => IsTraditionalChineseClient(clientState.ClientLanguage);

        /// <summary>
        /// 是否為繁體中文(台服)客戶端。
        ///
        /// 台服 Dalamud fork 的 ClientLanguage 列舉在國際服的 0~3(日/英/德/法)之後,額外
        /// 補了 4=ChineseSimplified(國服)、5=ChineseTraditional、6=Korean、7=TraditionalChinese。
        /// 實測台服回報的是 7;5 是同一 fork 裡較早期的繁中列舉值,任何非繁中客戶端都不會
        /// 回報這兩個值,所以一併認列不會影響其他服的行為。
        /// </summary>
        public static bool IsTraditionalChineseClient(ClientLanguage clientLanguage)
            => (int)clientLanguage is 5 or 7;
    }
}

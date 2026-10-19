<!-- ffxiv-tc-port 繁體中文說明開始 -->
# CriticalCommonLib(台服 fork)

由 Critical-Impact 開發的函式庫，提供庫存追蹤、角色追蹤、Universalis 市場查價快取、
製作清單需求計算、NPC 位置查詢等功能，是 `InventoryTools`（Allagan Tools）的核心底層。

## 台服 fork 的目的

跟隨艦隊釘 API13，讓 Universalis 查價在台服可用且穩定，並修正實際用到的效能問題。

## 與上游的差異

- **查價逐世界判定**：依 Universalis 的世界清單判斷能不能查，不再按客戶端語言整區關閉
  （原本的語言判斷在台服會誤關整個查價功能）；另可指定「不要查的伺服器」，已停止營運的伺服器
  不再被反覆查價。
- **查價穩定性**：主查詢與成交歷史分開查（主查詢不再動不動逾時）、「N 日成交數」改成取回整個
  時間窗再算（不再卡在 20）、先看 HTTP 狀態碼再解析、逾時時自動調小批次、連續失敗時以
  30／60／120 秒三級重試等待；查價狀態改為原子讀寫。
- **僱員掛賣價格改由遊戲結構直讀**，移除容易因改版失效的封包解析路徑。
- **合成清單用道具 ID 查項目時的收藏品判定來源修正**。
- **多處降低記憶體用量**：背包掃描重用暫存陣列、各角色持有數量重算少配置、每幀的視窗與佇列檢查
  不再配置；並限制庫存歷史成長上限、修正 `ThrottleDispatcher` 追蹤清單只增不減。
- **log 整理**：關閉遊戲時查價背景服務的正常取消不再記成錯誤；成交歷史查不到時每小時彙總一次成敗。

其餘結構跟隨上游 API13 世代（13.1.9 前後）合併，之後未逐一比對上游後續變動。

## 誰在用它

目前只有 **`InventoryTools`** 一個插件 repo 以子模組使用。

---

以下為上游原始 README，內容未經修改：

<!-- ffxiv-tc-port 繁體中文說明結束 -->

### Common Library for Dalamud Plugins

This is a library for some of the more generic functionality that Allagan Tools uses.

Including but not limited to
- Inventory Tracking
- Character Tracking
- Custom inherited sheets for lumina that contain extra functionality
- Inventory highlighting
- ODR Parsing(the sort order of items)
- Universalis result fetching and caching
- Crafting(can generate craft lists to work out requirements)
- NPC location parsing and mapping

<!-- ffxiv-tc-port 繁體中文說明開始 -->
# CriticalCommonLib(台服 fork)

由 Critical-Impact 開發的函式庫，提供庫存追蹤、角色追蹤、Universalis 市場查價快取、
製作清單需求計算、NPC 位置查詢等功能，是 `InventoryTools` 的核心底層。

## 台服 fork 的目的

跟隨艦隊釘 API13，並針對台服環境做了幾項實質修改：

- **查價邏輯改為依 Universalis 世界清單逐世界判定**，不再按客戶端語言整區關閉（原本的
  語言判斷在台服會誤關整個查價功能）。
- **合成清單用道具 ID 查項目時的收藏品判定來源修正**。
- **雇員掛賣價格改由遊戲結構直讀**，移除容易因改版失效的封包解析路徑。
- **效能修正**：限制庫存歷史成長上限、修正 `ThrottleDispatcher` 追蹤清單只增不減的洩漏。

## 與上游的差異

以上四項為主，其餘結構跟隨上游 API13 世代（`aaab333`，13.1.9 前後）合併，之後未逐一比對
上游後續變動。

## 誰在用它

艦隊裡目前只有 **`InventoryTools`** 一個插件消費。

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

# Glow Mini A3136 PartyCast 開關實測

日期：2026-11-05（Asia/Taipei）。目標為目前藍牙連線的 Glow Mini。專用通道沿用已驗證的 `0cf12d31-fac3-4553-bd80-d6832e7b3136`。

## 辨識與驗證

先以唯讀狀態查詢 `01 01` 記錄基準，使用者再短按實體 PartyCast 按鈕，並確認 PartyCast 已開啟。觀測到：

- 喇叭主動通知：`09 ff 00 00 01 01 24 0b 00 01 3a`。
- 完整狀態 body offset 32（從 0 起算）由 `00` 變成 `01`。

以通知命令的高位元 setter 形式提出單一候選 `01 A4`，沒有對裝置掃描命令範圍。實測此候選有 setter ACK、對應通知，且獨立完整狀態回讀也符合目標：

| 操作 | 寫入完整封包 | 喇叭通知 | 完整狀態 offset 32 |
| --- | --- | --- | --- |
| 關閉 | `08 ee 00 00 00 01 a4 0b 00 00 a6` | `01 24` body `00` | `01 → 00` |
| 開啟 | `08 ee 00 00 00 01 a4 0b 00 01 a7` | `01 24` body `01` | `00 → 01` |

兩次操作前後的 49-byte 完整狀態 body 比對，只有 offset 32 改變。使用者亦確認機身燈號／提示音確實跟著切換，最後重新開啟。

`01 24` 無 body 查詢只收到空 ACK，沒有帶回狀態。因此正式程式不把這個 ACK 當作狀態，也不把 setter ACK 單獨當作操作成功；一律重新查詢 `01 01`，讀取已驗證的 A3136 欄位。

## 桌面程式 v1.3

- `SoundcoreProtocol.ReadPartyCast`：只讀取狀態。
- `SoundcoreProtocol.SetPartyCast`：先確認 A3136 狀態格式與原狀態，必要時送一次明確 0/1 寫入，再獨立回讀；相同目標狀態不重送寫入。
- 完整狀態必須符合 59-byte 封包長度、49-byte body、方向標頭、命令、校驗和，且 PartyCast 旗標為 0/1。未知格式不允許盲寫。
- 充電與 PartyCast 共用通道鎖，避免同一程式同時開啟兩個專用 RFCOMM 連線。
- UI 切換時禁用重複操作及裝置選擇，按鈕動作綁定發起時確認的藍牙位址。舊的背景讀取不覆寫較新的操作結果。
- 狀態每 15 秒更新；斷線、逾時或未知格式顯示未取得狀態，不猜測開關結果。

本次只驗證目前連線這顆喇叭的 PartyCast 開啟／關閉。未實作群組成員管理、其他喇叭搜尋、TWS 或 PartyCast 版本切換。

發佈前驗證：38 項離線檢查通過；C# 原生唯讀查詢成功；桌面介面實際按「關閉」後回讀為已關閉，再按「開啟」後回讀為已開啟。切換中的兩個按鈕及裝置選擇皆確實停用，最後維持開啟。

原始擷取：`partycast-button-capture.jsonl`、`partycast-set-off-trial.json`、`partycast-set-on-trial.json`。它們包含本機裝置識別，僅留在本機並由 gitignore 排除。測試使用保留實際格式、移除序號的狀態封包。

官方對使用者可見的 PartyCast 行為說明：[Glow Mini PartyCast 使用方式](https://service.soundcore.com/article-description/How-can-I-use-the-PartyCast-function?urlName=How-can-I-use-the-PartyCast-function)。官方頁面沒有公開上述控制 opcode；opcode 與狀態欄位由本機實測取得。

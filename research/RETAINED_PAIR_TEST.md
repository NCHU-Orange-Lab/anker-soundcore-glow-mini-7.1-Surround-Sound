# 保留副機配對的 PartyCast 對照（已完成本次驗證）

2026-11-05 台灣時間。使用者明確要求保留副機配對，只中斷電腦的直接音訊連線，再加入 PartyCast 測試即時讀取。副機 `F4:9D:8A:0E:D2:BB`，主機 `F4:9D:8A:0A:91:10`。

- 初始副機 Remembered/Authenticated=false；主機均 true、音訊連線中且為系統預設輸出。
- 使用者已確認副機退出 PartyCast 並進入一般藍牙配對模式。
- 新工具 `pair_secondary_retained.py --pair` 僅針對已核對的副機提出 Windows 原生配對，**沒有任何移除配對邏輯**。舊 `test_secondary_classic_pair.py` 會自動清除配對，這次不能使用其變更模式。
- 09:29 的程式化配對回傳 Windows 錯誤 1244（尚未完成驗證）；副機仍 Remembered/Authenticated=false，雖 Classic Connected=true，但沒有其音訊端點，不能誤認為配對成功。紀錄 `secondary-retained-pair.json`、`link-retained-before-pair.json`。
- 已請使用者在 Windows「新裝置」清單手動完成副機配對；目前等待完成回覆。保留配對已獲使用者明確授權，後續不需再問是否可以保留。
- 暫停了工作區 `dist/SoundcoreDesk.exe` 的自動輪詢，避免它與研究程式爭用 RFCOMM；測試結束應重新開啟。
- `BleInfoProbeRetained.exe` 已編譯，最後一個參數可指定 3..60 秒查詢間隔；保留既有已驗證 `01 01/03/04` 查詢白名單。只將新通知中的完整、校驗有效封包當作新回覆，不使用初始讀值當成即時資訊。
- 後續依序量測：配對且直接音訊連線基線 → 保留配對但音訊中斷、尚未加入 PartyCast → 持續 BLE 通道時加入 PartyCast → 關閉通道後重新連 BLE 查詢。各階段以 `Capture-LinkState.ps1 -Label ...` 保存實際音訊端點與 Classic 配對狀態。
- 已讀 computer-use 技能及 guidance/api/confirmations，使用 Node REPL `sky` 介面。Settings 視窗已選取並開啟 Bluetooth 與裝置頁；node 全域 `settingsWindow`、`state`、`sky` 已建立。使用者正在處理配對時，不搶其操作焦點。後續動作前須重新擷取狀態，舊座標不可沿用。
- Windows `BluetoothSetServiceState` 會安裝／移除對應服務驅動，與單純中斷連線不同，因此未使用；`IOCTL_BTH_DISCONNECT_DEVICE` 官方驅動說明屬 kernel-mode 呼叫，未為此安裝任何驅動。優先用 Windows 已配對裝置的「中斷連線」操作，必須辨識正確副機，不能憑同名卡片猜測。

## 配對完成與音訊中斷對照

- 使用者確認副機手動配對完成；`link-retained-paired-baseline.json` 證實兩顆皆 Remembered/Authenticated=true，副機音訊為 Active 且曾成為系統預設輸出。BLE 新通知能驗證 checksum、序號與狀態，見 `ble-retained-audio-connected-baseline.jsonl`。
- 在 Windows Settings 透過「進階音效內容」確認裝置清單的第二個同名項目對應原主機（未加 2- 的 soundcore Glow Mini）；另一項才是副機。透過聲音頁選擇原主機為預設輸出，再回裝置頁中斷副機連線。未點移除、未刪除配對。
- `link-retained-audio-disconnected.json`：副機 Audio State=8，Remembered/Authenticated=true、Connected=false；主機仍 State=1、預設輸出=true。此時 BLE 已知查詢有寫入成功但無新通知 (`ble-retained-audio-disconnected-baseline.jsonl`)。
- 嘗試保留配對、無音訊時的 RFCOMM 查詢，通道可連但無回覆；剛結束時音訊仍 State=8，見 `probe-retained-paired-no-audio-rfcomm.json` 及 `link-retained-paired-no-audio-after-rfcomm.json`。
- 隨後重新開啟 BLE 觀察時收到新通知，但同時重新核對發現副機音訊已自動重連為 State=1；這段不能當作「音訊已中斷」的成功。紀錄 `link-retained-live-ble-no-audio.json`、`ble-retained-partycast-transition.jsonl`。
- 再次從同一 Windows 副機項目中斷連線；`link-retained-second-audio-disconnect.json` 證實保留配對且音訊 State=8。Windows 同時關閉了既有 BLE 物件，持續觀察於 `01:37:42Z` 出現 ObjectDisposedException，因此無法把 Windows 此操作當作只中斷 A2DP、BLE 通道完全不動。
- `ble-retained-main-awaiting-group.jsonl` 已重新讀到主機有效通知，PartyCast=true。`link-retained-awaiting-partycast.json`：主機音訊 Active/預設=true，副機音訊 State=8、Remembered/Authenticated=true、Connected=false。
- 使用者隨後已確認副機加入 PartyCast、兩顆同步播放。後續以 `Monitor-GlowAudio.ps1` 監看查詢前後音訊狀態，結果見下。

## 09:43–09:44 最終對照結果（Asia/Taipei）

1. `link-retained-confirmed-partycast.json`：原主機音訊 State=1 且為預設輸出；副機音訊 State=8，Classic Remembered/Authenticated=true、Connected=false。符合保留配對、沒有直接音訊的條件，且使用者確認同步播放。
2. `ble-retained-confirmed-partycast-state.jsonl`：新建 BLE 連線時 GATT service 回傳 Unreachable、零服務；**沒有新狀態封包**。
3. `probe-retained-confirmed-partycast-rfcomm.json`：已知副機 RFCOMM UUID 連線回傳 10060 逾時，未建立查詢通道。`link-retained-group-after-rfcomm.json` 仍顯示副機音訊中斷、配對保留。
4. `audio-retained-group-probes.jsonl`：01:43:02.621–01:44:29.747 UTC（09:43:02–09:44:29）持續每 500ms 檢查音訊狀態、變更即記錄並每 5 秒留 heartbeat；18 筆紀錄均為主機 Active=true、副機 Active=false、預設原主機，沒有觀察到副機音訊自動重連。不能把先前音訊自動重連的成功封包混入此測試。
5. 同一電腦、同一工具對原主機的正對照 `ble-retained-partycast-main-positive.jsonl` 成功收到兩個新的 59-byte `01 01` 回覆，校驗正確、主機序號吻合、PartyCast=true。讀取工具並未整體失效。

**結論**：本次韌體 02.10／Windows 環境下，保留配對不足以讓現有 BLE／RFCOMM 通道在此 PartyCast 副機條件下取得即時資訊，排除了「只要不刪除配對就會恢復讀取」這個假設。不能據此推論所有隱藏指令、其他通道或其他韌體都不可行；音訊未連且未加入群組時也曾無新回覆，因此不可單憑此次比較斷言只有 PartyCast 旗標造成限制。

**測後狀態**：依使用者要求保留副機配對，兩顆 Remembered/Authenticated=true；主機仍連線且為預設輸出，副機直接音訊未連。使用者已確認兩顆同步播放。沒有移除配對或變更音訊服務驅動。已停止研究廣播掃描、重新開啟桌面 v1.4；只把先前確實收到的副機新通知更新至紀錄（最後裝置讀取 09:37:32，發生於直接音訊已重連的階段），不把失敗查詢更新成新資料。

# Glow Mini A3136 充電狀態協定實測

> 2026-11-06 整理：研究用的原始擷取、腳本、APK 拆解、記憶體傾印與 Android 模擬器（約 30 GB）已從本資料夾移除，只保留這份與 PARTYCAST.md、PARTYCAST_MEMBERS.md、TWS.md、RETAINED_PAIR_TEST.md 等結論文件。文件中提到的其他檔名僅供對照，檔案已不在此處。

日期：2026-11-05（Asia/Taipei）。目標是使用者目前配對的單顆 soundcore Glow Mini，裝置容器 GUID 對應原程式選取的實體喇叭。

## 結論

這顆喇叭會透過專用 RFCOMM 通道回報正在充電與否。這個資訊未出現在本機 Windows PnP 的 ChargingState 屬性，但可以直接向喇叭查詢。

使用者協助拔插 USB-C 線後，三次觀測如下。三份回應的校驗和都正確。

| 實際條件 | 01 04 回應內容 | 解讀 |
| --- | --- | --- |
| 首次查詢，插著充電線 | 01 | 正在充電 |
| 使用者回覆已拔線、維持藍牙連線 | 00 | 未在充電 |
| 使用者回覆已插回充電線 | 01 | 正在充電 |

同時擷取的狀態查詢 `01 01` 回應長度為 59 bytes；比較三次內容，只有 body offset 2（從 0 起算）隨上述條件變成 `01 → 00 → 01`。正式程式使用獨立充電查詢，不依賴未完整解析的狀態封包偏移。

## 服務與封包

- 傳輸：Bluetooth Classic / RFCOMM。
- 已確認服務：`0cf12d31-fac3-4553-bd80-d6832e7b3136`。
- 唯讀充電查詢：`08 ee 00 00 00 01 04 0a 00 05`。
- 充電中回應：`09 ff 00 00 01 01 04 0b 00 01 1a`。
- 未充電回應：`09 ff 00 00 01 01 04 0b 00 00 19`。
- 封包總長度在 offset 7–8，以 little-endian 儲存。最後一 byte 是之前所有 bytes 加總後取低 8 bits 的校驗和。

另外查詢了 `01 03` 電量，回應為單一 byte `05`。尚未驗證此型號的刻度換算，因此沒有用它取代 Windows 的百分比。

`probe_glow.py` 只允許三個已知查詢：`01 01` 狀態、`01 03` 電量、`01 04` 充電；未送任何修改設定、重置、更新韌體或電源控制命令。`--only charging` 也已確認不需先送狀態查詢就能取得回應。

```powershell
python .\research\probe_glow.py --only charging --label manual
```

Python 探測器只使用標準函式庫。正式桌面程式以 C# 原生 Socket 實作同一查詢，不需 Python。

## 實作與限制

正式程式只查詢目前控制的喇叭，每 15 秒重新讀取。連線最多等 3 秒，連線後先等待 350ms 讓韌體通道就緒，再以 2.5 秒為限等待回應，必要時重送一次同樣的唯讀查詢。這個等待解決了背景查詢剛建立通道時收不到回應的實機問題。失敗則顯示未取得狀態，不保留舊的肯定結果。回應驗證包含方向標頭、命令、長度、校驗和及單 byte 0/1 旗標。

此旗標只代表充電中與否。未實測充飽後的行為，不能據此區分「拔線」和「插電但已充飽」，也沒有取得充電電壓、電流或充飽時間。未知旗標會被拒絕，不會自動解讀為已充飽。

原始 JSON 擷取資料包含本機裝置識別與狀態封包，保留在本機供對照，已加入 gitignore。

## 參考來源

開源實作的命令定義提供了查詢方向，實際支援狀況與 0/1 意義則由本機的拔插對照驗證。

- [OpenSCQ30 的充電查詢定義](https://github.com/Oppzippy/OpenSCQ30/blob/e75a68b70de8eaf2cf0ca364e132085a097557c5/lib/src/devices/soundcore/common/packet/outbound/request_battery_charging.rs)
- [OpenSCQ30 的充電回應解析](https://github.com/Oppzippy/OpenSCQ30/blob/e75a68b70de8eaf2cf0ca364e132085a097557c5/lib/src/devices/soundcore/common/packet/inbound/battery_charging.rs)
- [OpenSCQ30 的電池結構定義](https://github.com/Oppzippy/OpenSCQ30/blob/e75a68b70de8eaf2cf0ca364e132085a097557c5/lib/src/devices/soundcore/common/structures/battery.rs)
- [Microsoft SOCKADDR_BTH](https://learn.microsoft.com/windows/win32/api/ws2bth/ns-ws2bth-sockaddr_bth)：依服務 UUID 連線時由 Winsock 解析通道。

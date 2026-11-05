# PartyCast 身分與成員資訊研究（即時成員列舉尚未完成）

日期：2026-11-05，Asia/Taipei。

## 官方 App 測試模式實際呼叫追查（2026-11-05 19:01–19:06，最新）

正常 USB 開機對照後，繼續追查先前語意不明的 `CmmBtCmdService.B4(int)`。在既有研究模擬器內，確認飛航模式、無預設網路路由，並核對 ART 與 JNI reader 的 SHA-256 後，初始化並取出 4 個指定類別的 619 份方法 CodeItem；相較先前分析包有 203 份內容變更，其餘主要是原本就為空的預設 callback。未呼叫發送命令的方法，也未連接實體喇叭。

新增證據建立了實際鏈結：`AudioNoteCommonFlutterEvent.J0(Map)` 讀取 `flag`／`step` → `setDeviceTestMode` coroutine 將旗標轉成整數 → `AudioNoteCmdSender.A0(int)` 呼叫 `ISendCmdService.B4(int)`。共用 builder 的封包仍是 group `04`、command `8A`、一個旗標位元組。coroutine 同時更新 `D3200DeviceManager` 與 `onTestModeChanged`；`step` 在這段程式中是狀態通知參數，沒有傳給 sender。預設 `setDeviceTestModeStatusCallback` 確認是空方法。

這解決了「先前零引用是沒有 caller，還是尚未還原」的一部分未知：加入這 4 類後，REA 取得 1 個 `ISendCmdService.B4` 直接呼叫，含 DEX 指令位移。**實際找到的是 AudioNote／D3200 流程；未建立 A3136 的 USB／ADFU 切換，也未建立旗標值的韌體端效果。** 不能因為共用指令或「測試模式」名稱就對 Glow Mini 試送，亦不能反推其他型號都不支援。

分析包以原有衍生 APK 為基底，只替換核對過類別、方法索引與原始 CodeItem 位址的新增內容；DEX 雜湊及 ZIP 完整性通過，原始官方 APK 未修改。新增分析包 SHA-256 為 `ea8cee229380cabb1276af09503e35ae6d91e05f03bc4ad3102f54db3647f9f3`，僅靜態分析、未安裝。第一次合併因既有 lxml 的 Python ABI 不符而失敗，保留錯誤記錄後改用標準函式庫 patcher，沒有另裝依賴。

研究模擬器已正常結束，桌面程式仍在執行。**仍未取得 A3136 韌體，也未完成 PartyCast 即時副機資訊或真正成員名單。** [實際呼叫、證據與限制](firmware-acquisition/usb-maintenance-20261105/app-testmode/audit.json)／[16 筆完整 REA 證據](firmware-acquisition/usb-maintenance-20261105/app-testmode/rea-testmode-evidence.json)。

## 副喇叭正常開機 USB 對照完成（2026-11-05 18:44–18:51）

使用者首次回覆完成時，前兩輪記錄已結束，無法確認是否涵蓋實際開機，沒有把它們算作有效的開機負結果。18:44:10 重新開始記錄後，使用者再次確認已完成「副機正常關機、USB 拔插、等 5 秒、只按電源正常開機並保持插線」。回覆在 18:50:32 寫入紀錄；各個實體動作的精確時間未獨立量測。

- Windows 現存 USB 清單連續觀察 431 秒、2,078 次，零錯誤；最大取樣間隔 218.553 ms。原本 12 個 PnP 節點沒有變化，未觀察到額外的喇叭、未知裝置或維修介面。
- 追加依 Microsoft Windows SDK 結構編寫的唯讀 hub 查詢，使用零資料讀寫存取權，只呼叫 `GET_NODE_INFORMATION`／`GET_NODE_CONNECTION_INFORMATION_EX`。18:51:21 起的開機後檢查涵蓋 2 個 hub、22 個邏輯連接埠：3 個連線裝置均與既有滑鼠、鍵盤接收器及藍牙棒的識別相符，其餘 19 個回報沒有 USB 資料裝置；未見列舉失敗。12 次查詢、零錯誤。**這段 hub 檢查在開機後進行，不能稱為有捕捉開機瞬間。**
- 首次 hub 診斷誤把空連接埠回傳的 `ConnectionIndex=0` 當成錯誤；該欄位在 SDK 標示為輸入，修正成保留原始值後重新核對成功。失敗紀錄與修正後紀錄都保留。

本次確認的正常開機流程**沒有暴露可用的 USB 韌體讀取介面**。取樣仍可能漏掉短於間隔的變化，也沒有量測充電、資料線連續性或喇叭內部 USB 走線；因此不能推論所有隱藏維修模式都不存在。仍缺 A3136 專用的維修啟動條件與相容讀取協定，尚未取得韌體，PartyCast 副機即時資訊與真正群組名單仍未完成。

觀察程序已結束，桌面程式仍在執行。未改驅動、未傳送未知喇叭命令、未寫入韌體。[完整結果、範圍與雜湊](firmware-acquisition/usb-maintenance-20261105/usb-confirmed-boot-result.json)／[Microsoft 結構與查詢來源](firmware-acquisition/usb-maintenance-20261105/hub-header-evidence.json)。

## USB 維修入口追加逆向（2026-11-05）

使用者要求繼續尋找不拆機的 USB 維修模式。本輪新增了官方 Windows 更新器的 USB 切換／讀取流程、參考 SDK 的 USB 命令列接收鏈，以及官方 App 共用測試指令的靜態核對。**尚未驗證 A3136 的維修入口，也未讀出 A3136 韌體。**

- 官方 Anker Windows 更新工具的 `HardwareEx.dll` 有真正的 `SwitchToAdfu`、`ADFURead` 匯出。REA/Ghidra 已追查 21 個函式：維修切換需先有可開啟的 USB 裝置／儲存／MTP 介面，儲存分支用廠商 SCSI 指令；讀取函式只是具有位址／長度／模式參數的傳輸機制，尚未建立 A3136 的快閃位址、保護狀態或相容 loader。沒有執行更新工具或傳送其指令。[主機工具稽核](firmware-acquisition/usb-maintenance-20261105/host-tools/host-usb-maintenance-audit.json)
- 參考 SDK 的 USB 命令列接收鏈確有實作，可接到 shell 的 `flash_read` 分支；舊 Kconfig 中「USB input 不可用」的文字不足以否定它。但附帶的實際設定是 ATS2853P2 EVB，沒有啟用 USB console／CDC／MSC，不能代替 A3136 設定。開機按鍵進 ADFU 也是這塊參考板的應用程式判斷，沒有 Glow Mini 按鍵對應。25 份來源均核對 pinned Git blob 與 SHA-256。[SDK 入口稽核](firmware-acquisition/usb-maintenance-20261105/sdk-entry/audit.json)
- 16 組有界公開檢索沒有找到可驗證的 A3136 維修按鍵程序。另有 ATS2837 開發板專用 ADFU 鍵文件，但不是 A3136，也不是讀回程序。[公開來源稽核](firmware-acquisition/usb-maintenance-20261105/public/usb-maintenance-public-audit.json)
- 73 份已還原 Java 與 787 個既存選定類別宣告中，尚未建立藍牙切換 USB／ADFU 的路徑。`CmmBtCmdService.B4(int)` 的共用測試封包語意未確認；對衍生分析 APK 的 `ISendCmdService.B4` 追加靜態引用追蹤為零，不能推論未解密方法、反射或韌體內沒有入口。沒有試送未知測試封包。[App 稽核](firmware-acquisition/usb-maintenance-20261105/public/bluetooth-maintenance-static-audit.json)／[引用證據](firmware-acquisition/usb-maintenance-20261105/host-tools/rea-testmode-interface-refs.json)

新增唯讀 `UsbPresenceWatch`，每約 200 ms 記錄 Windows 現存 USB 裝置變化，不開裝置資料通道、不重設、不換驅動。首輪 600 秒取得 2,904 次觀察、零錯誤、無清單變更；**當時尚未收到使用者完成副機關機／接線／開機的確認，不能把它當成已完成開機對照或不存在維修模式的證據。** 已要求只測副喇叭的正常電源流程，未要求猜測按鍵組合；實機讀回仍取決於後續能否取得可用介面與相容的唯讀協定。

## 限定不拆機：官方請求實測與 Windows 驅動入口核對

使用者已明確回覆「沒有，繼續限定不拆機」，解決上一節尚待確認的硬體條件。接下來不走拆板、量測或焊接；本輪沒有發送任何喇叭命令、更新韌體、更換藍牙驅動或修改正式桌面程式。**仍未取得 A3136 韌體，副機即時資訊與真正的 PartyCast 成員列舉仍未完成。**

### 官方請求又補上了一項實際差異

在離線 Android 模擬器中實際執行官方 5.0.32 的 `RequestCommonInterceptor`，發現系統版本標頭是 `sys_version: Android 12`，早先手工請求是 `11`。首次測試因自建 Application context 不完整失敗，已保留當時輸入與記錄；修正 context 後取得完整標頭。這是隔離的訪客 context，不是登入後的完整官方操作流程；其 US/GMT 預設也不是使用者地區。

之後用官方 Gson、實際共用標頭及既有 ARM64 簽名 helper，固定 TW／Asia/Taipei 向已授權的官方全球韌體服務各查一次：

| 查詢版本 | 時間（UTC） | 實際回覆 |
| --- | --- | --- |
| 02.10 | 07:00:08 | HTTP 200／SUCCESS，`needUpdate=false`，`lastPackage=null` |
| 02.09（合成的較舊版本對照） | 07:16:39 | HTTP 200／SUCCESS，`needUpdate=false`，`lastPackage=null` |

02.09 只是查詢參數，不能證明該版本曾正式發行，實體喇叭仍是 02.10。這兩次與早先查詢不同之處是已核對的完整 Gson 欄位及實際共用標頭；沒有繼續重複換版號碰運氣。直接執行簽名 interceptor 的嘗試因 x86 `app_process` 無法載入 ARM64 library 而失敗，並未送出請求；後續使用既有、可透過模擬器 native bridge 執行的 activity helper 產生簽名，HTTP 傳輸與已驗證的 AES-CFB 仍由 Python 處理。因此不稱為官方 App 全流程重放。兩次皆無套件，仍不能推論伺服器沒有 A3136 映像。

### 公開下載來源

官方產品頁完整資料含 26 個下載項目，依標題與網址為 25 份手冊／指南及 1 份聲明，沒有辨識到韌體檔；沒有檢查所有 PDF 的內嵌附件。已知 CDN 的一次公開 urlscan 檔名前綴查詢為零筆；Common Crawl 最新索引的一次指定篩選回傳 HTTP 504，不能算成「沒有收錄」。未提交掃描、登入索引服務、猜測 bucket 或大量試檔名。[公開來源紀錄](firmware-acquisition/noninvasive-followup/public/public-followup.json)／[追加索引紀錄](firmware-acquisition/noninvasive-followup/public/additional-public-indexes.json)。

### 直接逆向已安裝的 Windows 藍牙驅動

文件研究未建立讀取本機 LMP feature page 2 的正式桌面入口；因此進一步靜態分析這台電腦的 `Bthport.sys` 10.0.26100.8655（SHA-256 `6750a4ea8acec328ec615863cc1a10b5808be0beacbf8f9f329fa340ded38d62`），未載入或修改研究副本。

已從微軟符號服務取得對應 PDB。GUID 與驅動相同，PDB Info age=4、DBI age=1、PE age=1，依微軟公開的 PDB 驗證實作核對；初次把兩個 age 一律要求相同的檢查失敗已明確記錄。PDB 只用來定位候選，行為另以 Ghidra 對實際驅動的反編譯及組合語言核對。

確定 `BthHandleControlDispatch` 的 `0x410050` 分支會進入 `HCI_VendorCommand`。該函式要求 `(opcode & 0xFC00) == 0xFC00`；不符就設定 `0xC000000D`，後續錯誤分支會略過 `HCI_SendCommandEx`。因此標準的本機延伸功能查詢 `0x1004` **無法走這個廠商命令入口**。這補上前一份文件研究留下的「實際驅動是否檢查 opcode」未知項，不是只依 API 名稱推測，也沒有真的發送 IOCTL。

此結果不代表藍牙棒一定沒有 CSB 接收能力，也不代表所有其他私有介面都不存在；LMP page 2 仍未取得，不能用 HCI/LMP=0x09 直接認定支援。即使取得接收能力，也不等於已有 A3136 群組名單或私有回報解碼。[驅動核對、位址與完整證據](firmware-acquisition/noninvasive-followup/controller-capability/driver-analysis/driver-gate-audit.json)。

本輪核對 38 個既有雜湊，總索引涵蓋研究程式、原始回覆、符號檔及 REA 證據。兩次臨時 helper 均移除，兩次模擬器均正常結束，REA 原生分析已匯出並關閉。詳見 [不拆機調查總索引](firmware-acquisition/noninvasive-followup/final-audit.json)／[驗證紀錄](firmware-acquisition/noninvasive-followup/verification.json)。下一個能解除核心限制的證據仍是真實 A3136 映像或適用此型號的可驗證讀取介面；已備妥 [官方詢問信草稿](firmware-acquisition/noninvasive-followup/support-request-draft.eml)，**尚未寄送、未包含序號，也未取得對外寄信授權**。

## iPhone 更新歷史、硬體入口與 ROM 範圍核對（14:15 起）

使用者確認主機**從未更新，買來就是目前 02.10 版本**。因此沒有這支 iPhone 的已知歷史更新下載可追；未讀取手機檔案或建立備份，也不將這句回覆延伸成「App 絕不會預先下載」或「伺服器沒有韌體」。[使用者回覆記錄](firmware-acquisition/iphone-update-history-observation.json)。

已重新渲染並檢視 FCC 8361615 報告第 1 頁，配合既有第 2 頁影像。認證樣機可見 `A3136_MAIN_PCB_V4.0`、`2023/7/19` 與 ATS2835P；照片無法確定 UART 測試點或 USB D+/D- 走線，更不代表使用者這顆的實板已量測。正常開機 USB 測試仍以先前快照為準，沒有新增插線或切換模式測試。

從固定版本 Actions 參考倉庫取得 12 份文字來源與 `librom_code.a`，13 份全部通過 Git blob SHA-1 與獨立 SHA-256 核對。這是 **ATS2853P2 EVB 參考資料，不是 A3136 韌體**。本輪確認：

- `uart.bat` 對找到的 COM 連接埠發送文字檔，參考設定為 3,000,000 baud；`uart_adfu.txt` 內容是 `kernel reboot adfu`。它需要已存在、可輸入命令的 shell，會重開機，並非從藍牙或未列舉的 USB 直接讀取韌體。未執行這些腳本。
- JTAG 腳本需要已連接的除錯器，會寫暫存器後繼續執行；參考板也會依組態開關 JTAG。未建立 A3136 的接點、啟用狀態或除錯器連線。參考板的 ADC 按鍵開機檢查不能當成 Glow Mini 維修按鍵組合。
- ROM library 共解析 16 個 ELF 物件。`soc_rom_csb.o` 只有一個 10-byte 的 `rom_get_csb_fsm_api`；比對同庫的已列印指令與 ROM 表格欄位後，可確認它只讀出 `p_csb` 函式表指標。沒有該指標指向的 ROM 內容。CSB codec 物件另有七個短轉接函式，不能當成完整廣播協定實作。

這補上了先前 CSB 覆蓋不足的具體原因之一：部分實作另放於 ROM；**不能用參考套件內未見成員清單，推論 A3136 絕無私有回報通道**。未來取得真實映像後，若也呼叫 ROM，還需核對對應 ROM 版本與內容。

詳細來源、二進位位址、雜湊與執行範圍見 [硬體與 ROM 核對](firmware-acquisition/readout/hardware-followup/audit.json)。本輪只有靜態分析，沒有喇叭命令、寫入韌體、改驅動、啟動模擬器或修改正式桌面程式。**A3136 韌體仍未取得；副機即時資訊與群組成員列舉仍未完成。** 已詢問現場是否有熟悉電路板拆裝及萬用表量測的人，僅確認下一步條件，尚未要求拆機或接線。

## iPhone 最新版本確認與重新下載入口核對（14:00 起）

使用者回覆 iPhone 官方 Soundcore App 顯示主機 **02.10 已是最新版本**。這與先前電腦工具「沒有更新套件」的可見結果一致，但沒有擷取 iOS 的實際請求或回覆，不能因此宣稱兩者所有帳戶／地區條件完全相同。[使用者觀察記錄](firmware-acquisition/iphone-current-version-observation.json)。

進一步檢查全部 25 個本地 5.0.32 解包 DEX 的類別宣告，共 82,059 個不重複類別名稱；以韌體、修復、回復、降版等名稱找出 107 個候選。這是宣告名稱清單，不代表 82,059 個類別的方法都已解密。路徑字串篩選得到 12 筆候選，未找到新的 A3136 修復／版本歷史入口；`A3600/firmware/list`、A3910 及音訊庫 `sound_mix` 不能直接改型號後套用。沒有對其他型號或猜測的 URL 發送查詢。Windows Downloads 的限定檔名搜尋也沒有 `3136`／`Glow`／`.fw`／`.OTA` 候選。

對已還原的更新流程另取得四筆新的 REA 方法證據，確認：

- `downLoadFirmwareFile` 直接使用 `lastPackage.url`；沒有網址就返回。`USA282A_A3161.OTA` 是 App cache 內的本機檔名，不是可據以猜測的伺服器檔名。
- `handlerServiceFirmwareData` 只在 `needUpdate=true` 時處理 package／MD5／快取。沒有更新時顯示最新版；現有快取也不是此流程繞過缺少 package metadata 的入口。
- `updateForceOtaStatus` 只計算強制更新的 UI 限制；`getFURequestBody` 沒有把首頁強制旗標變成重下目前韌體的請求欄位。重試按鈕仍呼叫同一個版本查詢；What's New 只讀回覆內的 change log。
- 共用 `updateCompleteNext` 會清空快取 MD5 並嘗試刪除快取檔。尚未還原 Actions 父類的 `onUpdateComplete` 全部內容，因此不宣稱每一次 A3136 更新都必定走到這個刪除點，也不能將 Android 行為直接套到 iPhone。

已透過 DEX class_def 確認 `A3398OtaActivity → ActionsOtaActivity → OtaBaseActivity`；REA 對 Actions 父類的完整方法宣告清單未見下載、快取路徑、請求模型或強制旗標方法的 override。此輪沒有啟動模擬器或傳送新的喇叭命令。

完整來源、限制、固定副本與 REA Evidence IDs 見 [目前版本取得路徑稽核](rea-current-firmware-recovery-audit.json)。新證據為 `ev_c966b0d77c9820950b01f40f0eaa5fc3634ab278848bce65c161dd7accdfd4a0`、`ev_76e42dc670a6c86790577e90b2316abd11907896118742bce9e8b4e8c4ebe0f4`、`ev_69d2916a1db85d8441bdbbbe395bb6f02933fd992399e5910b3a79148e33cac7`、`ev_e082d8625c11d4111cc57f974265085a89b07dfa26c9bed3c438face10766b83`；四個方法均為完整 Java、無 fallback。

**仍未取得 A3136 映像，副機即時資訊與群組成員列舉仍未完成。** 後續使用者已確認從未更新主機，詳見本頁最新一節，沒有已知歷史下載可追。也不能保證 iPhone 有保留檔案或能從一般備份取得：Apple 文件指出 cache／tmp 不包含在備份中，檔案共享也需要 App 支援；尚未證明 Soundcore iOS 使用哪個保存位置或支援共享。[Apple 檔案系統說明](https://developer.apple.com/documentation/foundation/using-the-file-system-effectively)／[Windows 檔案共享條件](https://support.apple.com/en-mt/120402)。

前一輪總索引的 Markdown 證據已改指當時的固定副本 `firmware-acquisition/ledger-snapshots/PARTYCAST_MEMBERS-20261105-1358.md`，保留其原 SHA-256，避免本節更新讓歷史證據失效。下節原「等待 iPhone 最新版本」為當時狀態，現已由本節回覆解決。

## A3136 韌體取得、USB 與參考 SDK 私有通道（13:13 起）

**尚未取得 A3136 韌體映像，也尚未完成 PartyCast 副機即時資訊或真正的群組成員列舉。** 本節是新一輪已完成的調查，不是功能完成宣告；桌面程式沒有將參考 SDK 或歷史資料冒充即時結果。

### USB 資料線實測

使用者確認主喇叭以可傳資料的 USB-C 線接到電腦、維持開機。接線前、接線後與穩定後均觀察到 10 個 USB VID/PID 裝置；新增／移除的 5 組識別均屬 `3434:D030` 的複合裝置與 HID 介面，BusReportedDeviceDesc 為 `Keychron Link`，是接收器換埠，不能歸到喇叭。後續未出現額外 USB 裝置。

本次正常開機狀態沒有可辨識為 Glow Mini 的 USB 資料介面，因此不能進行 USB 韌體讀回。這不證明資料線路不存在，也不排除尚未確認的維修模式。沒有猜測按鍵組合、進入 ADFU、傳送 USB 命令或更換驅動。詳見 [USB 結果與三份原始快照雜湊](firmware-acquisition/usb-readout-result.json)。

### 官方韌體查詢已補齊並實際核對

隔離、無藍牙且飛航模式的 Android 模擬器中，實際呼叫官方 5.0.32 APK 的 Gson，證實請求包含 `product_component=""`、`product_language=""`、`relation_sn=""` 三個欄位，早先手工請求省略了它們。另以虛構序號與測試 key，實際呼叫官方 `AesSafeUtil.e/d/b`，驗證衍生 key 一致、官方可解 Python 密文、Python 可解官方密文，三項均通過。這些是實際執行結果，不只是反編譯推論。

補齊 JSON 後的全球 02.10／00.00 查詢、CN host 比較、以及依新還原 `AccountBaseUtil.q → AreaUtil.e` 補齊 `X-App-Key: soundcore.cn` 的 CN App profile 比較，均 HTTP 200／SUCCESS，但 `needUpdate=false`、`lastPackage=null`。CN profile 使用官方的區域分支，系統 country 仍為 TW；不表示使用者的帳戶或喇叭已改區。僅將已授權的主機序號傳給兩個官方服務。

回覆**沒有型號或版本回顯**，SUCCESS 不足以證明伺服器採納所有篩選條件，也不能證明韌體不存在。匿名 A3027／00.00 控制沒有使用任何喇叭序號，回傳應用層 500／服務暫時無法使用，未形成有效的正向控制。已還原流程未顯示訪客韌體查詢必須登入；正常 App 是否已建立 visitor UID，仍是未確認差異。沒有重複相同失敗查詢。

證據：[完整查詢稽核](rea-firmware-query-audit.json)、[官方 Gson 執行記錄](firmware-acquisition/serializer/runtime.log)、[加密雙向互通結果](firmware-acquisition/serializer/cipher-interop-result.json)。稽核核對 61 個檔案雜湊；在後續訪客流程還原前，已將這 61 份確切輸入另存於 `firmware-acquisition/query-audit-snapshot/`，稽核的 evidence_files 指向保留副本。

接續還原訪客流程後，確認 `SoundCoreMainVM.getThingVisitorInfo → ThingVisitorManager.g` 啟動背景協程後立即返回，成功時儲存 `guest_id`；已還原 A3136 韌體檢查不會呼叫／等待該管理器，也沒有 UID 前置檢查。因此沒有證據將缺少訪客註冊視為本次空回覆的原因，沒有新建訪客或重複查詢。部分 callback 與其他受保護方法仍未知，這不是伺服器對所有帳戶行為的證明。[訪客流程稽核](rea-visitor-uid-audit.json) 保存確切方法與原始 DEX annotations。

已建立新的靜態分析 APK `android-analysis/rea-catalog-analysis/`，含 86 個還原類別、3,086 個方法，內容衝突 0，SHA-256 `5a68c83350dcda432f6e3c0c75d64158bb9b086247a92c909b3b9c9a3a8ac29f`。原始官方 APK 與先前 `rea-wire-analysis` 均未修改。REA 實際檢視兩個訪客入口，Java 方法完整、沒有 smali fallback：`rea-visitor-uid-entry-method.json`（`ev_f6666d719fbc687995c796ed1d92addeb77e97f094edbc6f385a866edcb2e2a3`）、`rea-visitor-uid-launch-method.json`（`ev_96f0dd0d1aa55f4783f2b2d6364337864a82fc8d89e446ac8c527b12f397c18f`）。內嵌未還原 callback 的範圍限制仍保留，不把衍生 APK 當作完整解密或官方簽署的安裝包。

### 公開映像與下載工具

已檢查官方 APK 3.9.1／5.0.32 的檔案清單與候選巢狀資源；未找到可辨識的 A3136 韌體。官方 [SoundcoreSDKDemo](https://github.com/AnkerInnovations/SoundcoreSDKDemo/tree/69d3065284003686b89c6e6bf4172dca796973f1) 的 Android AAR 與 iOS binary/header 已取得並核對 Git blob／SHA-256，但已檢視 OTA 範例使用本機檔案，倉庫內更新測試檔屬 D3200，不能作為 Glow Mini 韌體。

官方 Windows 更新器 ZIP 已下載並用 REA/Ghidra 靜態分析。83 個 CAB 成員沒有獨立韌體，檔案選擇流程要求使用者提供 `*.fw`；未取得 A3136 下載目錄。未安裝或執行更新器。公開韌體封存只找到其他型號，指定 CDN 的 Wayback A3136 篩選為空；這些僅是指定索引內的負結果，不是整個網路或伺服器的完整搜尋。

詳見 [公開來源紀錄](firmware-acquisition/public-search.json)、[Windows 更新器分析](firmware-acquisition/windows-updater/analysis.json) 與其 15 筆 REA 證據。

已知 CDN 的 `upgrade/prod/` 目錄直接讀取為 HTTP 200、零 bytes；對同一 CDN 的標準公開索引請求只回傳簡單 HTML，沒有 `ListBucketResult` 或檔案列表。沒有因此猜測來源 bucket、憑證或大量檔名。這是無可用公開索引，不代表目錄內沒有檔案。記錄：`firmware-acquisition/public/cdn-directory-observation.json`、`cdn-public-index-observation.json`。

### 找到參考 SDK 私有通知，但不是已證實的 A3136 讀取入口

固定版本的 Actions 參考 SDK 存在實作中的 TWS peer-to-peer 通道：BR/EDR L2CAP 固定 CID `0x001F`，框架為 `EE CRC8 [type cmd lenLE payload]`。副機的電量／電壓事件是 type `02`、cmd `06`、4-byte payload。來源、編譯後 C-SKY 指令與 library relocation 能互相對應。

這是副機向主機傳送的**通知**；接收端會更新單一 `slave_cap/slave_vol` 快取，不能把相同封包當成電腦查詢。此事件沒有序號、充電旗標或群組名單。`app btinfo` 能顯示 ACL 裝置的位址、名稱、角色與 profile，但它是本機 shell，尚未找到從 A3136 的既有 Soundcore RFCOMM 通道到該 shell／固定 CID 的轉接。

參考 SDK 的 BLE 停止廣播由軟體政策控制，所檢視函式不會主動中斷已存在的 BLE 連線；不能說副機硬體必然關閉 BLE。CSB 參考實作覆蓋不足，包含 TODO／停用部分及一份未完整解析的 archive；也不能用它證明 A3136 沒有私有回傳通道。

詳見 [私有通道證據與限制](firmware-acquisition/readout/csb-sidechannel-audit.json)：36 個來源檔雜湊、60 組有效行號、6 個已核對二進位函式位置。這些線索可用來追查未來取得的真實 A3136 映像，目前不足以在喇叭上發送新指令。

### 目前待取得的關鍵證據

已請使用者在 iPhone 官方 Soundcore App 只查詢主機的「設定 → 更新韌體」，暫不安裝，以對照是否同樣顯示 02.10 最新。仍待回覆。若官方 App 能取得套件，下一步是取得該實際下載或快取；若同樣沒有套件，仍需真正 A3136 映像或可驗證的裝置讀回介面，不能把其他型號韌體當成成功取得。

本輪沒有新增喇叭控制指令或修改正式桌面程式。臨時訪客查詢 helper 已移除；第一次驗證與後續訪客程式碼還原所開的隔離模擬器均已正常結束。[首次清理](firmware-acquisition/runtime-cleanup-20261105.json)／[後續清理](firmware-acquisition/visitor-runtime-cleanup-20261105.json)。本輪總索引為 [取得調查結果](result-a3136-acquisition-20261105.json)。

## 副機 Fast Pair 既有連線與入群後重連對照（12:58–13:09）

使用者完成副機配對並確認尚未加入 PartyCast，之後依提示將副機加入，確認兩顆同步播放。本次已完成先前等待的實機對照：**副機既有的 Fast Pair 通道在入群過程中關閉；確認同步播放後重新連線亦逾時。未取得 PartyCast 內的副機即時資訊或自動成員名單。**

| 階段 | 已取得的證據 |
| --- | --- |
| 副機已配對、尚未入群 | Windows Remembered／Authenticated=true，初始音訊 State=8。12:59:03 成功連上副機 Fast Pair UUID，收到型號 `008f7d` 及副機 BLE 位址 `cb9d8a0ed2bb`。 |
| 入群前持續通訊 | 共發送 117 次 `03 05 00 00`、收到 117 筆 `03 06 = 03`；加上型號與位址，共 119 個完整訊息、602 bytes，重新串接原始資料後全部可解析，無殘留。`03` 不能視為 PartyCast 喇叭數。 |
| 音訊與輪詢干擾控制 | 12:59:10 Windows 自動重連副機音訊並切為預設；13:01:14 已在音效設定切回原主機。桌面程式起初也有自動輪詢，因此不能將初始音訊重連單獨歸因於 Fast Pair。發現主機 PartyCast 為關閉後，已透過既有 `01 A4` 控制開啟，13:03:59 收到確認；13:04:20 前後正常關閉桌面程式，後續副機 Fast Pair 仍持續回覆。 |
| 入群過程 | 13:05:10.733 Fast Pair `recv` 返回空資料（遠端關閉）；13:05:10.853 音訊監看記錄副機轉為 State=8。使用者於 13:06:17 的確認表示兩顆已同步播放；實體按鍵的精確時間未知。 |
| 確認入群後重連 | 13:06:17–13:06:22 對副機同一 Fast Pair UUID 重連，Windows 回覆 10060 逾時，應用封包傳送數 0。 |
| 同工具正對照 | 接著對主機 Fast Pair 連線成功，收到主機正確 BLE 位址及 3 次元件回覆。另以一般 Soundcore 通道取得新的 59-byte `01 01`，校驗有效，PartyCast 旗標為 1。 |

10 分鐘音訊監看共 120 筆紀錄；副機通道關閉後的約 220 秒內，主機保持音訊連線、原主機為預設輸出，副機直接音訊沒有重連。測後兩顆配對均保留，副機 Remembered／Authenticated=true、Connected=false；已重新啟動桌面程式，所有研究擷取程序均已結束。沒有移除配對或變更藍牙服務驅動。

本次 Fast Pair 訊息沒有電池更新 `03 03`、剩餘時間 `03 04`、韌體 `03 09` 或群組成員資料。此結果驗證了「先連上副機 Fast Pair，再加入 PartyCast」在本次環境下仍無法保留通道；不能擴大解讀為所有韌體私有通道都不存在。標準訊息定義參見 [Google Device information](https://developers.google.com/nearby/fast-pair/specifications/extensions/deviceinformation)。

完整統計、操作邊界、原始檔 SHA-256 與限制見 [本輪結果](result-fastpair-peer-20261105.json)。原始記錄：`fastpair-peer-transition-20261105.jsonl`、`fastpair-peer-postjoin-reconnect-20261105.jsonl`、`fastpair-main-positive-postjoin-20261105.jsonl`、`audio-fastpair-peer-transition-20261105.jsonl`、`fastpair-peer-transition-events-20261105.jsonl`。最終快照為 `link-fastpair-peer-final-20261105.json` 與 `classic-fastpair-peer-final-20261105.jsonl`。下方先前「等待副機配對」的文字是歷史進度，本節已完成該對照。

## REA 安裝與進一步逆向（11:39 起）

使用者指定 [morluto/rea](https://github.com/morluto/rea) 後，已下載儲存庫 `71f680d7c1aa7a45ba0325fe87b104744b0c105f`，檢視 README、AGENTS、安裝／Android／Windows Ghidra 指南、隨附工作流程，以及安裝和分析提供者的相關程式碼。這不是對儲存庫所有程式碼的完整稽核。

- 已在 `%LOCALAPPDATA%\SoundcoreResearchTools` 安裝 REA 6.8.0、Node 24.22.1、Temurin JDK 21.0.13+9、JADX headless MCP 0.7.1、Ghidra 12.1.4，並完成 Frida 17.23.2 原生依賴安裝與載入檢查。下載封存檔均與官方／REA 固定的 SHA-256 核對。`research/Invoke-Rea.ps1` 與使用者 PATH 的 `rea.cmd` 固定使用這些工具。
- REA setup 已加入 Codex 註冊、保留 `%USERPROFILE%\.codex\config.toml.rea.backup`，並安裝 `.agents/skills/reverse-engineer-anything`。`rea mcp doctor` 實際啟動 production stdio server，229／229 tools 與 6／6 prompts 相符，`binary_session` 呼叫成功；不是只檢查設定檔存在。本次分析透過 CLI 執行，未要求重啟目前對話。記錄：`rea-setup-result.json`、`rea-doctor-installed.json`、`rea-mcp-doctor.json`。
- REA/JADX 已成功分析原始官方 APK，確認套件 `com.oceanwing.soundcore`、版本 5.0.32。原封裝內只呈現 301 個類別，因此另建立**只供靜態分析、不安裝、不具官方簽章**的衍生 APK；合併現有 5.0.32 的 73 個類別、2,757 個執行期還原方法，沒有方法內容衝突。衍生 APK 的 26,165 個類別是選取 DEX 載體內的宣告數，不代表全部方法已解密。原始 APK 未變動；完整來源雜湊與範圍見 `android-analysis/rea-analysis/recovered-analysis-provenance.json`。

### Glow Mini 的 PartyCast UI 路徑已進一步還原

新增還原 `A3136HomeMusicVM`、`A3136HomeMusicFragment`、其 `eventAdapter`，共 42 個方法。PartyCast 操作只走 `setPartyCastStatus → Cmm2BtDeviceManager.F6 → CmmBtCmdService.a1 → 01 A4`；回呼更新目前裝置的 `isPartyCastEnable`，資訊按鈕則開啟操作指南。在這些方法內未找到副機集合或追加的成員查詢。不能據此排除韌體內未公開功能。

REA 實際反編譯證據：`rea-partycast-ui-method.json`，Evidence ID `ev_717351533df47d36d3ec0f2edc0e6ba41ccd642f87756b52848ad7e08c8708cd`。完整靜態路徑與行號：`rea-audit-partycast-code.json`。DEX 裡的 `slaveDeviceList`／`slaveDeviceMac` 已定位為其他型號的音箱／耳機功能，不能直接套用到 A3136。

### 新的來源支持查詢：0B 01 音源連線歷史

已從還原的 `Cmm2CmdData.<clinit>` 確認 `L1 = 08 ee 00 00 00 0b 01`，`CmmBtCmdService.b3()` 建立空內容查詢；完整封包為 `08 ee 00 00 00 0b 01 0a 00 0c`。共用解析器把回覆解讀為音源裝置歷史（MAC、phoneName、connectedStatus），**並非已確認的 PartyCast 成員查詢**。REA 對介面 `ISendCmdService.b3` 的引用追蹤確認 `Cmm2BtDeviceManager.T1` 在 DEX code-unit offset 2 使用 `INVOKE_INTERFACE`；直接查具體類別的使用者為零不代表沒有呼叫。證據 `rea-history-interface-references.json`，ID `ev_37aa174de254841f068edaef343212d4728ed9d33e8e5b5cd976f4c0a816907a`。

首次測試與暫停桌面程式後的重試都在 RFCOMM 建立連線時逾時，未送出任何應用封包。當時主機音訊端點已連線但非預設輸出；使用者回報無聲並自行重開，隨後確認恢復。重開後主機再次成為預設輸出，實測順序是 `01 01 → 0B 01 → 0B 01 → 01 01`：兩次 `0B 01` 在各 2 秒觀察窗均無資料；前後的 `01 01` 都是校驗正確的 59 bytes，49-byte 內容完全一致、PartyCast 旗標為 1。這是本次條件下無回覆，不能擴大解讀成任何條件下都不支援。檔案：`rea-main-connection-history-after-restart-20261105.json`、`link-rea-after-restart.json`。沒有送 `0B 03`：官方程式把它用於寫入帳號名稱，不是安全的空內容查詢。

### 已核對最後一層封包組裝與前置條件

接續在隔離 Android 模擬器還原 `BaseBtDeviceCommand`、`SppLink`、`CheckSumUtil`，共 3 個類別、44 個方法。A3136 的有效管理器建立 `BaseBtDeviceCommand(true)`，因此 `T1 → X → f → G` 會將 7-byte 命令範本加上總長度、可選內容、最後的檢查碼，再交給 `SppLink.w`。`SppLink.d` 直接寫入 RFCOMM stream。這與已成功的 `01 03` 電量、`01 04` 充電讀取是同一條路徑；已檢查的方法沒有依 `0B 01` 追加登入、加密、握手、目標 MAC 或啟用回報的步驟。`U(frame)` 註冊的是 App 端回覆／逾時處理。`BytesUtil` 的底層位元轉換函式仍未另外還原，校驗和格式另有成功 `01xx` 封包互證。

新的靜態衍生 APK 位於 `android-analysis/rea-wire-analysis/`：76 個還原類別、2,801 個方法，方法內容衝突為 0，SHA-256 `fb4cc10b0137a40e84e95d1e4f4a75eb2330ec9614a4ae607b77063e13ac0421`。前一份衍生 APK 的 SHA-256 仍為 `8a08c3366bf09d3c2dc724facf9ccf29db25709ec7cdc09b5eaf0c50b0cca462`。REA 對 `G`、`CheckSumUtil.c` 與 `CheckSumUtil.a` 都回傳完整 Java 方法、無 smali fallback；最後一項已透過本次對話新可用的 REA MCP 直接呼叫成功。

證據：`rea-wire-format-method.json`（`ev_605ee5f498fb63a0488fcf7f8b883984df19ec62970b318cc6c09fca8c177eac`）、`rea-checksum-method.json`（`ev_60b3c9a23d629310ae3f42928cb40055160f31fae148f1980759d8cfb05b7dc6`）、`rea-checksum-accumulator-method.json`（`ev_b6ceda42043cace9a637a22785ff25049afa4a0f8957e3660ba977aeaafd9548`）。呼叫鏈、來源行號及雜湊副本見 `rea-audit-history-wire.json` 與 `rea-audit-history-sources/`。

因此，目前沒有程式碼證據可將 `0B 01` 的無回覆歸因於缺少已知握手；這仍不能證明 A3136 韌體支援這個共用的音源歷史功能，或排除韌體內部未公開通道。現場最後的只讀快照 `link-rea-wire-audit-final-20261105.json` 顯示主機已連線且為預設音訊輸出，副機在 Windows 記錄中尚未配對。副機 Fast Pair 的「先建立連線、再加入 PartyCast」對照仍待現場配對完成。擷取工具已備妥；本次隔離模擬器已正常結束。

### 原生函式庫與額外通道

REA 對原始 APK 的 ARM64 `libscnativelib.so` 匯出表分析成功（916 個匯出，檢查範圍 complete），Evidence ID `ev_6e78ae5d69ea7f7db83165d263d37af2864d575ad65424720913bfc012494d86`。REA Windows 的 Ghidra 包裝不接受 ELF，因此另外使用 Ghidra 本身對同一未修改函式庫做靜態分析；找到的 `A3136` 字串有 11 個函式引用，皆為 `LightEffectUtil` 燈效計算。這是一般 Ghidra 產出，不冒充 REA Evidence。輸出 `android-analysis/rea-analysis/ghidra-a3136-xrefs.txt`。首次腳本目錄過大導致編譯混入其他研究 Java；改用獨立 `research/ghidra-scripts` 後已成功執行。Ghidra 匯入仍有例外表範圍警告，不能宣稱全程無診斷或整個函式庫已完整理解。

另在副機目前未配對、主機 PartyCast 旗標仍為開啟的條件下，嘗試只被動連接副機 Fast Pair RFCOMM UUID，回報 10060 逾時，應用封包傳送數為零。主機旗標不能單獨證明副機當下仍在群組。這不排除「先配對並建立副機 Fast Pair，再進入 PartyCast」的不同條件；記錄 `rea-secondary-fastpair-partycast-unpaired-20261105.json`，下一項實機對照正等待副機退出並完成配對。

利用已授權的主機序號，另比對官方 v2 韌體請求模型中的 `matched=true`（02.10、00.00）；兩次皆 HTTP 200／SUCCESS，仍 `needUpdate=false`、`lastPackage=null`。這個欄位的伺服器語義未驗證，不把它描述為確定的強制下載功能。沒有取得 A3136 韌體映像。記錄 `android-analysis/a3136-firmware/global-v2-*-matched.decoded.json`。

**本輪尚未完成即時副機資訊與自動成員列舉。正式桌面程式未新增無證據的即時欄位，也沒有把歷史紀錄改成目前連線狀態。**

## 私有通道直接逆向與實測（10:27–11:24）

本輪已還原更多官方 App 方法、讀取主機完整 BLE 服務表、測試額外的 Fast Pair RFCOMM 通道，並完成副機退出／重新加入的對照。**仍未取得可用的 PartyCast 副機即時資料或自動成員名單。** 喇叭本身的 A3136 韌體映像仍缺少，以下將實機觀察、App 程式碼與晶片 SDK 的推論分開記錄。

### Fast Pair 通道有回覆，元件旗標無法區分本次副機離群

已在主喇叭的 `df21fe2c-2515-4fdb-8886-f12c4d67927c` 建立獨立連線。被動接收得到 `03 01` 型號 ID `008f7d` 與 `03 02` 主機 BLE 位址，位址與已確認主機相符。再依 Google 文件發送唯讀的 Active components request `03 05 00 00`，收到 `03 06 00 01 03`。該規格將回覆定義為元件位元遮罩，必須另外驗證與 PartyCast 成員的對應關係。[Google Message Stream](https://developers.google.com/nearby/fast-pair/specifications/extensions/messagestream) · [Google Device information](https://developers.google.com/nearby/fast-pair/specifications/extensions/deviceinformation)

連續記錄從台灣時間 11:18:38 至 11:23:35，共 95 次查詢、95 筆元件回覆，數值全部為 `03`。使用者確認副機退出且保持開機，再確認重新加入並恢復兩顆同步播放。為避免把操作過渡期混入，另外取出下列保守區間。

| 確認狀態 | 納入的元件回覆 | 結果 |
| --- | --- | --- |
| 發出退出提示前的最初 8 秒 | 3 筆 | 全部 `03` |
| 收到退出確認後的 35 秒，早於重新加入提示 | 11 筆 | 全部 `03` |
| 收到重新加入確認後，至擷取結束 | 23 筆 | 全部 `03` |

這次連線共 97 筆完整訊息，另兩筆是型號與主機 BLE 位址，未解析殘留為 0 bytes。此數值在已確認的入群、離群狀態都相同，無法拿來實作本次副機數量或即時群組狀態。記錄也沒有電量更新 `03 03` 或副機識別訊息。使用者操作的實際按鍵時間未知，時間標記是電腦收到確認後的記錄時間。

工具與證據保存在 `capture_fastpair_components.py`、`private-fastpair-passive-20261105.json`、`private-fastpair-components-20261105.json`、`private-fastpair-transition-20261105.jsonl`、`private-fastpair-transition-events-20261105.jsonl`。本輪統計與 SHA-256 見 [私有通道結果](result-private-channels-20261105.json)。

### 額外 BLE 通道與 OTA 的關聯

主機以 Uncached 完整列舉成功，服務為 `1800`、`FE2C`、已使用的 `0171f5da-0000-1000-8000-00805f9b34fb`，以及 `e49a25f8-f69a-11e8-8eb2-f2801f1b9fd1`。官方 App 的 `A3136ProductModel.getServiceUUID()` 宣告 `014CF5DA`，本次完整列舉未出現該值。兩者差異尚未取得 A3136 韌體解釋。

額外服務的 `e49a28e1-…` 具有 Read／Notify，`e49a25e0-…` 具有 Read／Write／WriteWithoutResponse。兩個 Uncached Read 都成功，各取得 497 bytes 全零資料。隨後通知訂閱遭 Windows 回傳 `0x800704C7`，故這輪未建立私有通知接收。錯誤文字雖為操作取消，實際原因尚未確定，不能據此判定使用者按了取消或裝置拒絕授權。此私有通道的應用層寫入數為 0。

Actions 廠商開放的 LVGL SDK 原始碼明確將這三個相同 UUID 命名為 `OTA_SERVICE_UUID`、`OTA_CHA_RX_UUID`、`OTA_CHA_TX_UUID`，並交給 `ota_backend_bt_init`。其 `ota_backend_bt_read` 會透過 `OTA_CMD_D2H_REQUIRE_IMAGE_DATA` 向更新主機索取映像片段。這給出該通道用於 OTA 的具體線索，A3136 實際採用哪個實作仍待其韌體確認。[Actions OTA 服務原始碼](https://github.com/lvgl/lv_port_actions_technology/blob/26f51e584940d5b141cd1d09d2629d69b595f579/action_technology_sdk/application/app_demo/lvgl_demo/src/ota_app.c) · [Actions OTA 傳輸後端](https://github.com/lvgl/lv_port_actions_technology/blob/26f51e584940d5b141cd1d09d2629d69b595f579/action_technology_sdk/framework/ota/ota_backend_bt.c)

另已由官方 Soundcore App 的 `A3398OtaActivity.getUUID()` 確認，A3136 的 Classic OTA 通道為 `0CF12D31-FAC3-4553-BD80-D6832E7C3136`，符合實機既有 SDP 記錄。一般控制通道使用 `…2E7B3136`。本輪僅追蹤 OTA 程式與服務宣告，OTA 升級請求數為 0。

實機證據為 `private-main-all-gatt-20261105.json`、`private-main-notify-20261105.jsonl`。研究工具 `list-glow-gatt.ps1` 新增完整服務列舉及指定輸出檔功能，`BleInfoProbe.cs` 新增獨立私有服務的讀取／訂閱模式。

### 韌體取得與晶片核對

Anker 的 2023 與 2025 A3136 認證內部照片，主晶片標示都可讀為 `ATS2835P`。這是認證樣機證據。晶片廠產品資料列出 C-SKY CK802 CPU 與 CSB 廣播支援，可供日後取得映像時選擇分析工具。[2023 內部照片](https://fccid.io/2AOKB-A3136/Internal-Photos/Internal-Photos-6707410.pdf) · [2025 內部照片](https://fccid.io/2AOKB-A3136/Internal-Photos/Intemal-photos-8361615.pdf) · [Actions ATS2835P](https://www.actionstech.com/index.php?id=3581&siteId=4)

已依使用者授權，僅以主機序號（末四碼 0365）查詢 Anker 官方服務。還原目前 App 的訪客請求簽章及 AES-CFB 封裝流程後，`speaker.eufylife.com/api/v2/speaker/firmware/upgrade_check/batch` 對目前版本 `02.10`、比較版本 `02.09`、`00.00` 均回覆 HTTP 200。加密回覆已成功解開，外層與內層皆為 `res_code=1 / SUCCESS`，每次 `upgrade_list` 只有一筆 `needUpdate=false, lastPackage=null`。舊版 v1 查詢同樣未提供檔案。比較版本只用於雲端查詢參數。

因此本輪實際取得的是官方 App 程式與晶片 SDK 參考碼，A3136 韌體檔仍未取得。官方服務回覆不足以判定其他批次、帳號或版本永遠沒有更新包，也不足以排除喇叭內部的其他私有回報通道。使用者現場提供的手機條件為 iPhone 與官方 Soundcore App。

本輪分析副本位於 `android-analysis/a3136-firmware/`，其 `dex/manifest.json` 記錄 54 個類別、946 個還原方法。查詢結果為 `global-v2-02.10.decoded.json`、`global-v2-02.09.decoded.json`、`global-v2-00.00.decoded.json`。查詢用的原生函式在隔離模擬器中執行，保存的查詢結果以韌體回覆為主。

### 測後狀態與剩餘限制

使用者已確認副機重新加入、兩顆恢復 PartyCast 同步播放。主機仍為 Windows 預設音訊輸出。原控制 BLE 通道取得一筆新查詢的有效 59-byte `01 01` 回覆，PartyCast 旗標為 1；第二次查詢遇到 Windows `ObjectDisposedException / 0x80000013`，保留錯誤記錄，未將舊讀值當成新的回覆。對照檔為 `private-control-postcheck-20261105.jsonl` 與 `link-private-postcheck-20261105.json`。

擷取程序已結束，臨時 Android 訪客查詢 helper 已移除，隔離模擬器程序已結束。49 項既有檢查通過，研究工具亦已編譯／語法檢查。本輪尚未具備將副機即時名單接入桌面程式的資料來源。更深入的 A3136 內部處理流程需要該型號韌體映像、可讀出的實機程式，或兩顆喇叭之間的空中封包。

## 副機 USB-C 拔插對照（10:11–10:17）

兩顆 Glow Mini 以 PartyCast 同步播放。使用者只拔掉副機的 USB-C，之後插回同一條有電的線，確認充電指示燈改變且兩顆仍同步播放。以電腦發出操作提示與收到使用者確認的時間界定動作區間。拔線確認到提示插回之間相隔 57 秒，插回確認後持續記錄主機回覆 114 秒。

| 電腦端觀測 | 結果 |
| --- | --- |
| 主機的 Soundcore RFCOMM 控制連線 | 195 筆校驗正確的 `01 01` 狀態回覆，每筆 59 bytes。所有狀態的 49-byte 內容完全相同，序號屬於主機，主機充電與 PartyCast 旗標均為 1。擷取的 11,505 bytes 全部可由這些封包解釋。 |
| Windows 藍牙低功耗（BLE）廣播掃描 | 9,826 筆廣播觀測中，351 筆能辨識為主機。主機兩種廣播內容在拔線前、拔線後與插回後都有出現。掃描結果中，帶有副機已知位址、內嵌 MAC 或序號的廣播為 0 筆。 |
| Windows 音訊與藍牙狀態 | 涵蓋兩次操作的 190 筆觀測中，主機始終是預設輸出，副機均未直接連上電腦。這輪開始與結束時，Windows 已將副機標為未記住、未驗證。這次只讀取狀態，沒有執行配對、解除配對或變更音訊路由。 |

目前可觀測的兩條路徑都未顯示副機的充電變化。使用者確認現場只有電腦目前的 USB 藍牙棒，這輪沒有取得兩顆喇叭彼此傳送的經典藍牙（BR/EDR）空中封包。對韌體是否有其他私有回報通道，這次實驗無法判定。[本次統計與時間標記](result-peer-usb-20261105-101120.json) · [Microsoft 的 Windows 藍牙 HCI 擷取範圍](https://learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/testing-btp-tools-btvs)

## 保留配對後的直接連線測試（09:43–09:44）

已依使用者要求完成「保留副機配對 → 中斷直接音訊 → 加入 PartyCast」對照。使用者確認兩顆同步播放，副機仍 Remembered/Authenticated=true，直接音訊 State=8；BLE 新建連線回傳 Unreachable，RFCOMM 連線 10060 逾時。87 秒音訊監看沒有觀察到副機重連；同時對主機使用相同 BLE 工具成功取得兩個新的有效狀態回覆。

本次排除「保留配對即可讀取」假設，仍不能宣稱硬體絕對不支援其他通道或隱藏指令。測後依要求保留副機配對，原主機為系統輸出，桌面 v1.4 已重新開啟。副機的最後有效裝置讀取紀錄更新為 09:37:32（來自測試中直接音訊已自動重連的階段，**不是** PartyCast 副機模式的新回覆）。完整時間線、控制條件及檔案見 [保留配對驗證](RETAINED_PAIR_TEST.md)。

## 喇叭之間的 PartyCast 廣播能讀到什麼

Soundcore 說明 PartyCast 2.0 以 CSB 廣播音樂和燈效。藍牙核心規格規定這條廣播只從發送端送往接收端，沒有逐台確認收件的機制。主機可以在零台或多台副機收聽時送出相同內容，因此從廣播本身無法推算收聽台數。若 A3136 能提供副機即時狀態，還需要確認副機透過哪一條路徑回傳資料。[Soundcore PartyCast](https://www.soundcore.com/partycast) · [Bluetooth Core，Vol 2 Part B §4.8](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/Core-62/out/en/br-edr-controller/baseband-specification.html)

重新串接並校驗主機在副機退出、重新加入兩段觀察期間傳給電腦的原始資料。前段的 `partycast-members-join-leave.jsonl` 有 95 筆 `01 01`，後段的 `partycast-members-secondary-rejoin.jsonl` 有 44 筆 `01 01`、2 筆 `01 03`、3 筆 `01 09`。139 筆狀態回覆全為 59 bytes，序號全是主機，PartyCast 旗標全為 1。獨立的雙機播放與副機關機快照中，49-byte 狀態內容只有主機音量不同。官方 A3136 狀態解析器也將同一個電池值填入左右兩欄，這兩欄只能算一顆裝置。

這些擷取涵蓋電腦的控制連線，沒有喇叭對喇叭的空中封包。本機 Windows 回報 USB 藍牙介面的 HCI 與 LMP 版本均為 `0x09`（藍牙 5.0）。CSB 接收能力另列在 LMP feature page 2，本次未取得該頁。要檢驗額外的回報路徑，可分析 A3136 喇叭韌體，或在單獨改變副機的入群狀態與充電狀態時比對 BR/EDR 和 BLE 空中流量。[Bluetooth SIG 版本對照](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/Assigned_Numbers/out/en/index-en.html) · [Bluetooth Core，Vol 2 Part C §3.3 的功能位元表](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/Core-62/out/en/br-edr-controller/link-manager-protocol-specification.html)

## 最新結果與已發佈功能（v1.4）

- 已以兩輪副機退出／加入廣播對照、Classic 名稱及直接讀取的序號，辨識本次副機。主機 MAC `F4:9D:8A:0A:91:10`、序號 `（已遮蔽）`；副機 MAC `F4:9D:8A:0E:D2:BB`、BLE 位址 `CB9D8A0ED2BB`、序號 `（已遮蔽）`。兩顆韌體皆為 `02.10`。
- 使用者同意暫時配對測試後，工具執行前已發現副機完成配對、驗證與音訊連線；保護機制拒絕覆寫既有配對。使用者隨後明確確認是手動建立的本次測試配對、可於測後移除。已直接讀取：RFCOMM `01 01` 完整 59-byte 回覆與 `01 04 = 01`；BLE WriteWithoutResponse `01 01` 亦收到兩次校驗正確回覆。副機資訊讀取成功，並非只有通道寫入成功。
- 副機最後有效裝置回覆 `2026-11-05T01:08:13.2002239Z`（台灣 09:08:13）顯示正在充電；Windows 電量 90% 的實際來源時間為 `01:06:51.1790601Z`（09:06:51）。它們是上次讀取／回報，不應當作現在的電量、充電或充飽狀態。檔案 `probe-secondary-already-paired-state.json`、`probe-secondary-already-paired-charging.json`、`ble-secondary-already-paired-state.jsonl`、`paired-glow-identity-batteries.json`。
- 已依使用者確認移除本次副機配對，原生 API 返回 0；主機保留配對與連線，系統預設輸出恢復原主機。清理後副機 Remembered/Authenticated/Connected 均 false。檔案 `secondary-pair-cleanup.jsonl`、`audio-after-secondary-pair-cleanup.json`。
- 移除配對後，副機 BLE 服務仍可連，讀取特徵曾返回先前那份 59-byte 值，但新查詢沒有通知回覆；**該初始特徵值不可視為新狀態**。RFCOMM 連線則逾時。當時僅支持「副機直接配對連線時成功取得狀態」；後續保留配對的對照已完成，結果見最上節。
- 使用者確認兩顆已恢復 PartyCast 同步播放。清理舊程式通道後，正式 v1.4 的 `--query-partycast` 唯讀核對成功，選取地址是原主機、PartyCast 旗標 true、預設輸出是原主機，沒有錯誤。檔案 `partycast-final-v14.json`。
- **v1.4 已建置、49 項檢查通過並更新啟動 `dist/SoundcoreDesk.exe`**：主機顯示序號／韌體；新增「已確認的 PartyCast 副喇叭」及左側直達按鈕，顯示已確認關聯、實體 MAC、序號、韌體、最後充電與 Windows 電量／來源時間。此名單是現場驗證後保存的紀錄，不是從主機自動列舉。所有列都明示 PartyCast 群組連線「未取得」，不能用電腦藍牙連線取代群組狀態。
- 已將本次副機紀錄存於 `%LOCALAPPDATA%\SoundcoreDesk\partycast-speakers.json`；同名以 MAC 分開，僅在副機實際直接連電腦時每 30 秒讀取更新。離線或逾期保留資料但標示「上次」，不以 UI 重新整理時間冒充來源回報時間。程式不自動配對或自動變更 PartyCast。
- 離線測試涵蓋實際封包（序號匿名化）、錯誤校驗和、截斷與未識別序號、歷史資料時效、藍牙連線與群組連線的區分、舊回報不能覆蓋新回報、紀錄的持久化與無效關聯排除。亦離屏渲染並目視檢查新增 UI。下列 v1.3／等待回覆等段落保留為研究歷程，以本節為準。

## 現場恢復驗證（2026-11-05）

使用者已到現場，確認兩顆開機並透過 PartyCast 同步播放，授權繼續驗證。分析模擬器仍關閉；下列早期紀錄中的「尚未解密／尚未安裝」是歷史狀態，以本節為準。

- `probe-members-onsite-resume.json`：已驗證 `01 01` 的回覆仍是 59 bytes，校驗正確，PartyCast 旗標為 1；不能把這個旗標解讀為副機數量。
- `ble-onsite-two-speakers.jsonl`：兩顆一起播放的 30 秒廣播基線，只辨識主機 BLE 位址 `CB9D8A0A9110`。AD type 5 的 `DAF57101` 對應 service UUID `0171f5da-0000-1000-8000-00805f9b34fb`；AD type 255 前六 bytes 是主機 Classic MAC `F49D8A0A9110`，後八 bytes 均為零。不能把 MAC 前兩 bytes 誤讀為 Bluetooth Company ID。
- 只列舉上述型號服務，成功讀到 `8888` 特徵（Read/Notify/ExtendedProperties）與 `7777` 特徵（Write/WriteWithoutResponse）。初次主機服務查詢曾回傳 Unreachable，該次流程在訂閱／傳送前結束；後續已取得主機的有效 BLE 回覆，見下。
- Windows 快取名稱為 `soundcore Glow Mini BLE`，2026-11-05 00:42 UTC 檢查顯示 BLE Disconnected、未配對。這只是本機 BLE 連線資料，不能代表 Classic 音訊或 PartyCast 的連線狀態。
- 已以 `ble-onsite-transition.jsonl` 持續擷取，另以 `ble-onsite-extended.jsonl` 對照 extended advertisements。早期兩顆一起播放時尚未辨識第二個 Glow Mini 身分；退出對照後已出現第二顆，見下。主機停止出現在廣播中也不能直接解讀為關機。
- 使用者已確認副機退出 PartyCast、仍開機。持續擷取中第二個 Glow 身分首次出現於 `2026-11-05T00:44:30.3096658Z`：BLE `CB9D8A0ED2BB`、廣播內 Classic MAC `F49D8A0ED2BB`、相同型號 service `0171f5da`；另成功讀到名稱 `soundcore Glow Mini BLE` 及 `7777/8888` 特徵。原主機仍是 `F49D8A0A9110`。此身分與退出動作的時間對照吻合，仍需重新加入與後續物理對照確認角色關聯。
- `BleInfoProbe.cs` 先以 WriteWithResponse 傳送已驗證的 `01 01`，GATT 寫入成功但沒有協定回覆。已從解密程式額外還原 `BaseBleDeviceLink` 70 個方法及其 `$4` GATT callback 8 個方法，還原總數現為 **1,279**；來源明確選擇 Android `WRITE_TYPE_NO_RESPONSE (1)`，沒有額外的封包加密步驟。改成 Windows WriteWithoutResponse 後，主機立即回覆校驗正確的 59-byte `01 01`，證明 BLE 讀取通道可用。紀錄 `ble-main-state-no-response.jsonl`。
- 同樣查詢第二顆時，GATT 服務、訂閱、寫入均成功，目前仍未收到有效狀態回覆；電量、充電、韌體及序號因此不能填入。先前另一個觀察程序尚保留訂閱，後續應在其結束後單獨重測，不能直接判定副機不支援讀取。
- 使用者已確認副機重新加入、兩顆同步播放。副機最後一次可見廣播為 `2026-11-05T00:53:33.6028607Z`，確認加入後單獨連線於 `00:54:24Z` 回傳 Unreachable (`ble-secondary-confirmed-rejoined-state.jsonl`)；與群組模式下停止 BLE 服務的可能性吻合，但不能把所有 Unreachable 都當作已加入群組。
- 使用者再確認副機退出、保持開機後，同一 BLE 身分恢復可連。單一程序、WriteWithoutResponse、訂閱後延遲 350ms、已知查詢重試一次：`01 01`、`01 03`、`01 04` 均沒有收到通知，並非先前雙程序訂閱就能完全解釋。標準 Battery Service `180F`、Device Information `180A` 查詢均 Success 但零服務。紀錄：`ble-secondary-exited-solo.jsonl`、`ble-secondary-exited-battery.jsonl`、`ble-secondary-exited-charging.jsonl`。
- 副機已知 RFCOMM service 可連，但 `01 01` 同樣無回覆 (`probe-secondary-exited-rfcomm.json`)。`inspect_classic_glow.py` 額外確認兩顆 Classic 名稱皆為 `soundcore Glow Mini`，主機 Connected/Remembered/Authenticated 均 true，副機均 false；這是 Windows 來源連線快取，仍不是主機回報的群組名單。
- 暫時 BLE-only 配對測試回傳 `Failed / None`，未留下新配對，已再讀取 IsPaired=false 確認。此失敗不足以判定是韌體或 Windows UI 環境原因。紀錄 `ble-secondary-temporary-pair.jsonl`，工具 `Test-SecondaryBlePair.ps1`。
- 額外還原 BaseDeviceManager 38、其 `$2` callback 3、BaseDeviceManagerWithDispatch 13、Cmm2BtDeviceManager 436 個方法，總數 **1,769**。可讀方法顯示一般初始化仍是 getDeviceInfo → `01 01` 空 body，A3136 未覆寫；未找到 A3136 的額外解鎖步驟。JADX 部分方法有型別推論錯誤，不能將未成功反編譯部分稱為已排除所有可能。
- `decode_glow_state.py` 依官方 A3136 欄位解析已驗證的通知；主機韌體 `02.10`，序號可從封包 offset 21..36 讀取，電池是粗略等級而非百分比。副機仍沒有有效回覆，不能填入其韌體、序號、電量、充電或充飽狀態。
- **目前等待使用者確認**：工具 `test_secondary_classic_pair.py` 已以預設唯讀模式核對地址與未配對狀態；只有明確旗標 `--pair-secondary-temporarily` 才會新增一般配對、讀取並移除此測試新增的配對。尚未執行此旗標。因新增 Classic 配對可能短暫影響正在播放的音效輸出，已詢問使用者是否接受；未獲答覆前不執行。副機目前依最後回覆處於退出 PartyCast、仍開機。

## 已完成的解密研究與限制

- Google 官方 AEHD 2.2 已經使用者授權、通過 UAC 後安裝成功；Google／Microsoft 簽章有效，加速檢查確認可用。紀錄：`android-analysis/hypervisor-install.log`。
- 已成功取得 5.0.32 的 23 個解密 DEX，並用獨立 Android 套件執行環境加上 `NterpGetCodeItem` 讀取，還原 **1,201 個方法 CodeItem**。也以 3.9.1 交叉對照。重要工具：`ResearchMain.java`、`sc_method_dump.c`、`restore_method_code.py`；native ART RVA 僅適用此分析映像，`libart.so` SHA-256 為 `cd37d2fb969ffd8b53db0be10eb995591e0fd629e255a7ad11bb05edd129d5a8`。
- 本機結果：`android-analysis/methods-5032/`、`restored-5032/manifest.json`、`decompiled-5032/`。原始 APK 沒有改寫；分析 VM 內曾調整此套件 ABI 與執行環境，實體喇叭沒有連到模擬器。
- 已讀到最新 `A3136CmdService`、`A3136AnalysisService`、`CmmBtCmdService`、`Cmm2BtAnalysisRDService` 的實際方法內容。PartyCast setter 為 `01 A4`（最新常數 `Cmm2CmdData.l0`），通知 `01 24` 只解析開關；A3136 完整狀態 packet offset 41／body offset 32 同樣是開關。
- 這份 A3136／共用協定目前沒有找到可驗證的 PartyCast 副機列舉實作。`DeviceCnnPhoneHistory`／`ConnectionHistoryInfo` 是手機或音源紀錄，`ConnectedDeviceCount` 屬於雙音源解析，不能當作副機名單或數量。不能由此宣稱韌體絕對不存在隱藏指令。
- 已修正先前未知欄位的意義：body offset **30 是燈光開關、33 是亮度原始值、48 是燈效索引**；舊值 `00/23/04` → `01/13/00` 是燈光設定差異，不應稱為 PartyCast 版本／角色改變。本次沒有擅自覆寫目前燈光設定。
- 實體主機僅再接受已驗證的 `01 01` 讀取，59-byte 回覆校驗正確、PartyCast 仍開啟。紀錄：`probe-members-after-sdk-recovery.json`。
- Windows BLE 掃描已辨識主機：廣播 service `0171f5da`，manufacturer data 前六 bytes 對應 Classic MAC，另有 Fast Pair；GATT 曾讀到名稱 `soundcore Glow Mini BLE`。未對未知 GATT 特徵寫入，後續探索有 Unreachable。目前未辨識第二顆，不能據此說副機已離線。
- 已在現場恢復 `BleAdvertisementScan.cs` 驗證；即使看到第二顆，也必須另驗證 PartyCast 關聯，不能直接把附近喇叭當成已加入成員。

桌面程式維持 v1.3；副喇叭名單與各機資訊仍未完成，沒有以不完整或假造資料發佈。

使用者確認目前是 1 顆藍牙主喇叭，加上 1 顆透過 PartyCast 播放的 Glow Mini 副喇叭。本次目標是從主喇叭取得實際成員清單與各顆識別資訊，不能把附近藍牙裝置或使用者口述的數量當成主喇叭回報。

## 已取得的資料

- 兩顆一起播放時，`01 01` 仍回傳 49-byte 狀態 body，沒有成員 MAC、名稱或序號清單。
- 對已觀測的 `0E 04`、`0E 09` 查詢，分別得到單 byte `04`、`00`。副喇叭關機後仍是相同值，因此不能把這兩個值解讀為成員數量。
- 副喇叭關機前後，完整狀態只有音量欄位不同，沒有能核對副喇叭身分的變化。
- 使用者重新開機並確認兩顆一起播放時，持續擷取的通知只有電量、音量與播放狀態；沒有成員加入通知或副喇叭識別碼。

保留的原始資料：`partycast-members-two-speakers.json`、`partycast-members-secondary-off.json`、`partycast-members-secondary-rejoin.jsonl`。這些檔案包含裝置識別，僅保留於本機，由 gitignore 排除。

## 未公開命令探測的錯誤與限制

曾以相鄰低位元 opcode 作為查詢候選。這個假設在 `0E` 命令族不成立：額外探測後，完整狀態的 offset 30、33、48 從 `00/23/04` 變成 `01/13/00`。這些不是單純查詢，不能再稱為唯讀探測。

已停止此類探測，並從探測工具白名單移除未確認的命令；正式桌面程式從未加入這些命令。以已驗證的 `01 A4` 關閉再開啟 PartyCast 能恢復開啟旗標，但未恢復上述全部欄位，已請使用者以正常關機／開機和實體按鈕重建原有連線，需再確認還原結果。

不應把 `0E 06` 的 `13` 或其他未知數值解讀成成員數量、協定版本或角色，尚無足夠證據。

### 主喇叭正常重開後的核對

使用者已回覆「已重開主喇叭，兩顆已重新一起播放」。只送出已驗證的 `01 01` 唯讀查詢，取得校驗和正確的狀態。PartyCast 開啟旗標為 `01`，但 offset 30、33、48 仍為 `01/13/00`，與探測前 `00/23/04` 不同，故不能宣稱模式已完全還原。已改以官方使用指南的實體指示燈辨識 TWS／PartyCast，不再送未知 opcode。

核對檔：`probe-members-after-user-restart.json`。

使用者再確認主喇叭的藍牙指示燈為白色恆亮或閃爍。結合兩顆可同步播放、主喇叭回報 PartyCast 旗標開啟，目前可確認 PartyCast 播放功能可用；但不能宣稱所有未解析模式欄位已回到原始值。官方使用指南將白色指示燈列為 PartyCast 配對／已連線狀態，TWS 主喇叭的已連線指示燈則為藍色。參考：[官方 Glow Mini 使用指南](https://service.soundcore.com/article-description/soundcore-Glow-Mini-User-Guide--A3136--soundcore-Glow-User-Guide--A3166)。

已停止未公開命令探測，桌面程式維持 v1.3。成員列表仍未完成，沒有發佈能列舉副喇叭的版本。

## App 靜態檢查

只讀取公開分發的 APK ZIP 目錄及部分程式／語系資源，沒有安裝或執行 Android App。原生 Java 程式受 Ijiami 保護，沒有取得完整協定實作；可讀取的路由與語系有 PartyCast 使用指引，但未找到能證明有成員列表查詢的資料。這不能證明不存在該功能。

官方 [PartyCast 技術介紹](https://www.soundcore.com/partycast) 提到 DSP 與 CSB 廣播。廣播架構可能使主喇叭不持有完整接收端名單，但這只是本次研究的假設，不能據此宣稱硬體絕對無法提供成員資訊。

## 目前結論

尚未找到可驗證的 PartyCast 成員清單命令，不能實作真實的自動成員清單，也不能假造副喇叭位址、電量、充電狀態或連線數。桌面程式仍維持已驗證的 v1.3 功能；沒有把此研究當作完成的成員列舉功能發佈。

## 追查加密封裝的進度

- 已完整取得 Android App 5.0.32（versionCode 431），套件 `com.oceanwing.soundcore`。APK SHA-256：`a07ab8ca1d61e8ff31f8dd814758e1d8ce6bd057547a587ac9c132ea833954a4`；v2 簽章憑證 SHA-256：`eab2b0ae8a490a58a5ed1fcae81f503df001892f0b2655ffc031511af5a3867f`。
- `assets/ijiami.dat` 為版本欄位 3 的加密容器，30,871,104 bytes。另有 `ijiami.ajm` 與 `IJMDal.Data`。它們沒有直接可讀的 DEX magic 或 PartyCast/A3136 字串，不能把解壓 ZIP 當成解密完成。
- 已從殼 DEX 定位 `s.h.e.l.l.S.attachBaseContext()`：先準備並載入 `libexec.so`／`libexecmain.so`，再呼叫 native `s.h.e.l.l.N.l(Application, "com.oceanwing.soundcore")` 與 `N.r(...)`，交接到 `com.oceanwing.soundcore.application.SoundCoreApplication`。
- 可行的下一步是執行階段擷取：在 `libart.so`／`libdexfile.so` 的 DEX 載入入口攔截明文，另掃描記憶體中的 DEX 結構，即使 magic 被移除也驗證 header、endian、size 與索引範圍。工具為 `dump_soundcore_runtime.py`，限定本機隔離研究模擬器，沒有 Bluetooth 指令。
- 已下載並驗證 Google Android 系統映像與模擬器，建立獨立 AVD；Android 11 映像確認支援 `x86_64,x86,arm64-v8a,armeabi-v7a,armeabi`，native bridge 為 `libndk_translation.so`。沒有登入帳號，也未將喇叭連到模擬器。
- 純軟體 TCG 模擬完成首次開機，但多次 Android 指令逾時，之後套件服務消失；尚未成功啟動並擷取官方 App。不能宣稱已取得解密後的通訊實作。
- 已備妥 Google 官方 AEHD 2.2 驅動及安裝腳本；`aehd.Sys` 的 Google LLC 與 `aehd.cat` 的 Microsoft 簽章皆有效。目前未安裝，已詢問使用者是否授權此 Windows 核心驅動變更；未取得回覆前不執行。
- 後續已獨立成功拆除兩個 x86_64 原生載入器的第一層：以 `unpack_native_loader.py` 在 Unicorn 中只執行外層解壓 stub，僅模擬有界記憶體映射，拒絕其他系統呼叫，於最後的 `mprotect` 停止，未執行 App 初始化。已還原 `libexec` 與 `libexecmain` 的 native 程式碼；修正分析用 ELF 的程式段位置後可由 ELF parser 正確解析，分別取得 532 與 11 個動態符號，並確認 `JNI_OnLoad`、`core`、`getOpCode`。原始 APK 沒有變更。
- 第一層還原不等於業務 DEX 已解密：`JNI_OnLoad` 仍透過自訂 VM 執行，尚未取得 A3136／PartyCast 通訊方法。完整 Android 動態擷取路線仍待硬體加速環境，純軟體模擬器已停止以免持續占用 CPU。

參考原始工具說明：[BlackDex](https://github.com/CodingGay/BlackDex/blob/main/README_EN.md)、[Frida DEXDump](https://github.com/hluwa/frida-dexdump)、[Google ARM 模擬支援](https://android-developers.googleblog.com/2020/03/run-arm-apps-on-android-emulator.html)。這些說明提供擷取方法，尚不能證明這個 App 的所有受保護方法都能完整還原。

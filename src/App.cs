using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SoundcoreDesk
{
    public sealed class Snapshot
    {
        public List<AudioDevice> Audio { get; set; }
        public List<BluetoothDevice> Bluetooth { get; set; }
        public string BluetoothError { get; set; }
        public static Snapshot Read()
        {
            var result = new Snapshot { Audio = SoundcoreDesk.Audio.Devices(), Bluetooth = new List<BluetoothDevice>() };
            try { result.Bluetooth = SoundcoreDesk.Bluetooth.Devices(); }
            catch (Exception ex) { result.BluetoothError = ex.Message; }
            result.AssociateContainers();
            return result;
        }
        public void AssociateContainers()
        {
            foreach (var endpoint in Audio)
            {
                var physical = Bluetooth.FirstOrDefault(b => b.ContainerId != Guid.Empty && b.ContainerId == endpoint.ContainerId);
                endpoint.BluetoothAddress = physical == null ? null : physical.Address;
            }
        }
        public List<AudioDevice> GlowEndpoints()
        {
            return Audio.Where(d => d.IsGlow || Bluetooth.Any(b => IsGlow(b.Name) && b.ContainerId != Guid.Empty && b.ContainerId == d.ContainerId))
                .OrderByDescending(d => d.Connected).ThenBy(d => d.HandsFree).ThenByDescending(d => d.IsDefault).ThenBy(d => d.Id).ToList();
        }
        public static bool IsGlow(string name) { return name != null && name.IndexOf("glow mini", StringComparison.OrdinalIgnoreCase) >= 0; }
        /// <summary>
        /// The speakers shown as home-page cards: one connected stereo endpoint per physical speaker.
        /// Hands-Free endpoints are mono call audio and are left out; with TWS only each pair's main speaker
        /// is connected to the PC, so each pair appears once.
        /// </summary>
        public List<AudioDevice> ConnectedSpeakers()
        {
            return GlowEndpoints().Where(d => d.Connected && !d.HandsFree)
                .GroupBy(d => d.ContainerId == Guid.Empty ? d.Id : d.ContainerId.ToString())
                .Select(g => g.First()).ToList();
        }
    }

    public sealed class Controller : IDisposable
    {
        readonly Window window;
        readonly DispatcherTimer timer = new DispatcherTimer();
        readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
        readonly EventWaitHandle activationRequest;
        // One card per connected speaker, in display order (surround front, rear, then by address).
        readonly List<SpeakerCard> cards = new List<SpeakerCard>();
        readonly StackPanel speakerList;
        // Confirmed PartyCast partners, shown under each speaker's PartyCast row. A partner's record is
        // refreshed from its own state reads when it is connected, plus Windows battery reports.
        readonly List<PartyCastSpeakerRecord> savedMembers = PartyCastRecords.Load(PartyCastRecords.FilePath);
        // Charging is taken from the same 01 01 state read as PartyCast: one RFCOMM connection per poll.
        readonly Dictionary<string, GlowChargingReport> chargingReports = new Dictionary<string, GlowChargingReport>();
        readonly Dictionary<string, string> chargingErrors = new Dictionary<string, string>();
        readonly Dictionary<string, GlowPartyCastReport> partyCastReports = new Dictionary<string, GlowPartyCastReport>();
        readonly Dictionary<string, DateTime> nextPartyCastRead = new Dictionary<string, DateTime>();
        readonly Dictionary<string, string> partyCastErrors = new Dictionary<string, string>();
        readonly Dictionary<string, int> partyCastGeneration = new Dictionary<string, int>();
        readonly HashSet<string> partyCastInFlight = new HashSet<string>();
        // Address → requested state while a PartyCast change waits for that speaker's confirmation.
        readonly Dictionary<string, bool> partyCastChanging = new Dictionary<string, bool>();
        Snapshot snapshot;
        bool refreshing, disposed, failed;
        int ticks;
        DateTime manualPollUntil;
        /// <summary>
        /// Surround or the sync test is streaming to the speakers. The 15-second status reads open a new
        /// RFCOMM connection each time; on a single dongle already carrying two A2DP streams that extra
        /// traffic can make the audio drop out, so routine reads pause while audio streams.
        /// </summary>
        bool StreamingAudio { get { return (surround != null && surround.Running) || (syncTest != null && syncTest.Running); } }
        T Find<T>(string name) where T : class { return (T)window.FindName(name); }
        void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        void Click(string name, Action action) { Find<Button>(name).Click += delegate { action(); }; }

        public Controller(Window window, EventWaitHandle activationRequest)
        {
            this.window = window;
            this.activationRequest = activationRequest;
            speakerList = Find<StackPanel>("SpeakerList");
            // 「重新整理」 always reads the speakers once, even while audio streams.
            Click("Refresh", () => { nextPartyCastRead.Clear(); manualPollUntil = DateTime.UtcNow.AddSeconds(5); Refresh(); });
            Click("NavHome", () => ShowPage(Page.Home));
            Click("NavSurround", () => ShowPage(Page.Surround));
            Click("NavSync", () => ShowPage(Page.Sync));
            Click("BluetoothSettings", () => OpenSettings("ms-settings:bluetooth"));
            Click("SoundSettings", () => OpenSettings("ms-settings:sound"));
            Click("ToTray", () => { tray.Visible = true; window.Hide(); });
            var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundcoreDesk.ico");
            if (iconStream != null) { using (iconStream) tray.Icon = new System.Drawing.Icon(iconStream); }
            else tray.Icon = System.Drawing.SystemIcons.Application;
            tray.Text = "Soundcore Desk";
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("開啟 Soundcore Desk", null, delegate { Show(); });
            menu.Items.Add("全部喇叭音量 +5%", null, delegate { AdjustAll(5); });
            menu.Items.Add("全部喇叭音量 −5%", null, delegate { AdjustAll(-5); });
            menu.Items.Add("全部靜音／取消靜音", null, delegate { ToggleMuteAll(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("結束程式", null, delegate { window.Close(); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Show(); }; tray.Visible = true;
            window.Closed += delegate { Dispose(); };
            // Shortcuts act on every connected speaker; each card's own buttons act on that speaker only.
            window.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                if (e.Key == Key.Up) { AdjustAll(5); e.Handled = true; }
                if (e.Key == Key.Down) { AdjustAll(-5); e.Handled = true; }
                if (e.Key == Key.M) { ToggleMuteAll(); e.Handled = true; }
            };
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += delegate { if (this.activationRequest.WaitOne(0)) Show(); UpdateNav(); if (++ticks % 5 == 0 || failed) Refresh(); PollVolumes(); };
            window.Loaded += delegate { Refresh(); timer.Start(); };
            SetTrayControls(false); UpdateNav();
        }

        void Show() { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }

        enum Page { Home, Surround, Sync }
        Page page = Page.Home;
        SyncTestController syncTest;
        SurroundController surround;
        /// <summary>
        /// Switches the right-hand side in place. Pages are created on first use and then kept, so a running
        /// surround or sync test continues while another page is shown; the nav marks it 「播放中」.
        /// </summary>
        void ShowPage(Page next)
        {
            try
            {
                if (next == Page.Surround && surround == null) { surround = new SurroundController(); Find<Border>("SurroundPage").Child = surround.View; }
                if (next == Page.Sync && syncTest == null) { syncTest = new SyncTestController(); Find<Border>("SyncPage").Child = syncTest.View; }
            }
            catch (Exception ex) { Error("無法開啟" + (next == Page.Surround ? " 4 聲道環繞" : "環繞同步測試") + "：" + ex.Message); next = Page.Home; }
            page = next;
            Find<UIElement>("HomePage").Visibility = page == Page.Home ? Visibility.Visible : Visibility.Collapsed;
            Find<UIElement>("SurroundPage").Visibility = page == Page.Surround ? Visibility.Visible : Visibility.Collapsed;
            Find<UIElement>("SyncPage").Visibility = page == Page.Sync ? Visibility.Visible : Visibility.Collapsed;
            if (page == Page.Surround) surround.Shown();
            if (page == Page.Sync) syncTest.Shown();
            UpdateNav();
        }
        void UpdateNav()
        {
            Nav("NavHome", page == Page.Home); Nav("NavSurround", page == Page.Surround); Nav("NavSync", page == Page.Sync);
            Find<UIElement>("NavSurroundLive").Visibility = surround != null && surround.Running ? Visibility.Visible : Visibility.Collapsed;
            Find<UIElement>("NavSyncLive").Visibility = syncTest != null && syncTest.Running ? Visibility.Visible : Visibility.Collapsed;
        }
        void Nav(string name, bool current)
        {
            var button = Find<Button>(name);
            button.Background = current ? Brush("#21332F") : Brushes.Transparent;
            button.BorderBrush = current ? Brush("#31554C") : Brushes.Transparent;
            button.Foreground = current ? Brush("#91F1DD") : Brush("#C3CFDB");
            button.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
        }
        static SolidColorBrush Brush(string color) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
        void OpenSettings(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { Error("無法開啟 Windows 設定：" + ex.Message); }
        }
        async void Refresh()
        {
            if (refreshing || disposed) return;
            refreshing = true;
            Find<Button>("Refresh").IsEnabled = false;
            try
            {
                var next = await Task.Run(() => Snapshot.Read());
                if (disposed) return;
                snapshot = next;
                // Windows keeps battery reports for paired speakers; PartyCast partners pick them up here.
                bool batteryChanged = false;
                foreach (var row in savedMembers)
                {
                    var device = snapshot.Bluetooth.FirstOrDefault(b => string.Equals(b.Address, row.Address, StringComparison.OrdinalIgnoreCase));
                    if (device != null) batteryChanged |= row.Apply(null, device.Battery);
                }
                if (batteryChanged) SaveRecords();
                foreach (string address in chargingReports.Keys.ToArray())
                    if (!snapshot.Bluetooth.Any(b => b.Address == address && b.Connected)) chargingReports.Remove(address);
                foreach (string address in partyCastReports.Keys.ToArray())
                    if (!snapshot.Bluetooth.Any(b => b.Address == address && b.Connected))
                    {
                        partyCastReports.Remove(address); nextPartyCastRead.Remove(address);
                        int generation; partyCastGeneration.TryGetValue(address, out generation); partyCastGeneration[address] = generation + 1;
                    }
                SyncCards(snapshot.ConnectedSpeakers());
                failed = false;
                UpdateCards(); PollVolumes();
            }
            catch (Exception ex) { failed = true; Error("讀取裝置失敗，正在重試：" + ex.Message); }
            finally { refreshing = false; if (!disposed) Find<Button>("Refresh").IsEnabled = true; }
        }

        /// <summary>Adds a card for each newly connected speaker, drops cards of disconnected ones, keeps the rest.</summary>
        void SyncCards(List<AudioDevice> speakers)
        {
            var roles = SurroundSettings.Load();
            Func<AudioDevice, int> rank = d => d.Id == roles.FrontId ? 0 : d.Id == roles.RearId ? 1 : 2;
            var ordered = speakers.OrderBy(rank).ThenBy(d => d.BluetoothAddress ?? d.Id, StringComparer.OrdinalIgnoreCase).ToList();
            var next = new List<SpeakerCard>();
            foreach (var endpoint in ordered)
            {
                var card = cards.FirstOrDefault(c => c.Endpoint.Id == endpoint.Id);
                if (card == null)
                {
                    try { card = CreateCard(endpoint); }
                    catch (Exception ex) { Error("無法顯示喇叭：" + ex.Message); continue; }
                }
                card.Endpoint = endpoint;
                string role = endpoint.Id == roles.FrontId ? "環繞前方" : endpoint.Id == roles.RearId ? "環繞後方" : null;
                card.Text("RoleText", role ?? "");
                card.Find<UIElement>("RoleTag").Visibility = role == null ? Visibility.Collapsed : Visibility.Visible;
                next.Add(card);
            }
            // Re-parenting a card would drop a slider drag in progress; wait for the next refresh then.
            bool dragging = cards.Any(c => c.Slider.IsMouseCaptureWithin);
            if (!dragging && !next.Select(c => c.View).SequenceEqual(speakerList.Children.Cast<UIElement>()))
            {
                speakerList.Children.Clear();
                foreach (var card in next) speakerList.Children.Add(card.View);
            }
            if (!dragging) { cards.Clear(); cards.AddRange(next); }
            Find<UIElement>("NoSpeakers").Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Text("HomeSubtitle", cards.Count == 0 ? "目前沒有已連線的 Glow Mini。"
                : cards.Count == 1 ? "已連線 1 顆 Glow Mini。TWS 配對時，每組只顯示連接電腦的主喇叭。"
                : "已連線 " + cards.Count + " 顆 Glow Mini，各自調整音量。TWS 配對時，每組只顯示連接電腦的主喇叭。");
        }
        SpeakerCard CreateCard(AudioDevice endpoint)
        {
            var card = new SpeakerCard(endpoint);
            card.Slider.ValueChanged += delegate { if (!card.Updating) SetVolume(card, card.Slider.Value); };
            card.OnClick("VolumeDown", () => Adjust(card, -5)); card.OnClick("VolumeUp", () => Adjust(card, 5));
            card.OnClick("Preset25", () => SetVolume(card, 25)); card.OnClick("Preset50", () => SetVolume(card, 50)); card.OnClick("Preset75", () => SetVolume(card, 75));
            card.OnClick("Mute", () => ToggleMute(card));
            card.OnClick("PartyCastOn", () => SetPartyCast(card, true)); card.OnClick("PartyCastOff", () => SetPartyCast(card, false));
            card.SetControls(false);
            return card;
        }

        void UpdateCards()
        {
            if (snapshot == null) return;
            foreach (var card in cards) UpdateCard(card);
            Text("Status", snapshot.BluetoothError == null ? "每秒同步音量 · 每 5 秒偵測連線 · Ctrl + ↑ / ↓ 調全部喇叭音量，Ctrl + M 全部靜音" : "音量可獨立控制；藍牙識別暫時無法讀取：" + snapshot.BluetoothError);
        }
        void UpdateCard(SpeakerCard card)
        {
            var endpoint = card.Endpoint;
            var physical = endpoint.ContainerId == Guid.Empty ? null : snapshot.Bluetooth.FirstOrDefault(b => b.ContainerId == endpoint.ContainerId);
            card.Physical = physical;
            card.NameControls();
            RefreshPartyCast(physical);
            card.Text("DeviceTitle", (physical == null ? endpoint.Name : physical.Name) + (physical == null ? "" : " · " + SpeakerCard.Suffix(physical.Address)));
            card.Text("ConnectionText", physical != null && physical.Connected ? "已連線 · 音訊可用" : "音訊可用 · 藍牙身分未取得");
            card.Text("AddressText", "藍牙位址  " + (physical == null ? "無法取得" : physical.Address));
            card.Find<TextBlock>("AddressText").ToolTip = "音訊端點：" + endpoint.Name + "\n" + endpoint.Id + "\n裝置容器：" + endpoint.ContainerId;
            var battery = physical == null ? null : physical.Battery;
            GlowChargingReport vendorCharging = null;
            if (physical != null && physical.Connected) chargingReports.TryGetValue(physical.Address, out vendorCharging);
            // While reads are paused the last result stays, labelled with its read time.
            bool paused = StreamingAudio;
            if (vendorCharging != null && !paused && DateTime.UtcNow - vendorCharging.ObservedAtUtc > TimeSpan.FromSeconds(30)) vendorCharging = null;
            string batteryLevel = battery == null ? "電量：未取得" : "電量 " + battery.Percentage + "%";
            string charging = vendorCharging != null ? (vendorCharging.IsCharging ? "正在充電" : "未在充電") : battery == null || !battery.Charging.HasValue ? "充電狀態未取得" : battery.Charging.Value ? "正在充電" : "未在充電";
            card.Text("BatteryText", batteryLevel + " · " + charging);
            card.Find<TextBlock>("BatteryText").Foreground = Brush(battery != null && battery.Percentage <= 20 ? "#E6C890" : "#A0EEDC");
            card.Text("BatteryUpdated", (battery == null ? "等待 Windows 回報電量" : "電量回報：" + (battery.ReportedAtUtc.HasValue ? battery.ReportedAtUtc.Value.ToLocalTime().ToString("MM/dd HH:mm:ss") : "時間未提供")) + (vendorCharging == null ? "" : " · 充電查詢：" + vendorCharging.ObservedAtUtc.ToLocalTime().ToString("HH:mm:ss") + (paused ? "（播放中暫停更新）" : "")));
            string protocolError = null;
            if (physical != null) chargingErrors.TryGetValue(physical.Address, out protocolError);
            card.Find<TextBlock>("BatteryText").ToolTip = "電量來自 Windows 藍牙回報，可能以階梯值更新。\n充電狀態直接向 Glow Mini 專用通道查詢（每 15 秒），01 = 正在充電，00 = 未在充電。\n未在充電不代表已充飽；不以 100% 電量推論充飽。" + (protocolError != null ? "\n最近查詢失敗：" + protocolError : "");
            card.Find<TextBlock>("BatteryUpdated").ToolTip = battery == null ? "尚無電量資料" : "來源端點：" + battery.SourceDeviceId + "\n這是 Windows 保存的裝置回報時間，不是畫面重新整理時間。";
            GlowPartyCastReport partyCast = null;
            if (physical != null && physical.Connected) partyCastReports.TryGetValue(physical.Address, out partyCast);
            if (partyCast != null && !paused && DateTime.UtcNow - partyCast.ObservedAtUtc > TimeSpan.FromSeconds(30)) partyCast = null;
            var identity = Identity(partyCast);
            card.Text("DeviceIdentity", identity == null ? "韌體與序號：等待裝置回覆" : "韌體 " + identity.Firmware + " · 序號 " + identity.SerialNumber);
            // Speakers confirmed on site as this one's PartyCast partner, with each value's own source time.
            var peers = physical == null ? new List<PartyCastSpeakerRecord>() : savedMembers.Where(r => string.Equals(r.PrimaryAddress, physical.Address, StringComparison.OrdinalIgnoreCase)).ToList();
            card.Text("PartyCastPeers", peers.Count == 0 ? "" : "曾一起 PartyCast：" + string.Join("；", peers.Select(r => PartyCastRecords.PeerSummary(r, DateTime.UtcNow))));
            card.Find<UIElement>("PartyCastPeers").Visibility = peers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            bool target = false;
            bool changing = physical != null && partyCastChanging.TryGetValue(physical.Address, out target);
            bool canControlParty = physical != null && physical.Connected && !changing;
            card.Text("PartyCastState", changing ? (target ? "正在開啟…" : "正在關閉…") : partyCast == null ? "狀態未取得" : partyCast.Enabled ? "已開啟" : "已關閉");
            card.Find<Button>("PartyCastOn").IsEnabled = canControlParty && (partyCast == null || !partyCast.Enabled);
            card.Find<Button>("PartyCastOff").IsEnabled = canControlParty && (partyCast == null || partyCast.Enabled);
            string partyError = null;
            if (physical != null) partyCastErrors.TryGetValue(physical.Address, out partyError);
            card.Text("PartyCastHint", changing ? "正在等待喇叭確認，請稍候。"
                : paused ? "環繞／同步測試播放中，暫停自動查詢以免干擾藍牙音訊（按「重新整理」可讀一次）" + (partyCast == null ? "" : " · 上次確認 " + partyCast.ObservedAtUtc.ToLocalTime().ToString("HH:mm:ss"))
                : partyError != null ? "最近操作：" + partyError : partyCast == null ? "設定後會向這顆喇叭確認結果。" : "喇叭確認：" + partyCast.ObservedAtUtc.ToLocalTime().ToString("HH:mm:ss") + " · 每 15 秒同步");
        }
        void SaveRecords()
        {
            try { PartyCastRecords.Save(PartyCastRecords.FilePath, savedMembers); }
            catch (IOException ex) { Error("無法儲存 PartyCast 紀錄：" + ex.Message); }
            catch (UnauthorizedAccessException ex) { Error("無法儲存 PartyCast 紀錄：" + ex.Message); }
        }
        /// <summary>Firmware, serial and charging carried by the same 01 01 frame, stamped with its read time.</summary>
        static GlowDeviceReport Identity(GlowPartyCastReport report)
        {
            if (report == null || string.IsNullOrEmpty(report.StateFrameHex)) return null;
            byte[] frame = new byte[report.StateFrameHex.Length / 2];
            for (int i = 0; i < frame.Length; i++) frame[i] = Convert.ToByte(report.StateFrameHex.Substring(i * 2, 2), 16);
            GlowDeviceReport identity;
            if (!SoundcoreProtocol.TryReadDeviceState(frame, out identity)) return null;
            identity.ObservedAtUtc = report.ObservedAtUtc;
            return identity;
        }
        async void RefreshPartyCast(BluetoothDevice device)
        {
            if (device == null || !device.Connected || disposed || partyCastChanging.ContainsKey(device.Address) || partyCastInFlight.Contains(device.Address)) return;
            if (StreamingAudio && DateTime.UtcNow > manualPollUntil) return;
            DateTime next;
            if (nextPartyCastRead.TryGetValue(device.Address, out next) && next > DateTime.UtcNow) return;
            string address = device.Address;
            nextPartyCastRead[address] = DateTime.UtcNow.AddSeconds(15);
            int generation; partyCastGeneration.TryGetValue(address, out generation);
            partyCastInFlight.Add(address);
            try
            {
                var report = await Task.Run(() => SoundcoreProtocol.ReadPartyCast(address));
                int current; partyCastGeneration.TryGetValue(address, out current);
                if (!disposed && generation == current && snapshot.Bluetooth.Any(b => b.Address == address && b.Connected))
                { partyCastReports[address] = report; partyCastErrors.Remove(address); StoreReport(address, report); }
            }
            catch (Exception ex)
            {
                int current; partyCastGeneration.TryGetValue(address, out current);
                if (generation == current) { partyCastReports.Remove(address); partyCastErrors[address] = ex.Message; }
                // Never keep an old positive charging result after a failed read.
                chargingReports.Remove(address); chargingErrors[address] = ex.Message;
            }
            finally { partyCastInFlight.Remove(address); if (!disposed) UpdateCards(); }
        }
        async void SetPartyCast(SpeakerCard card, bool enabled)
        {
            var physical = card.Physical;
            if (snapshot == null || physical == null || !physical.Connected || partyCastChanging.ContainsKey(physical.Address)) return;
            string address = physical.Address;
            int generation; partyCastGeneration.TryGetValue(address, out generation); partyCastGeneration[address] = generation + 1;
            partyCastChanging[address] = enabled; partyCastErrors.Remove(address); UpdateCards();
            try
            {
                var report = await Task.Run(() => SoundcoreProtocol.SetPartyCast(address, enabled));
                if (!disposed && snapshot.Bluetooth.Any(b => b.Address == address && b.Connected))
                { partyCastReports[address] = report; nextPartyCastRead[address] = DateTime.UtcNow.AddSeconds(15); StoreReport(address, report); }
            }
            catch (Exception ex)
            {
                partyCastReports.Remove(address); partyCastErrors[address] = ex.Message;
                nextPartyCastRead[address] = DateTime.UtcNow.AddSeconds(5);
            }
            finally { partyCastChanging.Remove(address); if (!disposed) UpdateCards(); }
        }
        void StoreReport(string address, GlowPartyCastReport report)
        {
            if (report.Charging.HasValue)
            {
                chargingReports[address] = new GlowChargingReport { IsCharging = report.Charging.Value, ObservedAtUtc = report.ObservedAtUtc, FrameHex = report.StateFrameHex };
                chargingErrors.Remove(address);
            }
            else { chargingReports.Remove(address); chargingErrors[address] = "狀態封包的充電旗標不是 0/1，未解讀。"; }
            // When this speaker is someone's confirmed PartyCast partner, keep that saved line current.
            var identity = Identity(report);
            var device = snapshot == null ? null : snapshot.Bluetooth.FirstOrDefault(b => string.Equals(b.Address, address, StringComparison.OrdinalIgnoreCase));
            bool changed = false;
            foreach (var row in savedMembers.Where(r => string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase)))
                changed |= row.Apply(identity, device == null ? null : device.Battery);
            if (changed) SaveRecords();
        }
        void SetTrayControls(bool enabled)
        {
            if (tray.ContextMenuStrip != null) for (int i = 1; i <= 3; i++) tray.ContextMenuStrip.Items[i].Enabled = enabled;
        }

        void PollVolumes()
        {
            if (disposed) return;
            var summary = new List<string>();
            foreach (var card in cards.ToList())
            {
                try
                {
                    var state = Audio.Read(card.Endpoint.Id);
                    if (!card.Slider.IsMouseCaptureWithin) ShowVolume(card, state);
                    var battery = card.Physical == null ? null : card.Physical.Battery;
                    summary.Add(card.Label + " " + Math.Round(state.Volume * 100) + "%" + (state.Muted ? "靜音" : "") + (battery != null ? " 電" + battery.Percentage + "%" : ""));
                }
                catch (Exception ex)
                {
                    failed = true; card.SetControls(false); card.Text("VolumeNumber", "—");
                    Error("「" + card.Label + "」暫時無法控制，正在重新偵測：" + ex.Message);
                }
            }
            // NotifyIcon.Text is limited to 63 characters.
            string tip = summary.Count == 0 ? "Soundcore Desk · 沒有已連線的喇叭" : "Glow Mini · " + string.Join(" · ", summary);
            tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
            SetTrayControls(cards.Count > 0);
        }
        void ShowVolume(SpeakerCard card, VolumeState state)
        {
            card.Updating = true;
            try { card.Slider.Value = state.Volume * 100; }
            finally { card.Updating = false; }
            card.Text("VolumeNumber", Math.Round(state.Volume * 100).ToString());
            card.Text("VolumeCaption", state.Muted ? "已靜音" : "喇叭音量");
            card.Find<Button>("Mute").Content = state.Muted ? "取消靜音" : "靜音";
            card.SetControls(true);
        }
        void SetVolume(SpeakerCard card, double value)
        {
            try { Audio.SetVolume(card.Endpoint.Id, (float)(Math.Max(0, Math.Min(100, value)) / 100)); ShowVolume(card, Audio.Read(card.Endpoint.Id)); }
            catch (Exception ex) { failed = true; card.SetControls(false); Error("「" + card.Label + "」音量設定失敗：" + ex.Message); }
        }
        void Adjust(SpeakerCard card, int delta)
        {
            try { SetVolume(card, Audio.Read(card.Endpoint.Id).Volume * 100 + delta); }
            catch (Exception ex) { Error("「" + card.Label + "」無法調整音量：" + ex.Message); }
        }
        void ToggleMute(SpeakerCard card)
        {
            try { Audio.SetMute(card.Endpoint.Id, !Audio.Read(card.Endpoint.Id).Muted); ShowVolume(card, Audio.Read(card.Endpoint.Id)); }
            catch (Exception ex) { failed = true; card.SetControls(false); Error("「" + card.Label + "」靜音設定失敗：" + ex.Message); }
        }
        void AdjustAll(int delta) { foreach (var card in cards.ToList()) Adjust(card, delta); }
        /// <summary>Mutes every speaker if any is playing; otherwise unmutes them all.</summary>
        void ToggleMuteAll()
        {
            try
            {
                bool mute = cards.Any(c => !Audio.Read(c.Endpoint.Id).Muted);
                foreach (var card in cards.ToList()) { Audio.SetMute(card.Endpoint.Id, mute); ShowVolume(card, Audio.Read(card.Endpoint.Id)); }
            }
            catch (Exception ex) { failed = true; Error("靜音設定失敗：" + ex.Message); }
        }
        void Error(string message) { Text("Status", message); }
        public void Dispose()
        {
            if (disposed) return;
            // Pages outlive their visibility; stop their audio streams before the process ends.
            if (surround != null) surround.Shutdown();
            if (syncTest != null) syncTest.Shutdown();
            disposed = true; timer.Stop(); tray.Visible = false;
            if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose();
            var icon = tray.Icon; tray.Dispose(); if (icon != null) icon.Dispose();
        }
    }

    static class Program
    {
        [STAThread] public static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--diagnostics" || args[0] == "--verify-controls" || args[0] == "--query-charging" || args[0] == "--query-partycast" || args[0] == "--sync-selftest" || args[0] == "--surround-selftest")) return Diagnostics(args);
            bool created;
            using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\SoundcoreDesk.Activate.v1"))
            using (var mutex = new Mutex(true, "Local\\SoundcoreDesk.Main.v1", out created))
            {
                if (!created) { activation.Set(); return 0; }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                        MessageBox.Show("程式發生錯誤：" + e.Exception.Message, "Soundcore Desk", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true;
                    };
                    // Shared theme lives in Application.Resources so the sync-test window uses it too.
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundcoreDesk.Theme.xaml")) app.Resources = (ResourceDictionary)XamlReader.Load(stream);
                    Window window;
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundcoreDesk.MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundcoreDesk.ico")) window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    using (var controller = new Controller(window, activation)) return app.Run(window);
                }
                catch (Exception ex) { MessageBox.Show(ex.ToString(), "Soundcore Desk 啟動失敗"); return 1; }
            }
        }
        static int Diagnostics(string[] args)
        {
            string output = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics.json");
            var report = new Dictionary<string, object>(); int exitCode = 0;
            try
            {
                var snapshot = Snapshot.Read(); report["snapshot"] = snapshot;
                report["selected"] = snapshot.GlowEndpoints().FirstOrDefault(d => d.Connected && !d.HandsFree);
                var device = report["selected"] as AudioDevice;
                if (device != null) report["volume"] = Audio.Read(device.Id);
                if (args[0] == "--sync-selftest")
                {
                    // Silent engine check: opens two muted render streams (the same endpoint twice if only one is connected).
                    var outputs = snapshot.Audio.Where(d => d.Connected && !d.HandsFree).OrderByDescending(d => d.IsGlow).ToList();
                    if (outputs.Count == 0) throw new InvalidOperationException("沒有已連線的播放裝置。");
                    var first = outputs[0]; var second = outputs.Count > 1 ? outputs[1] : outputs[0];
                    int seconds = args.Length > 2 ? Math.Max(4, Math.Min(60, int.Parse(args[2]))) : 8;
                    using (var session = new SyncTestSession(first.Id, second.Id, 0))
                    {
                        session.A.Muted = true; session.B.Muted = true;
                        string startError = session.Start();
                        if (startError != null) { report["syncStartError"] = startError; exitCode = 1; }
                        else
                        {
                            Thread.Sleep(seconds * 1000);
                            Func<ClickStream, AudioDevice, object> describe = (s, d) => new { Device = d.Name, Format = s.Format, FramesWritten = s.FramesWritten, WindowsDriftMs = s.WindowsDriftMs, Error = s.Error };
                            report["syncA"] = describe(session.A, first); report["syncB"] = describe(session.B, second);
                            report["syncSeconds"] = seconds; report["relativeWindowsDriftMs"] = session.RelativeWindowsDriftMs;
                            if (session.A.Error != null || session.B.Error != null) exitCode = 1;
                        }
                    }
                }
                if (args[0] == "--surround-selftest")
                {
                    // Silent pipeline check: loopback of the default output → mixer (master 0) → two outputs.
                    var outputs = snapshot.Audio.Where(d => d.Connected && !d.HandsFree).OrderByDescending(d => d.IsGlow).ToList();
                    var src = snapshot.Audio.FirstOrDefault(d => d.IsDefault && d.Connected);
                    if (src == null || outputs.Count == 0) throw new InvalidOperationException("沒有可用的來源或播放裝置。");
                    var f = outputs[0]; var r = outputs.Count > 1 ? outputs[1] : outputs[0];
                    int seconds = args.Length > 2 ? Math.Max(4, Math.Min(60, int.Parse(args[2]))) : 8;
                    using (var engine = new SurroundEngine(src.Id, f.Id, r.Id))
                    {
                        string startError = engine.Start(0.5f, 0f);   // master 0 from the first frame: nothing audible
                        if (startError != null) { report["surroundStartError"] = startError; exitCode = 1; }
                        else
                        {
                            Thread.Sleep(seconds * 1000);
                            report["surround"] = new {
                                Source = src.Name, SourceFormat = engine.SourceFormat.Describe(), SourceMask = engine.SourceFormat.ChannelMask,
                                Front = f.Name, FrontFormat = engine.FrontFormat, Rear = r.Name, RearFormat = engine.RearFormat,
                                Seconds = seconds, PaddedSeconds = engine.PaddedFrames / (double)engine.SourceFormat.Rate,
                                Snaps = engine.Snaps, FrontMissingFrames = engine.FrontMissingFrames, RearMissingFrames = engine.RearMissingFrames,
                                Error = engine.Error
                            };
                            if (engine.Error != null) exitCode = 1;
                        }
                    }
                }
                if (args[0] == "--query-charging")
                {
                    if (device == null || string.IsNullOrEmpty(device.BluetoothAddress)) throw new InvalidOperationException("沒有已連線且可辨識的 Glow Mini。");
                    report["vendorCharging"] = SoundcoreProtocol.ReadCharging(device.BluetoothAddress);
                }
                if (args[0] == "--query-partycast")
                {
                    if (device == null || string.IsNullOrEmpty(device.BluetoothAddress)) throw new InvalidOperationException("沒有已連線且可辨識的 Glow Mini。");
                    report["partyCast"] = SoundcoreProtocol.ReadPartyCast(device.BluetoothAddress);
                }
                if (args[0] == "--verify-controls")
                {
                    if (device == null) throw new InvalidOperationException("沒有已連線的 Glow Mini 立體聲端點。");
                    var before = Audio.Read(device.Id);
                    var otherBefore = snapshot.Audio.Where(d => d.Connected && d.Id != device.Id).ToDictionary(d => d.Id, d => Audio.Read(d.Id));
                    try
                    {
                        // Mute first, then briefly lower volume; always restore the original state.
                        Audio.SetMute(device.Id, true);
                        Thread.Sleep(350);
                        if (!Audio.Read(device.Id).Muted) throw new Exception("靜音驗證失敗。");
                        float target = Math.Max(0, before.Volume - 0.01f);
                        Audio.SetVolume(device.Id, target);
                        Thread.Sleep(350);
                        if (Math.Abs(Audio.Read(device.Id).Volume - target) > 0.005) throw new Exception("音量讀寫驗證失敗。");
                        report["controlTest"] = "PASS";
                    }
                    finally { try { Audio.SetVolume(device.Id, before.Volume); Thread.Sleep(500); } finally { Audio.SetMute(device.Id, before.Muted); } }
                    var after = Audio.Read(device.Id);
                    if (Math.Abs(after.Volume - before.Volume) > 0.005 || after.Muted != before.Muted) throw new Exception("音量狀態還原驗證失敗。");
                    foreach (var pair in otherBefore)
                    {
                        var now = Audio.Read(pair.Key);
                        if (Math.Abs(now.Volume - pair.Value.Volume) > 0.005 || now.Muted != pair.Value.Muted) throw new Exception("其他端點音量出現變化：" + pair.Key);
                    }
                    report["restored"] = after; report["otherEndpointsUnchanged"] = true;
                    if (string.IsNullOrEmpty(device.BluetoothAddress)) throw new Exception("無法透過裝置容器對應藍牙位址。");
                }
            }
            catch (Exception ex) { report["error"] = ex.ToString(); exitCode = 1; }
            File.WriteAllText(output, new JavaScriptSerializer().Serialize(report), new System.Text.UTF8Encoding(false));
            return exitCode;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace SoundcoreDesk
{
    /// <summary>
    /// Two-speaker click alignment and drift test. A page of the main window: switching to another
    /// page keeps the clicks playing; they stop only via 「停止」 or when the app exits.
    /// </summary>
    public sealed class SyncTestController
    {
        readonly FrameworkElement view;
        readonly ComboBox deviceA, deviceB;
        readonly Slider slider;
        readonly CheckBox playA, playB;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly List<SyncMark> history = SyncMark.Load();
        readonly List<SyncMark> sessionMarks = new List<SyncMark>();
        SyncTestSession session;
        AudioDevice sessionA, sessionB;
        bool starting, closed, adjusting, loading;

        public FrameworkElement View { get { return view; } }
        public bool Running { get { return session != null; } }

        public SyncTestController()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundcoreDesk.SyncTestWindow.xaml"))
                view = (FrameworkElement)XamlReader.Load(stream);
            deviceA = Find<ComboBox>("DeviceA"); deviceB = Find<ComboBox>("DeviceB");
            slider = Find<Slider>("OffsetSlider"); playA = Find<CheckBox>("PlayA"); playB = Find<CheckBox>("PlayB");

            Click("RefreshDevices", LoadDevices);
            Click("StartTest", StartSession);
            Click("StopTest", () => StopSession("已停止。"));
            Click("RecordMark", RecordMark);
            Click("OffsetMinus10", () => Nudge(-10)); Click("OffsetMinus1", () => Nudge(-1));
            Click("OffsetPlus1", () => Nudge(1)); Click("OffsetPlus10", () => Nudge(10));
            Click("OffsetZero", () => slider.Value = 0);
            slider.ValueChanged += delegate { OnOffsetChanged(); };
            RoutedEventHandler mute = delegate
            {
                if (session == null) return;
                session.A.Muted = playA.IsChecked != true; session.B.Muted = playB.IsChecked != true;
            };
            playA.Checked += mute; playA.Unchecked += mute; playB.Checked += mute; playB.Unchecked += mute;
            deviceA.SelectionChanged += delegate { UpdateButtons(); };
            deviceB.SelectionChanged += delegate { UpdateButtons(); };
            timer.Tick += delegate { UpdateLive(); };
            OnOffsetChanged(); UpdateMarks(); UpdateButtons();
        }

        /// <summary>Called each time the page is switched to: re-lists devices unless a test is playing.</summary>
        public void Shown() { LoadDevices(); }

        /// <summary>App exit: stop both click streams before the process ends.</summary>
        public void Shutdown()
        {
            if (closed) return;
            closed = true; timer.Stop();
            var current = session; session = null;
            if (current != null) current.Dispose();
        }

        T Find<T>(string name) where T : class { return (T)view.FindName(name); }
        void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        void Click(string name, Action action) { Find<Button>(name).Click += delegate { action(); }; }
        void Status(string message) { Text("TestStatus", message); }

        async void LoadDevices()
        {
            if (session != null || starting || loading || closed) return;
            loading = true;
            Text("DeviceHint", "正在讀取音訊裝置…");
            Snapshot snapshot;
            try { snapshot = await Task.Run(() => Snapshot.Read()); }
            catch (Exception ex) { Text("DeviceHint", "讀取音訊裝置失敗：" + ex.Message); return; }
            finally { loading = false; }
            if (closed) return;
            if (session != null || starting) { Text("DeviceHint", ""); return; }
            // Hands-Free endpoints are mono call audio; opening one would switch the speaker to call mode.
            var outputs = snapshot.Audio.Where(d => d.Connected && !d.HandsFree).OrderByDescending(d => d.IsGlow).ThenBy(d => d.Name).ToList();
            string previousA = deviceA.SelectedItem is AudioDevice ? ((AudioDevice)deviceA.SelectedItem).Id : null;
            string previousB = deviceB.SelectedItem is AudioDevice ? ((AudioDevice)deviceB.SelectedItem).Id : null;
            deviceA.ItemsSource = outputs; deviceB.ItemsSource = outputs;
            var a = outputs.FirstOrDefault(d => d.Id == previousA) ?? outputs.FirstOrDefault(d => d.IsGlow) ?? outputs.FirstOrDefault();
            var b = outputs.FirstOrDefault(d => d.Id == previousB && (a == null || d.Id != a.Id)) ?? outputs.FirstOrDefault(d => d.IsGlow && (a == null || d.Id != a.Id)) ?? outputs.FirstOrDefault(d => a == null || d.Id != a.Id);
            deviceA.SelectedItem = a; deviceB.SelectedItem = b;
            int glows = outputs.Count(d => d.IsGlow);
            Text("DeviceHint", glows >= 2 ? "" : "目前只找到 " + glows + " 顆已連線的 Glow Mini 音訊裝置。請先退出 TWS 和 PartyCast，到 Windows 藍牙設定把兩顆都連上，再按「重新整理」。");
            UpdateButtons();
        }

        async void StartSession()
        {
            var a = deviceA.SelectedItem as AudioDevice; var b = deviceB.SelectedItem as AudioDevice;
            if (session != null || starting || a == null || b == null) return;
            if (a.Id == b.Id) { Status("A 和 B 要選不同的喇叭。"); return; }
            starting = true; UpdateButtons(); Status("正在開啟兩條音訊串流…");
            var next = new SyncTestSession(a.Id, b.Id, slider.Value);
            next.A.Muted = playA.IsChecked != true; next.B.Muted = playB.IsChecked != true;
            string error;
            try { error = await Task.Run(() => next.Start()); }
            catch (Exception ex) { error = ex.Message; }
            starting = false;
            if (closed || error != null)
            {
                var disposing = Task.Run(() => next.Dispose());
                if (!closed) { Status("無法開始：" + error); UpdateButtons(); }
                return;
            }
            session = next; sessionA = a; sessionB = b; sessionMarks.Clear();
            timer.Start(); UpdateLive(); UpdateMarks(); UpdateButtons();
            Status("播放中 · A：" + next.A.Format + " · B：" + next.B.Format);
        }

        void StopSession(string message)
        {
            timer.Stop();
            var current = session; session = null;
            if (current != null) Task.Run(() => current.Dispose());   // joins audio threads off the UI thread
            if (!closed) { UpdateButtons(); if (message != null) Status(message); }
        }

        void Nudge(double delta) { slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, Math.Round(slider.Value) + delta)); }

        void OnOffsetChanged()
        {
            if (adjusting) return;
            double rounded = Math.Round(slider.Value);
            if (rounded != slider.Value) { adjusting = true; slider.Value = rounded; adjusting = false; }
            if (session != null) session.SetOffset(rounded);
            Text("OffsetNumber", (rounded > 0 ? "+" : "") + rounded.ToString("0"));
            Text("OffsetMeaning", rounded == 0 ? "兩顆都不延遲" : rounded > 0 ? "B（低音）延後 " + rounded + " ms" : "A（高音）延後 " + (-rounded) + " ms");
        }

        void UpdateLive()
        {
            if (session == null) return;
            string error = session.A.Error != null ? "A：" + session.A.Error : session.B.Error != null ? "B：" + session.B.Error : null;
            if (error != null) { StopSession("播放中斷：" + error + "（喇叭可能斷線，請確認後重新開始）"); return; }
            Text("Elapsed", Clock(DateTime.UtcNow - session.StartedUtc));
            var drift = session.RelativeWindowsDriftMs;
            Text("WindowsDrift", "Windows 端相對漂移：" + (drift.HasValue ? drift.Value.ToString("+0.00;-0.00;0.00") + " ms（A 相對 B，開始 3 秒後起算）" : "測量中…"));
        }

        void RecordMark()
        {
            if (session == null) return;
            var mark = new SyncMark {
                RecordedUtc = DateTime.UtcNow, ElapsedSeconds = (DateTime.UtcNow - session.StartedUtc).TotalSeconds,
                OffsetMs = Math.Round(slider.Value), WindowsDriftMs = session.RelativeWindowsDriftMs,
                DeviceA = Describe(sessionA), DeviceB = Describe(sessionB)
            };
            sessionMarks.Add(mark); history.Add(mark);
            try { SyncMark.Save(history); }
            catch (IOException ex) { Status("紀錄未能存檔：" + ex.Message); }
            catch (UnauthorizedAccessException ex) { Status("紀錄未能存檔：" + ex.Message); }
            UpdateMarks();
        }

        void UpdateMarks()
        {
            if (sessionMarks.Count == 0) { Text("Marks", "尚無紀錄"); Text("DriftSummary", ""); return; }
            Text("Marks", string.Join("\n", sessionMarks.Select((m, i) => "#" + (i + 1) + "  " + Clock(TimeSpan.FromSeconds(m.ElapsedSeconds)) + "   對齊 " + m.OffsetMs.ToString("+0;-0;0") + " ms" + (m.WindowsDriftMs.HasValue ? "   （Windows " + m.WindowsDriftMs.Value.ToString("+0.0;-0.0;0.0") + " ms）" : ""))));
            var change = SyncMark.OffsetChangeMs(sessionMarks);
            if (!change.HasValue) { Text("DriftSummary", "已記錄起點。幾分鐘後再聽一次，若不再合成一聲就重新對齊並記錄。"); return; }
            double minutes = (sessionMarks[sessionMarks.Count - 1].ElapsedSeconds - sessionMarks[0].ElapsedSeconds) / 60.0;
            string rate = minutes > 0.5 ? "，約每分鐘 " + (change.Value / minutes).ToString("0.0") + " ms" : "";
            string verdict = Math.Abs(change.Value) <= 10 ? "前後差距在 10 ms 內，固定延遲就夠用。" : "差距超過 10 ms，完整版需要自動修正漂移。";
            Text("DriftSummary", Clock(TimeSpan.FromMinutes(minutes)) + " 內對齊值變了 " + change.Value.ToString("+0;-0;0") + " ms" + rate + "。" + verdict);
        }

        void UpdateButtons()
        {
            bool idle = session == null && !starting;
            Find<Button>("StartTest").IsEnabled = idle && deviceA.SelectedItem != null && deviceB.SelectedItem != null;
            Find<Button>("StopTest").IsEnabled = session != null;
            Find<Button>("RecordMark").IsEnabled = session != null;
            Find<Button>("RefreshDevices").IsEnabled = idle;
            deviceA.IsEnabled = idle; deviceB.IsEnabled = idle;
            if (session == null) Text("Elapsed", starting ? "啟動中…" : "尚未開始");
        }

        static string Describe(AudioDevice device)
        { return device == null ? null : device.Name + (string.IsNullOrEmpty(device.BluetoothAddress) ? "" : " · " + device.BluetoothAddress); }
        static string Clock(TimeSpan span) { return ((int)span.TotalMinutes).ToString("00") + ":" + span.Seconds.ToString("00"); }
    }
}

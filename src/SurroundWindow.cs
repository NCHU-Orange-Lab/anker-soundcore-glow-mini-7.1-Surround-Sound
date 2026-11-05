using System;
using System.Diagnostics;
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
    /// Front/rear 4-channel surround from a multichannel source endpoint. A page of the main window:
    /// switching to another page keeps surround playing; it stops only via 「停止」 or when the app exits.
    /// </summary>
    public sealed class SurroundController
    {
        static readonly string[] OutputNames = { "FL", "FR", "RL", "RR" };
        readonly FrameworkElement view;
        readonly ComboBox source, front, rear;
        readonly Slider offset, frontLevel, rearLevel, ambienceDelay, ambienceLevel;
        readonly RadioButton fillAmbience, fillCopy, fillOff;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        readonly SurroundSettings settings = SurroundSettings.Load();
        readonly float[] meters = new float[4];
        SurroundEngine engine;
        Snapshot snapshot;
        WaveFormatInfo sourceFormat;
        bool starting, closed, adjusting, loading, selecting;

        public FrameworkElement View { get { return view; } }
        public bool Running { get { return engine != null; } }

        public SurroundController()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundcoreDesk.SurroundWindow.xaml"))
                view = (FrameworkElement)XamlReader.Load(stream);
            source = Find<ComboBox>("SourceDevice"); front = Find<ComboBox>("FrontDevice"); rear = Find<ComboBox>("RearDevice");
            offset = Find<Slider>("OffsetSlider"); frontLevel = Find<Slider>("FrontLevel"); rearLevel = Find<Slider>("RearLevel");
            ambienceDelay = Find<Slider>("AmbienceDelay"); ambienceLevel = Find<Slider>("AmbienceLevel");
            fillAmbience = Find<RadioButton>("FillAmbience"); fillCopy = Find<RadioButton>("FillCopy"); fillOff = Find<RadioButton>("FillOff");

            adjusting = true;
            offset.Value = Math.Max(-300, Math.Min(300, settings.OffsetMs));
            frontLevel.Value = Math.Max(0, Math.Min(150, settings.FrontLevel * 100)); rearLevel.Value = Math.Max(0, Math.Min(150, settings.RearLevel * 100));
            var fill = settings.EffectiveFill();
            fillAmbience.IsChecked = fill == RearFill.Ambience; fillCopy.IsChecked = fill == RearFill.Copy; fillOff.IsChecked = fill == RearFill.Off;
            ambienceDelay.Value = Math.Max(0, Math.Min(SurroundMixer.MaxAmbienceDelayMs, settings.AmbienceDelayMs));
            ambienceLevel.Value = Math.Max(0, Math.Min(200, settings.AmbienceLevel * 100));
            adjusting = false;

            Click("RefreshDevices", LoadDevices);
            Click("OpenSoundPanel", () => { try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true }); } catch (Exception ex) { Status("無法開啟聲音控制台：" + ex.Message); } });
            Click("StartSurround", Start);
            Click("StopSurround", () => Stop("已停止。"));
            for (int i = 0; i < 4; i++) { int output = i; Click("Test" + OutputNames[i], () => { if (engine != null && engine.Mixer != null) engine.Mixer.RequestTest(output); }); }
            Click("OffsetMinus10", () => Nudge(-10)); Click("OffsetMinus1", () => Nudge(-1));
            Click("OffsetPlus1", () => Nudge(1)); Click("OffsetPlus10", () => Nudge(10));
            Click("OffsetZero", () => offset.Value = 0);
            offset.ValueChanged += delegate { ApplySettings(); };
            frontLevel.ValueChanged += delegate { ApplySettings(); };
            rearLevel.ValueChanged += delegate { ApplySettings(); };
            foreach (var choice in new[] { fillAmbience, fillCopy, fillOff }) choice.Checked += delegate { ApplySettings(); };
            ambienceDelay.ValueChanged += delegate { ApplySettings(); };
            ambienceLevel.ValueChanged += delegate { ApplySettings(); };
            // A choice the user makes is remembered at once; fallbacks picked by LoadDevices are not.
            source.SelectionChanged += delegate { if (!selecting) settings.SourceId = IdOf(source.SelectedItem) ?? settings.SourceId; ProbeSource(); UpdateButtons(); };
            front.SelectionChanged += delegate { if (!selecting) settings.FrontId = IdOf(front.SelectedItem) ?? settings.FrontId; UpdateButtons(); };
            rear.SelectionChanged += delegate { if (!selecting) settings.RearId = IdOf(rear.SelectedItem) ?? settings.RearId; UpdateButtons(); };
            timer.Tick += delegate { UpdateLive(); };
            ApplySettings(); UpdateButtons();
        }

        /// <summary>Called each time the page is switched to: re-lists devices unless surround is running.</summary>
        public void Shown() { LoadDevices(); }

        /// <summary>App exit: stop the audio streams before the process ends and keep the last settings.</summary>
        public void Shutdown()
        {
            if (closed) return;
            closed = true; timer.Stop();
            var current = engine; engine = null;
            if (current != null) current.Dispose();
            settings.Save();
        }

        T Find<T>(string name) where T : class { return (T)view.FindName(name); }
        void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        void Click(string name, Action action) { Find<Button>(name).Click += delegate { action(); }; }
        void Status(string message) { Text("SurroundStatus", message); }
        static string IdOf(object item) { var d = item as AudioDevice; return d == null ? null : d.Id; }

        async void LoadDevices()
        {
            if (engine != null || starting || loading || closed) return;
            loading = true;
            Status("正在讀取音訊裝置…");
            try { snapshot = await Task.Run(() => Snapshot.Read()); }
            catch (Exception ex) { Status("讀取音訊裝置失敗：" + ex.Message); return; }
            finally { loading = false; }
            if (closed || engine != null || starting) return;
            // Hands-Free endpoints are mono call audio; opening one switches the speaker into call mode.
            var outputs = snapshot.Audio.Where(d => d.Connected && !d.HandsFree).ToList();
            var sources = outputs.OrderByDescending(d => !d.IsGlow).ThenByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
            var speakers = outputs.OrderByDescending(d => d.IsGlow).ThenBy(d => d.Name).ToList();
            // The remembered assignment wins whenever its devices are present. A speaker that is briefly away is
            // replaced only for now, so front and rear do not come back swapped once it reconnects.
            string s = settings.SourceId ?? IdOf(source.SelectedItem), f = settings.FrontId ?? IdOf(front.SelectedItem), r = settings.RearId ?? IdOf(rear.SelectedItem);
            selecting = true;
            try
            {
                source.ItemsSource = sources; front.ItemsSource = speakers; rear.ItemsSource = speakers;
                var chosenSource = sources.FirstOrDefault(d => d.Id == s) ?? sources.FirstOrDefault(d => !d.IsGlow && d.IsDefault) ?? sources.FirstOrDefault(d => !d.IsGlow);
                var chosenFront = speakers.FirstOrDefault(d => d.Id == f && d.Id != r) ?? speakers.FirstOrDefault(d => d.IsGlow && d.Id != r) ?? speakers.FirstOrDefault(d => d.IsGlow);
                var chosenRear = speakers.FirstOrDefault(d => d.Id == r && (chosenFront == null || d.Id != chosenFront.Id)) ?? speakers.FirstOrDefault(d => d.IsGlow && (chosenFront == null || d.Id != chosenFront.Id));
                source.SelectedItem = chosenSource; front.SelectedItem = chosenFront; rear.SelectedItem = chosenRear;
            }
            finally { selecting = false; }
            int glows = speakers.Count(d => d.IsGlow);
            var away = new System.Collections.Generic.List<string>();
            if (f != null && !speakers.Any(d => d.Id == f)) away.Add("前方");
            if (r != null && !speakers.Any(d => d.Id == r)) away.Add("後方");
            Text("DeviceHint", (glows >= 2 ? "" : "目前只找到 " + glows + " 個已連線的 Glow Mini 音訊裝置。前、後各需要一個：兩組 TWS 的主喇叭，或兩顆各自連電腦的喇叭。")
                + (away.Count == 0 ? "" : (glows >= 2 ? "" : "\n") + "記住的" + string.Join("、", away) + "喇叭目前沒有連線，暫時換成其他裝置；它重新連上後按「重新整理」或切回這頁就會恢復。"));
            Status("準備就緒");
            ProbeSource(); UpdateButtons();
        }

        async void ProbeSource()
        {
            var device = source.SelectedItem as AudioDevice;
            sourceFormat = null;
            if (device == null)
            {
                Text("SourceFormat", "—");
                Text("SourceHint", "沒有可當來源的播放裝置。請安裝可設成 5.1／7.1 的虛擬音效裝置（例如 VB-CABLE），把它設為 Windows 預設輸出，再按「重新整理」。");
                return;
            }
            Text("SourceFormat", "正在讀取格式…");
            WaveFormatInfo format;
            try { format = await Task.Run(() => SurroundEngine.Probe(device.Id)); }
            catch (Exception ex) { Text("SourceFormat", "無法讀取格式：" + ex.Message); return; }
            if (closed || !ReferenceEquals(source.SelectedItem, device)) return;
            sourceFormat = format;
            Text("SourceFormat", "格式：" + format.Describe());
            var hints = new System.Collections.Generic.List<string>();
            if (device.IsGlow) hints.Add("來源不應該是 Glow Mini 本身：請選虛擬音效裝置。");
            if (!SurroundMatrix.HasRear(format.ChannelMask)) hints.Add("這個裝置目前只有前方聲道，後方只能播前方的複製。請按「聲音控制台」→ 選這個裝置 →「設定」→ 選 5.1 或 7.1。");
            if (!device.IsDefault) hints.Add("它不是 Windows 預設輸出，其他程式的聲音不會進來。請在聲音控制台把它「設為預設值」。");
            Text("SourceHint", string.Join("\n", hints));
            UpdateButtons();
        }

        async void Start()
        {
            var s = source.SelectedItem as AudioDevice; var f = front.SelectedItem as AudioDevice; var r = rear.SelectedItem as AudioDevice;
            if (engine != null || starting || s == null || f == null || r == null) return;
            if (f.Id == r.Id) { Status("前方和後方要選不同的喇叭。"); return; }
            if (s.Id == f.Id || s.Id == r.Id) { Status("來源不能同時是前方或後方喇叭，否則聲音會回授。"); return; }
            starting = true; UpdateButtons(); Status("正在開啟來源與兩組喇叭…");
            var next = new SurroundEngine(s.Id, f.Id, r.Id);
            next.SetOffset(Math.Round(offset.Value));
            string error;
            try { error = await Task.Run(() => next.Start(0.5f)); }
            catch (Exception ex) { error = ex.Message; }
            starting = false;
            if (closed || error != null)
            {
                var disposing = Task.Run(() => next.Dispose());
                if (!closed) { Status("無法開始：" + error); UpdateButtons(); }
                return;
            }
            engine = next;
            settings.SourceId = s.Id; settings.FrontId = f.Id; settings.RearId = r.Id; settings.Save();
            ApplySettings(); timer.Start(); UpdateButtons();
            Status("環繞中 · 來源 " + next.SourceFormat.Describe() + " · 前方 " + next.FrontFormat + " · 後方 " + next.RearFormat);
        }

        void Stop(string message)
        {
            timer.Stop();
            var current = engine; engine = null;
            if (current != null) Task.Run(() => current.Dispose());   // joins audio threads off the UI thread
            if (!closed)
            {
                foreach (var name in OutputNames) Find<ProgressBar>("Meter" + name).Value = 0;
                Text("RearMode", "");
                UpdateButtons(); if (message != null) Status(message);
            }
        }

        void Nudge(double delta) { offset.Value = Math.Max(offset.Minimum, Math.Min(offset.Maximum, Math.Round(offset.Value) + delta)); }

        void ApplySettings()
        {
            if (adjusting) return;
            double rounded = Math.Round(offset.Value);
            if (rounded != offset.Value) { adjusting = true; offset.Value = rounded; adjusting = false; }
            Text("OffsetText", (rounded > 0 ? "+" : "") + rounded + " ms · " + (rounded == 0 ? "兩組都不延遲" : rounded > 0 ? "後方延後 " + rounded + " ms" : "前方延後 " + (-rounded) + " ms"));
            Text("FrontLevelText", Math.Round(frontLevel.Value) + "%"); Text("RearLevelText", Math.Round(rearLevel.Value) + "%");
            var fill = fillAmbience.IsChecked == true ? RearFill.Ambience : fillCopy.IsChecked == true ? RearFill.Copy : RearFill.Off;
            double delay = Math.Round(ambienceDelay.Value), level = Math.Round(ambienceLevel.Value);
            if (delay != ambienceDelay.Value || level != ambienceLevel.Value) { adjusting = true; ambienceDelay.Value = delay; ambienceLevel.Value = level; adjusting = false; }
            Text("AmbienceDelayText", delay == 0 ? "0 ms · 與前方同時" : delay + " ms");
            Text("AmbienceLevelText", level + "%");
            Find<UIElement>("AmbiencePanel").Visibility = fill == RearFill.Ambience ? Visibility.Visible : Visibility.Collapsed;
            Text("FillHint", fill == RearFill.Ambience
                ? "把左右聲道之間的差異（混響、空間感、寬廣的音色）分離出來送到後方；人聲、主旋律等左右相同的聲音留在前方。後方預設比前方晚 15 ms，聲音位置才會留在前方，後方只增加包圍感。分析需要約 11 ms，這段時間前方也一起等，所以整體會多約 11 ms 延遲。"
                : fill == RearFill.Copy ? "後方播放前方聲音的 80%，和前方同時。人聲也會從後方出來，位置會被拉到房間中間。"
                : "沒有環繞內容時，後方不出聲。");
            settings.OffsetMs = rounded; settings.FrontLevel = frontLevel.Value / 100; settings.RearLevel = rearLevel.Value / 100;
            settings.RearMode = (int)fill; settings.StereoToRear = fill != RearFill.Off;
            settings.AmbienceDelayMs = delay; settings.AmbienceLevel = level / 100;
            if (engine == null) return;
            engine.SetOffset(rounded);
            if (engine.Mixer != null)
            {
                engine.Mixer.FrontLevel = (float)settings.FrontLevel; engine.Mixer.RearLevel = (float)settings.RearLevel;
                engine.Mixer.Fill = fill; engine.Mixer.AmbienceDelayMs = delay; engine.Mixer.AmbienceLevel = (float)settings.AmbienceLevel;
            }
        }

        void UpdateLive()
        {
            if (engine == null) return;
            if (engine.Error != null) { Stop("環繞中斷：" + engine.Error + "（裝置可能斷線，確認後重新開始）"); return; }
            var mixer = engine.Mixer;
            for (int i = 0; i < 4; i++)
            {
                meters[i] = Math.Max(mixer.TakePeak(i), meters[i] * 0.8f);
                Find<ProgressBar>("Meter" + OutputNames[i]).Value = meters[i];
            }
            var fill = mixer.Fill;
            Text("RearMode", mixer.SourceRearActive ? "後方目前播放：來源的環繞聲道"
                : fill == RearFill.Ambience ? "後方目前播放：從前方分離的環境聲（擴展環繞，比前方晚 " + Math.Round(mixer.AmbienceDelayMs) + " ms）"
                : fill == RearFill.Copy ? "後方目前播放：前方的複製（來源沒有環繞內容）"
                : "後方目前沒有聲音（來源沒有環繞內容）");
            var up = DateTime.UtcNow - engine.StartedUtc;
            Find<TextBlock>("SurroundStatus").ToolTip = "對齊修正 " + engine.Snaps + " 次 · 補靜音 " + (engine.PaddedFrames / (double)engine.SourceFormat.Rate).ToString("0.0") + " 秒 · 前方缺資料 " + engine.FrontMissingFrames + " 幀 · 後方缺資料 " + engine.RearMissingFrames + " 幀 · 已執行 " + (int)up.TotalMinutes + " 分 " + up.Seconds + " 秒";
        }

        void UpdateButtons()
        {
            bool idle = engine == null && !starting && !loading;
            Find<Button>("StartSurround").IsEnabled = idle && source.SelectedItem != null && front.SelectedItem != null && rear.SelectedItem != null;
            Find<Button>("StopSurround").IsEnabled = engine != null;
            foreach (var name in OutputNames) Find<Button>("Test" + name).IsEnabled = engine != null;
            Find<Button>("RefreshDevices").IsEnabled = idle;
            source.IsEnabled = idle; front.IsEnabled = idle; rear.IsEnabled = idle;
        }
    }
}

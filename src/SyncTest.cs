using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

namespace SoundcoreDesk
{
    /// <summary>Pure click-track maths, kept separate so it can be tested offline.</summary>
    public static class ClickTrack
    {
        public const double IntervalSeconds = 0.6;   // ±300 ms offsets stay unambiguous
        public const int BeatsPerBar = 4;            // first beat of each bar is accented
        public const double PitchA = 2000, PitchB = 1250;

        /// <summary>Sample value at frame <paramref name="n"/>; silence before 0 and between clicks.</summary>
        public static float Sample(long n, int rate, double pitchHz)
        {
            if (n < 0 || rate <= 0) return 0;
            long period = (long)Math.Round(rate * IntervalSeconds);
            long beat = n / period, pos = n % period;
            if (pos >= rate * 4 / 1000) return 0;   // 4 ms click
            double t = pos / (double)rate;
            double amplitude = beat % BeatsPerBar == 0 ? 0.8 : 0.45;
            return (float)(amplitude * Math.Sin(2 * Math.PI * pitchHz * t) * Math.Exp(-t / 0.0008));
        }

        /// <summary>Positive offset delays B, negative delays A; the other side plays undelayed.</summary>
        public static void Delays(double offsetMs, out double delayAMs, out double delayBMs)
        {
            delayAMs = offsetMs < 0 ? -offsetMs : 0;
            delayBMs = offsetMs > 0 ? offsetMs : 0;
        }

        public static long DelayFrames(double delayMs, int rate) { return (long)Math.Round(delayMs * rate / 1000.0); }

        /// <summary>How far the device clock ran ahead (+) of the system clock since the baseline, in ms.</summary>
        public static double DriftMs(double deviceStartSec, double deviceNowSec, double systemStartSec, double systemNowSec)
        { return ((deviceNowSec - deviceStartSec) - (systemNowSec - systemStartSec)) * 1000.0; }
    }

    /// <summary>One shared-mode WASAPI render stream playing the click track on its own MTA thread.</summary>
    public sealed class ClickStream : IDisposable
    {
        static readonly Guid FloatFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        static readonly Guid PcmFormat = new Guid("00000001-0000-0010-8000-00aa00389b71");
        readonly string deviceId;
        readonly double pitch;
        readonly ManualResetEvent go;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Thread thread;
        readonly object gate = new object();
        volatile bool stop, muted;
        long delayMicros;
        string error, format;
        double? driftMs;
        long framesWritten;

        public ClickStream(string deviceId, double pitch, ManualResetEvent go)
        {
            this.deviceId = deviceId; this.pitch = pitch; this.go = go;
            thread = new Thread(Run) { IsBackground = true, Name = "SoundcoreDesk click " + pitch };
            thread.SetApartmentState(ApartmentState.MTA);
        }

        public WaitHandle Ready { get { return ready; } }
        public string Error { get { lock (gate) return error; } }
        public string Format { get { lock (gate) return format; } }
        public double? WindowsDriftMs { get { lock (gate) return driftMs; } }
        public long FramesWritten { get { return Interlocked.Read(ref framesWritten); } }
        public bool Muted { get { return muted; } set { muted = value; } }
        public void SetDelayMs(double ms) { Interlocked.Exchange(ref delayMicros, (long)Math.Round(Math.Max(0, ms) * 1000)); }
        public void Start() { thread.Start(); }

        void Fail(string message) { lock (gate) if (error == null) error = message; ready.Set(); }

        void Run()
        {
            IMMDeviceEnumerator enumerator = null; IMMDevice device = null; IAudioClient client = null;
            IAudioRenderClient render = null; IAudioClock clock = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
                Audio.Check(enumerator.GetDevice(deviceId, out device));
                object instance; Guid iid = typeof(IAudioClient).GUID;
                Audio.Check(device.Activate(ref iid, 23, IntPtr.Zero, out instance));
                client = (IAudioClient)instance;

                IntPtr mix; Audio.Check(client.GetMixFormat(out mix));
                int channels, rate, bits; bool isFloat;
                try
                {
                    int tag = (ushort)Marshal.ReadInt16(mix, 0);
                    channels = (ushort)Marshal.ReadInt16(mix, 2); rate = Marshal.ReadInt32(mix, 4); bits = (ushort)Marshal.ReadInt16(mix, 14);
                    Guid sub = Guid.Empty;
                    if (tag == 0xFFFE) { var raw = new byte[16]; Marshal.Copy(mix + 24, raw, 0, 16); sub = new Guid(raw); }
                    isFloat = bits == 32 && (tag == 3 || (tag == 0xFFFE && sub == FloatFormat));
                    bool isPcm16 = bits == 16 && (tag == 1 || (tag == 0xFFFE && sub == PcmFormat));
                    if ((!isFloat && !isPcm16) || channels < 1) throw new NotSupportedException("不支援的音訊格式（" + bits + " bit, tag " + tag + "）。");
                    // Shared mode, event driven, 100 ms engine buffer.
                    Audio.Check(client.Initialize(0, 0x00040000, 1000000, 0, mix, IntPtr.Zero));
                }
                finally { Marshal.FreeCoTaskMem(mix); }
                lock (gate) format = rate + " Hz · " + channels + " ch · " + (isFloat ? "32-bit float" : "16-bit PCM");

                uint bufferFrames; Audio.Check(client.GetBufferSize(out bufferFrames));
                using (var wake = new AutoResetEvent(false))
                {
                    Audio.Check(client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle()));
                    iid = typeof(IAudioRenderClient).GUID; Audio.Check(client.GetService(ref iid, out instance)); render = (IAudioRenderClient)instance;
                    iid = typeof(IAudioClock).GUID; Audio.Check(client.GetService(ref iid, out instance)); clock = (IAudioClock)instance;
                    ulong clockFrequency; Audio.Check(clock.GetFrequency(out clockFrequency));

                    var floats = new float[bufferFrames * channels]; var shorts = isFloat ? null : new short[bufferFrames * channels];
                    Action<uint> fill = frames =>
                    {
                        IntPtr data; Audio.Check(render.GetBuffer(frames, out data));
                        long delay = ClickTrack.DelayFrames(Interlocked.Read(ref delayMicros) / 1000.0, rate);
                        long start = Interlocked.Read(ref framesWritten);
                        bool silent = muted;
                        for (int i = 0; i < frames; i++)
                        {
                            float value = silent ? 0 : ClickTrack.Sample(start + i - delay, rate, pitch);
                            for (int c = 0; c < channels; c++) floats[i * channels + c] = value;
                        }
                        int count = (int)frames * channels;
                        if (isFloat) Marshal.Copy(floats, 0, data, count);
                        else { for (int i = 0; i < count; i++) shorts[i] = (short)(floats[i] * 32767); Marshal.Copy(shorts, 0, data, count); }
                        Audio.Check(render.ReleaseBuffer(frames, 0));
                        Interlocked.Add(ref framesWritten, frames);
                    };

                    fill(bufferFrames);          // pre-roll so both streams begin from frame 0
                    ready.Set();
                    while (!go.WaitOne(50)) if (stop) return;
                    Audio.Check(client.Start());

                    var started = DateTime.UtcNow; bool baseline = false; double deviceStart = 0, systemStart = 0;
                    while (!stop)
                    {
                        wake.WaitOne(200);
                        uint padding; Audio.Check(client.GetCurrentPadding(out padding));
                        if (bufferFrames > padding) fill(bufferFrames - padding);
                        ulong position, qpc;
                        if (clock.GetPosition(out position, out qpc) != 0) continue;
                        double deviceSec = position / (double)clockFrequency, systemSec = qpc * 1e-7;  // qpc is in 100 ns units
                        // Skip the first seconds: Bluetooth endpoints settle after start.
                        if (!baseline) { if (DateTime.UtcNow - started > TimeSpan.FromSeconds(3)) { baseline = true; deviceStart = deviceSec; systemStart = systemSec; } }
                        else lock (gate) driftMs = ClickTrack.DriftMs(deviceStart, deviceSec, systemStart, systemSec);
                    }
                    client.Stop();
                }
            }
            catch (Exception ex) { Fail(ex.Message); }
            finally
            {
                ready.Set();
                Audio.Release(clock); Audio.Release(render); Audio.Release(client); Audio.Release(device); Audio.Release(enumerator);
            }
        }

        public void Dispose() { stop = true; if (thread.IsAlive) thread.Join(2000); ready.Dispose(); }
    }

    /// <summary>Two click streams started together; offset &gt; 0 delays B, &lt; 0 delays A.</summary>
    public sealed class SyncTestSession : IDisposable
    {
        readonly ManualResetEvent go = new ManualResetEvent(false);
        public ClickStream A { get; private set; }
        public ClickStream B { get; private set; }
        public DateTime StartedUtc { get; private set; }

        public SyncTestSession(string deviceA, string deviceB, double offsetMs)
        {
            A = new ClickStream(deviceA, ClickTrack.PitchA, go); B = new ClickStream(deviceB, ClickTrack.PitchB, go);
            SetOffset(offsetMs);
        }
        /// <summary>Blocks until both streams are initialised, then releases them at the same moment.</summary>
        public string Start()
        {
            A.Start(); B.Start();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            foreach (var stream in new[] { A, B })
            {
                int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (!stream.Ready.WaitOne(remaining)) return "裝置初始化逾時。";
            }
            string error = A.Error != null ? "A：" + A.Error : B.Error != null ? "B：" + B.Error : null;
            if (error != null) return error;
            StartedUtc = DateTime.UtcNow; go.Set(); return null;
        }
        public void SetOffset(double offsetMs)
        {
            double a, b; ClickTrack.Delays(offsetMs, out a, out b); A.SetDelayMs(a); B.SetDelayMs(b);
        }
        /// <summary>Change in A-vs-B Windows clock offset since the baseline (null until both have one).</summary>
        public double? RelativeWindowsDriftMs
        {
            get { var a = A.WindowsDriftMs; var b = B.WindowsDriftMs; return a.HasValue && b.HasValue ? a.Value - b.Value : (double?)null; }
        }
        public void Dispose() { A.Dispose(); B.Dispose(); go.Dispose(); }
    }

    public sealed class SyncMark
    {
        public DateTime RecordedUtc { get; set; }
        public double ElapsedSeconds { get; set; }
        public double OffsetMs { get; set; }
        public double? WindowsDriftMs { get; set; }
        public string DeviceA { get; set; }
        public string DeviceB { get; set; }

        public static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundcoreDesk", "sync-test.json"); } }
        public static List<SyncMark> Load()
        {
            try { return new JavaScriptSerializer().Deserialize<List<SyncMark>>(File.ReadAllText(FilePath)) ?? new List<SyncMark>(); }
            catch { return new List<SyncMark>(); }
        }
        public static void Save(IEnumerable<SyncMark> marks)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(marks.Skip(Math.Max(0, marks.Count() - 200)).ToList()), new System.Text.UTF8Encoding(false));
        }
        /// <summary>Speaker-side drift estimate between the first and latest alignment of one session.</summary>
        public static double? OffsetChangeMs(IList<SyncMark> sessionMarks)
        { return sessionMarks.Count < 2 ? (double?)null : sessionMarks[sessionMarks.Count - 1].OffsetMs - sessionMarks[0].OffsetMs; }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
    [ComImport, Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClock
    {
        [PreserveSig] int GetFrequency(out ulong frequency);
        [PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
        [PreserveSig] int GetCharacteristics(out uint characteristics);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

namespace SoundcoreDesk
{
    /// <summary>Format of a shared-mode endpoint, read from its WAVEFORMATEX(TENSIBLE).</summary>
    public sealed class WaveFormatInfo
    {
        static readonly Guid FloatFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        static readonly Guid PcmFormat = new Guid("00000001-0000-0010-8000-00aa00389b71");
        public int Channels { get; set; }
        public int Rate { get; set; }
        public int Bits { get; set; }
        public uint ChannelMask { get; set; }
        public bool IsFloat { get; set; }
        public bool IsPcm16 { get; set; }

        public static WaveFormatInfo Read(IntPtr format)
        {
            int tag = (ushort)Marshal.ReadInt16(format, 0);
            var info = new WaveFormatInfo { Channels = (ushort)Marshal.ReadInt16(format, 2), Rate = Marshal.ReadInt32(format, 4), Bits = (ushort)Marshal.ReadInt16(format, 14) };
            Guid sub = Guid.Empty;
            if (tag == 0xFFFE)
            {
                info.ChannelMask = (uint)Marshal.ReadInt32(format, 20);
                var raw = new byte[16]; Marshal.Copy(format + 24, raw, 0, 16); sub = new Guid(raw);
            }
            if (info.ChannelMask == 0) info.ChannelMask = SurroundMatrix.DefaultMask(info.Channels);
            info.IsFloat = info.Bits == 32 && (tag == 3 || (tag == 0xFFFE && sub == FloatFormat));
            info.IsPcm16 = info.Bits == 16 && (tag == 1 || (tag == 0xFFFE && sub == PcmFormat));
            return info;
        }
        public string Describe() { return Rate + " Hz · " + Channels + " 聲道（" + SurroundMatrix.LayoutName(ChannelMask, Channels) + "）"; }

        /// <summary>Allocates a 32-bit float stereo WAVEFORMATEXTENSIBLE; free with Marshal.FreeCoTaskMem.</summary>
        public static IntPtr AllocFloatStereo(int rate)
        {
            IntPtr p = Marshal.AllocCoTaskMem(40);
            Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE)); Marshal.WriteInt16(p, 2, 2); Marshal.WriteInt32(p, 4, rate);
            Marshal.WriteInt32(p, 8, rate * 8); Marshal.WriteInt16(p, 12, 8); Marshal.WriteInt16(p, 14, 32);
            Marshal.WriteInt16(p, 16, 22); Marshal.WriteInt16(p, 18, 32); Marshal.WriteInt32(p, 20, 3);
            byte[] sub = FloatFormat.ToByteArray(); Marshal.Copy(sub, 0, p + 24, 16);
            return p;
        }
    }

    /// <summary>Maps any Windows speaker layout onto four outputs: front L/R (pair 1) and rear L/R (pair 2).</summary>
    public static class SurroundMatrix
    {
        public const uint FL = 0x1, FR = 0x2, FC = 0x4, LFE = 0x8, BL = 0x10, BR = 0x20, FLC = 0x40, FRC = 0x80, BC = 0x100, SL = 0x200, SR = 0x400;
        public const int FrontLeft = 0, FrontRight = 1, RearLeft = 2, RearRight = 3;
        const float Half = 0.7071f;

        public static uint DefaultMask(int channels)
        {
            switch (channels)
            {
                case 1: return FC;
                case 2: return FL | FR;
                case 3: return FL | FR | FC;
                case 4: return FL | FR | BL | BR;
                case 5: return FL | FR | FC | BL | BR;
                case 6: return FL | FR | FC | LFE | BL | BR;
                case 7: return FL | FR | FC | LFE | BC | SL | SR;
                case 8: return FL | FR | FC | LFE | BL | BR | SL | SR;
                default: return 0;
            }
        }
        /// <summary>Speaker bit carried by each interleaved channel (channels follow the mask's set bits in order).</summary>
        public static uint[] Speakers(uint mask, int channels)
        {
            var result = new uint[channels]; int index = 0;
            for (int bit = 0; bit < 32 && index < channels; bit++) if ((mask & (1u << bit)) != 0) result[index++] = 1u << bit;
            return result;
        }
        public static bool HasRear(uint mask) { return (mask & (BL | BR | SL | SR | BC)) != 0; }
        public static string LayoutName(uint mask, int channels)
        {
            if (mask == (FL | FR) || channels == 2 && mask == 0) return "立體聲";
            if (mask == (FL | FR | FC | LFE | BL | BR | SL | SR)) return "7.1";
            if (mask == (FL | FR | FC | LFE | BL | BR) || mask == (FL | FR | FC | LFE | SL | SR)) return "5.1";
            if (mask == (FL | FR | BL | BR) || mask == (FL | FR | SL | SR)) return "4.0";
            if (mask == FC) return "單聲道";
            return HasRear(mask) ? "含環繞聲道" : "無環繞聲道";
        }
        /// <summary>coefficients[channel, output]; center and LFE fold into the front pair.</summary>
        public static float[,] Build(uint mask, int channels, float lfeLevel)
        {
            var m = new float[channels, 4];
            bool both = (mask & (SL | SR)) != 0 && (mask & (BL | BR)) != 0;
            float rear = both ? Half : 1f;
            var speakers = Speakers(mask, channels);
            for (int c = 0; c < channels; c++)
            {
                switch (speakers[c])
                {
                    case FL: m[c, FrontLeft] = 1; break;
                    case FR: m[c, FrontRight] = 1; break;
                    case FC: m[c, FrontLeft] = Half; m[c, FrontRight] = Half; break;
                    case LFE: m[c, FrontLeft] = lfeLevel; m[c, FrontRight] = lfeLevel; break;
                    case FLC: m[c, FrontLeft] = Half; break;
                    case FRC: m[c, FrontRight] = Half; break;
                    case BL: case SL: m[c, RearLeft] = rear; break;
                    case BR: case SR: m[c, RearRight] = rear; break;
                    case BC: m[c, RearLeft] = Half; m[c, RearRight] = Half; break;
                }
            }
            return m;
        }
    }

    /// <summary>What the rear pair plays while the source has no rear content of its own.</summary>
    public enum RearFill { Off = 0, Copy = 1, Ambience = 2 }

    /// <summary>
    /// Mixes source frames into 4 outputs. When the source carries no rear content (stereo music on a
    /// 5.1 device, or a stereo device) the rear pair can be filled, crossfaded in/out:
    /// <see cref="RearFill.Ambience"/> sends the ambience split from the front (AmbienceExtractor), later
    /// than the front by <see cref="AmbienceDelayMs"/>; <see cref="RearFill.Copy"/> sends 80% of the front.
    /// </summary>
    public sealed class SurroundMixer
    {
        public const double RearSilenceSeconds = 1.0, CrossfadeSeconds = 0.25, TestToneSeconds = 0.9, MaxAmbienceDelayMs = 40;
        public const float CopyLevel = 0.8f;
        readonly float[,] matrix;
        readonly int channels, rate;
        readonly bool sourceHasRear;
        readonly float[] peaks = new float[4];
        int pendingTest = -1, testChannel = -1;
        long testFrame;
        double rearSilentSeconds = RearSilenceSeconds * 2;
        float copyGain;
        public volatile float FrontLevel = 1, RearLevel = 1, Master = 1;
        public volatile RearFill Fill = RearFill.Ambience;
        /// <summary>Rear ambience level, 1 = as extracted.</summary>
        public volatile float AmbienceLevel = 1;
        volatile float ambienceDelayMs = 15;
        /// <summary>How much later than the front the ambience plays (0–40 ms).</summary>
        public double AmbienceDelayMs { get { return ambienceDelayMs; } set { ambienceDelayMs = (float)Math.Max(0, Math.Min(MaxAmbienceDelayMs, value)); } }
        // Ambience path: the extractor needs one FFT frame, so front and real rear wait the same time
        // (dry ring) and the slider delay is exactly "rear ambience vs front".
        RearFill activeFill = (RearFill)(-1);
        AmbienceExtractor extractor;
        float[] dryRing, ambienceRing;
        int dryPosition, ambiencePosition;
        public bool UpmixActive { get; private set; }
        /// <summary>The source itself had rear content within the last second.</summary>
        public bool SourceRearActive { get; private set; }
        public int Rate { get { return rate; } }

        public SurroundMixer(uint mask, int channels, int rate, float lfeLevel)
        {
            this.channels = channels; this.rate = rate; sourceHasRear = SurroundMatrix.HasRear(mask);
            matrix = SurroundMatrix.Build(mask, channels, lfeLevel);
        }
        /// <summary>Plays a short three-beep tone on one output (0 FL, 1 FR, 2 RL, 3 RR).</summary>
        public void RequestTest(int output) { if (output >= 0 && output < 4) Interlocked.Exchange(ref pendingTest, output); }
        public float TakePeak(int output) { lock (peaks) { float v = peaks[output]; peaks[output] = 0; return v; } }

        /// <summary>input == null means silence (gap filling); output receives frames × 4 floats.</summary>
        public void Process(float[] input, int frames, float[] output)
        {
            int requested = Interlocked.Exchange(ref pendingTest, -1);
            if (requested >= 0) { testChannel = requested; testFrame = 0; }
            var fill = Fill;
            if (fill != activeFill) SwitchFill(fill);
            bool upmix = fill != RearFill.Off && (!sourceHasRear || rearSilentSeconds > RearSilenceSeconds);
            float step = (float)(1.0 / (CrossfadeSeconds * rate));
            float front = FrontLevel * Master, back = RearLevel * Master, ambienceLevel = AmbienceLevel;
            int ambienceDelay = (int)Math.Round(ambienceDelayMs / 1000.0 * rate);
            bool rearSeen = false;
            var blockPeaks = new float[4];
            for (int f = 0; f < frames; f++)
            {
                float o0 = 0, o1 = 0, o2 = 0, o3 = 0;
                if (input != null)
                    for (int c = 0, b = f * channels; c < channels; c++)
                    {
                        float x = input[b + c];
                        o0 += x * matrix[c, 0]; o1 += x * matrix[c, 1]; o2 += x * matrix[c, 2]; o3 += x * matrix[c, 3];
                    }
                // Real rear content stops the fill immediately (then it fades out over the crossfade).
                if (!rearSeen && (Math.Abs(o2) > 1e-4f || Math.Abs(o3) > 1e-4f)) { rearSeen = true; upmix = false; }
                copyGain = upmix ? Math.Min(1, copyGain + step) : Math.Max(0, copyGain - step);
                if (fill == RearFill.Copy) { o2 += copyGain * CopyLevel * o0; o3 += copyGain * CopyLevel * o1; }
                else if (fill == RearFill.Ambience)
                {
                    float ambienceL, ambienceR;
                    extractor.Process(o0, o1, out ambienceL, out ambienceR);
                    // Hold the front and the real rear for the extractor's frame, then delay the ambience on top.
                    int d = dryPosition * 4;
                    float t0 = dryRing[d], t1 = dryRing[d + 1], t2 = dryRing[d + 2], t3 = dryRing[d + 3];
                    dryRing[d] = o0; dryRing[d + 1] = o1; dryRing[d + 2] = o2; dryRing[d + 3] = o3;
                    o0 = t0; o1 = t1; o2 = t2; o3 = t3;
                    if (++dryPosition == extractor.Latency) dryPosition = 0;
                    int size = ambienceRing.Length / 2, write = ambiencePosition * 2, read = ((ambiencePosition - ambienceDelay + size) % size) * 2;
                    ambienceRing[write] = ambienceL; ambienceRing[write + 1] = ambienceR;
                    ambienceL = ambienceRing[read]; ambienceR = ambienceRing[read + 1];
                    if (++ambiencePosition == size) ambiencePosition = 0;
                    o2 += copyGain * ambienceLevel * ambienceL; o3 += copyGain * ambienceLevel * ambienceR;
                }
                o0 *= front; o1 *= front; o2 *= back; o3 *= back;
                if (testChannel >= 0)
                {
                    double t = testFrame++ / (double)rate;
                    if (t >= TestToneSeconds) testChannel = -1;
                    else if (t % 0.3 < 0.15)
                    {
                        float tone = (float)(0.3 * Math.Sin(2 * Math.PI * 1000 * t));
                        if (testChannel == 0) o0 += tone; else if (testChannel == 1) o1 += tone; else if (testChannel == 2) o2 += tone; else o3 += tone;
                    }
                }
                o0 = Clamp(o0); o1 = Clamp(o1); o2 = Clamp(o2); o3 = Clamp(o3);
                int o = f * 4; output[o] = o0; output[o + 1] = o1; output[o + 2] = o2; output[o + 3] = o3;
                blockPeaks[0] = Math.Max(blockPeaks[0], Math.Abs(o0)); blockPeaks[1] = Math.Max(blockPeaks[1], Math.Abs(o1));
                blockPeaks[2] = Math.Max(blockPeaks[2], Math.Abs(o2)); blockPeaks[3] = Math.Max(blockPeaks[3], Math.Abs(o3));
            }
            rearSilentSeconds = rearSeen ? 0 : rearSilentSeconds + frames / (double)rate;
            UpmixActive = copyGain > 0.5f;
            SourceRearActive = sourceHasRear && rearSilentSeconds <= RearSilenceSeconds;
            lock (peaks) for (int i = 0; i < 4; i++) peaks[i] = Math.Max(peaks[i], blockPeaks[i]);
        }
        /// <summary>Starts every mode from a clean state, so no stale audio from an earlier switch replays.</summary>
        void SwitchFill(RearFill fill)
        {
            activeFill = fill;
            if (fill == RearFill.Ambience)
            {
                extractor = new AmbienceExtractor(rate);
                dryRing = new float[extractor.Latency * 4]; dryPosition = 0;
                ambienceRing = new float[((int)(MaxAmbienceDelayMs / 1000 * rate) + 1) * 2]; ambiencePosition = 0;
            }
            else { extractor = null; dryRing = null; ambienceRing = null; }
        }
        static float Clamp(float x) { return x > 1 ? 1 : x < -1 ? -1 : x; }
    }

    /// <summary>
    /// Ring of mixed 4-channel frames on one timeline. One writer (capture) and several readers (outputs).
    /// Readers anchor to the writer's smoothed position so every output stays aligned to the same moment.
    /// </summary>
    public sealed class SurroundTimeline
    {
        readonly float[] ring;
        readonly int ringFrames;
        readonly long startTicks = Stopwatch.GetTimestamp();
        long written, lagBits;
        bool lagKnown;
        public int Rate { get; private set; }

        public SurroundTimeline(int rate, double seconds)
        {
            Rate = rate; ringFrames = (int)(rate * seconds); ring = new float[ringFrames * 4];
        }
        public long Written { get { return Interlocked.Read(ref written); } }
        public long Now { get { return (Stopwatch.GetTimestamp() - startTicks) * Rate / Stopwatch.Frequency; } }
        /// <summary>Smoothed writer position on the timeline: Now minus the typical capture lag.</summary>
        public long Anchor { get { return Now - (long)BitConverter.Int64BitsToDouble(Interlocked.Read(ref lagBits)); } }

        public void Write(float[] frames4, int count)
        {
            long start = Written;
            for (int i = 0; i < count; i++)
            {
                int at = (int)((start + i) % ringFrames) * 4, from = i * 4;
                ring[at] = frames4[from]; ring[at + 1] = frames4[from + 1]; ring[at + 2] = frames4[from + 2]; ring[at + 3] = frames4[from + 3];
            }
            Interlocked.Add(ref written, count);
            double sample = Now - (start + count);
            double lag = BitConverter.Int64BitsToDouble(Interlocked.Read(ref lagBits));
            lag = lagKnown ? lag + (sample - lag) * 0.05 : sample; lagKnown = true;
            Interlocked.Exchange(ref lagBits, BitConverter.DoubleToInt64Bits(lag));
        }
        /// <summary>Copies one stereo pair (pair 0 front, 1 rear) starting at a timeline position; missing data is silence.</summary>
        public int Read(long position, int count, int pair, float[] stereo)
        {
            // Readers anchor slightly in the past, so right after start positions can be negative.
            long end = Written, oldest = Math.Max(0, end - ringFrames + 1); int missing = 0;
            for (int i = 0; i < count; i++)
            {
                long p = position + i;
                if (p < oldest || p >= end) { stereo[i * 2] = 0; stereo[i * 2 + 1] = 0; missing++; continue; }
                int at = (int)(p % ringFrames) * 4 + pair * 2;
                stereo[i * 2] = ring[at]; stereo[i * 2 + 1] = ring[at + 1];
            }
            return missing;
        }
    }

    /// <summary>Loopback capture of the source endpoint, mixed and written to the timeline; gaps become silence.</summary>
    sealed class LoopbackSource
    {
        public WaveFormatInfo Format { get; private set; }
        public SurroundMixer Mixer { get; private set; }
        public SurroundTimeline Timeline { get; private set; }
        public long PaddedFrames;
        readonly string deviceId;
        IMMDeviceEnumerator enumerator; IMMDevice device; IAudioClient client; IAudioCaptureClient capture;

        public LoopbackSource(string deviceId) { this.deviceId = deviceId; }

        /// <summary>Opens the endpoint and builds the mixer; runs on the capture thread.</summary>
        public void Open(float lfeLevel, float master)
        {
            enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
            Audio.Check(enumerator.GetDevice(deviceId, out device));
            object instance; Guid iid = typeof(IAudioClient).GUID;
            Audio.Check(device.Activate(ref iid, 23, IntPtr.Zero, out instance)); client = (IAudioClient)instance;
            IntPtr mix; Audio.Check(client.GetMixFormat(out mix));
            try
            {
                Format = WaveFormatInfo.Read(mix);
                if (!Format.IsFloat && !Format.IsPcm16) throw new NotSupportedException("來源格式不支援（" + Format.Bits + " bit）。");
                Audio.Check(client.Initialize(0, SurroundEngine.Loopback, 2000000, 0, mix, IntPtr.Zero));
            }
            finally { Marshal.FreeCoTaskMem(mix); }
            iid = typeof(IAudioCaptureClient).GUID; Audio.Check(client.GetService(ref iid, out instance)); capture = (IAudioCaptureClient)instance;
            Mixer = new SurroundMixer(Format.ChannelMask, Format.Channels, Format.Rate, lfeLevel) { Master = master };
            Timeline = new SurroundTimeline(Format.Rate, 4);
        }

        public void Run(Func<bool> stopping)
        {
            int channels = Format.Channels;
            float[] input = new float[0], mixed = new float[0]; short[] pcm = new short[0];
            Audio.Check(client.Start());
            try
            {
                while (!stopping())
                {
                    Thread.Sleep(5);
                    bool received = false; uint packet;
                    Audio.Check(capture.GetNextPacketSize(out packet));
                    while (packet > 0)
                    {
                        IntPtr data; uint frames, flags; ulong devicePosition, qpc;
                        Audio.Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpc));
                        int count = (int)frames * channels;
                        if (input.Length < count) input = new float[count];
                        if (mixed.Length < frames * 4) mixed = new float[frames * 4];
                        bool silent = (flags & 0x2) != 0;
                        if (!silent && Format.IsFloat) Marshal.Copy(data, input, 0, count);
                        else if (!silent) { if (pcm.Length < count) pcm = new short[count]; Marshal.Copy(data, pcm, 0, count); for (int i = 0; i < count; i++) input[i] = pcm[i] / 32768f; }
                        Mixer.Process(silent ? null : input, (int)frames, mixed);
                        Timeline.Write(mixed, (int)frames);
                        Audio.Check(capture.ReleaseBuffer(frames));
                        received = true;
                        Audio.Check(capture.GetNextPacketSize(out packet));
                    }
                    // Loopback delivers nothing while the source is idle: keep the timeline moving with silence.
                    long behind = Timeline.Now - Timeline.Written;
                    if (!received && behind > Format.Rate * 3 / 100)
                    {
                        int pad = (int)Math.Min(behind - Format.Rate / 100, Format.Rate);
                        if (mixed.Length < pad * 4) mixed = new float[pad * 4];
                        Mixer.Process(null, pad, mixed); Timeline.Write(mixed, pad);
                        Interlocked.Add(ref PaddedFrames, pad);
                    }
                }
            }
            finally { client.Stop(); }
        }
        public void Release() { Audio.Release(capture); Audio.Release(client); Audio.Release(device); Audio.Release(enumerator); }
    }

    /// <summary>Renders one stereo pair of the timeline to one endpoint (a TWS primary or a single speaker).</summary>
    sealed class PairOutput
    {
        public const double LatencySeconds = 0.06, SnapSeconds = 0.04;
        readonly string deviceId; readonly int pair;
        long delayMicros;
        public long Snaps, MissingFrames;
        public string Format { get; private set; }

        public PairOutput(string deviceId, int pair) { this.deviceId = deviceId; this.pair = pair; }
        public void SetDelayMs(double ms) { Interlocked.Exchange(ref delayMicros, (long)Math.Round(Math.Max(0, ms) * 1000)); }

        public void Run(SurroundTimeline timeline, Func<bool> stopping, Action ready)
        {
            IMMDeviceEnumerator enumerator = null; IMMDevice device = null; IAudioClient client = null; IAudioRenderClient render = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
                Audio.Check(enumerator.GetDevice(deviceId, out device));
                object instance; Guid iid = typeof(IAudioClient).GUID;
                Audio.Check(device.Activate(ref iid, 23, IntPtr.Zero, out instance)); client = (IAudioClient)instance;
                IntPtr mix; Audio.Check(client.GetMixFormat(out mix));
                try { Format = WaveFormatInfo.Read(mix).Describe(); } finally { Marshal.FreeCoTaskMem(mix); }
                // Render at the source rate; Windows converts to the speaker's own rate (e.g. 44.1 kHz).
                IntPtr format = WaveFormatInfo.AllocFloatStereo(timeline.Rate);
                try { Audio.Check(client.Initialize(0, SurroundEngine.EventDriven | SurroundEngine.AutoConvert | SurroundEngine.DefaultQualitySrc, 500000, 0, format, IntPtr.Zero)); }
                finally { Marshal.FreeCoTaskMem(format); }
                uint bufferFrames; Audio.Check(client.GetBufferSize(out bufferFrames));
                using (var wake = new AutoResetEvent(false))
                {
                    Audio.Check(client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle()));
                    iid = typeof(IAudioRenderClient).GUID; Audio.Check(client.GetService(ref iid, out instance)); render = (IAudioRenderClient)instance;
                    var stereo = new float[bufferFrames * 2];
                    long cursor = 0; bool anchored = false;
                    long latency = (long)(timeline.Rate * LatencySeconds), snap = (long)(timeline.Rate * SnapSeconds);
                    Action<uint> fill = frames =>
                    {
                        long target = timeline.Anchor - latency - (long)(Interlocked.Read(ref delayMicros) / 1e6 * timeline.Rate);
                        if (!anchored || Math.Abs(cursor - target) > snap) { if (anchored) Interlocked.Increment(ref Snaps); cursor = target; anchored = true; }
                        Interlocked.Add(ref MissingFrames, timeline.Read(cursor, (int)frames, pair, stereo));
                        cursor += frames;
                        IntPtr data; Audio.Check(render.GetBuffer(frames, out data));
                        Marshal.Copy(stereo, 0, data, (int)frames * 2);
                        Audio.Check(render.ReleaseBuffer(frames, 0));
                    };
                    fill(bufferFrames);
                    Audio.Check(client.Start());
                    ready();
                    while (!stopping())
                    {
                        wake.WaitOne(200);
                        uint padding; Audio.Check(client.GetCurrentPadding(out padding));
                        if (bufferFrames > padding) fill(bufferFrames - padding);
                    }
                    client.Stop();
                }
            }
            finally { Audio.Release(render); Audio.Release(client); Audio.Release(device); Audio.Release(enumerator); }
        }
    }

    /// <summary>Source loopback → 4-channel mix → front pair + rear pair, each on its own thread.</summary>
    public sealed class SurroundEngine : IDisposable
    {
        internal const uint Loopback = 0x00020000, EventDriven = 0x00040000, AutoConvert = 0x80000000, DefaultQualitySrc = 0x08000000;
        readonly LoopbackSource source;
        readonly PairOutput front, rear;
        readonly List<Thread> threads = new List<Thread>();
        readonly object gate = new object();
        volatile bool stop;
        string error;
        double offsetMs;

        public SurroundEngine(string sourceId, string frontId, string rearId)
        {
            source = new LoopbackSource(sourceId); front = new PairOutput(frontId, 0); rear = new PairOutput(rearId, 1);
        }
        public string Error { get { lock (gate) return error; } }
        public WaveFormatInfo SourceFormat { get { return source.Format; } }
        public SurroundMixer Mixer { get { return source.Mixer; } }
        public string FrontFormat { get { return front.Format; } }
        public string RearFormat { get { return rear.Format; } }
        public long Snaps { get { return Interlocked.Read(ref front.Snaps) + Interlocked.Read(ref rear.Snaps); } }
        public long PaddedFrames { get { return Interlocked.Read(ref source.PaddedFrames); } }
        public long FrontMissingFrames { get { return Interlocked.Read(ref front.MissingFrames); } }
        public long RearMissingFrames { get { return Interlocked.Read(ref rear.MissingFrames); } }
        public DateTime StartedUtc { get; private set; }

        void Fail(string where, Exception ex) { lock (gate) if (error == null) error = where + "：" + ex.Message; stop = true; }

        /// <summary>Positive offset delays the rear pair, negative delays the front pair.</summary>
        public void SetOffset(double ms)
        {
            offsetMs = ms; double f, r; ClickTrack.Delays(ms, out f, out r); front.SetDelayMs(f); rear.SetDelayMs(r);
        }

        /// <summary>Opens the source and both outputs; returns an error message or null.</summary>
        public string Start(float lfeLevel, float master = 1f)
        {
            var opened = new ManualResetEvent(false);
            var capture = new Thread(() =>
            {
                try { source.Open(lfeLevel, master); opened.Set(); source.Run(() => stop); }
                catch (Exception ex) { Fail("來源", ex); }
                finally { opened.Set(); source.Release(); }
            }) { IsBackground = true, Name = "SoundcoreDesk surround capture" };
            capture.SetApartmentState(ApartmentState.MTA); threads.Add(capture); capture.Start();
            if (!opened.WaitOne(8000)) { stop = true; return "來源裝置開啟逾時。"; }
            if (Error != null) return Error;
            Thread.Sleep(120);   // let the capture lag estimate settle before outputs anchor to it
            SetOffset(offsetMs);
            var readyCount = new CountdownEvent(2);
            foreach (var output in new[] { Tuple.Create(front, "前方"), Tuple.Create(rear, "後方") })
            {
                var o = output;
                var t = new Thread(() =>
                {
                    bool signaled = false;
                    Action signal = () => { if (!signaled) { signaled = true; readyCount.Signal(); } };
                    try { o.Item1.Run(source.Timeline, () => stop, signal); }
                    catch (Exception ex) { Fail(o.Item2, ex); }
                    finally { signal(); }   // unblock Start() if this output failed before becoming ready
                }) { IsBackground = true, Name = "SoundcoreDesk surround " + o.Item2 };
                t.SetApartmentState(ApartmentState.MTA); threads.Add(t); t.Start();
            }
            if (!readyCount.Wait(8000)) { stop = true; return "喇叭開啟逾時。"; }
            if (Error != null) return Error;
            StartedUtc = DateTime.UtcNow;
            return null;
        }

        /// <summary>Opens a render endpoint just long enough to read its shared-mode format.</summary>
        public static WaveFormatInfo Probe(string deviceId)
        {
            IMMDeviceEnumerator enumerator = null; IMMDevice device = null; IAudioClient client = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new DeviceEnumerator();
                Audio.Check(enumerator.GetDevice(deviceId, out device));
                object instance; Guid iid = typeof(IAudioClient).GUID;
                Audio.Check(device.Activate(ref iid, 23, IntPtr.Zero, out instance)); client = (IAudioClient)instance;
                IntPtr mix; Audio.Check(client.GetMixFormat(out mix));
                try { return WaveFormatInfo.Read(mix); } finally { Marshal.FreeCoTaskMem(mix); }
            }
            finally { Audio.Release(client); Audio.Release(device); Audio.Release(enumerator); }
        }

        public void Dispose() { stop = true; foreach (var t in threads) if (t.IsAlive) t.Join(2000); }
    }

    public sealed class SurroundSettings
    {
        public string SourceId { get; set; }
        public string FrontId { get; set; }
        public string RearId { get; set; }
        public double OffsetMs { get; set; }
        public double FrontLevel { get; set; }
        public double RearLevel { get; set; }
        /// <summary>Older versions' on/off switch; still written so they keep working with this file.</summary>
        public bool StereoToRear { get; set; }
        /// <summary>RearFill as a number; -1 = not chosen yet (settings from before v1.12, decided by StereoToRear).</summary>
        public int RearMode { get; set; }
        public double AmbienceDelayMs { get; set; }
        public double AmbienceLevel { get; set; }

        public SurroundSettings() { FrontLevel = 1; RearLevel = 1; StereoToRear = true; RearMode = -1; AmbienceDelayMs = 15; AmbienceLevel = 1; }
        public RearFill EffectiveFill() { return RearMode >= 0 && RearMode <= 2 ? (RearFill)RearMode : StereoToRear ? RearFill.Ambience : RearFill.Off; }
        static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundcoreDesk", "surround.json"); } }
        public static SurroundSettings Load()
        {
            try { return new JavaScriptSerializer().Deserialize<SurroundSettings>(File.ReadAllText(FilePath)) ?? new SurroundSettings(); }
            catch { return new SurroundSettings(); }
        }
        public void Save()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

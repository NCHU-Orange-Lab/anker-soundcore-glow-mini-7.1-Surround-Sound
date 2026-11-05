using System;
using System.Collections.Generic;
using System.Linq;
using SoundcoreDesk;

static class SyncTestTests
{
    static int checks;
    static void Assert(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++; Console.WriteLine("PASS: " + label);
    }
    public static int Run()
    {
        const int rate = 48000;
        long period = (long)Math.Round(rate * ClickTrack.IntervalSeconds);
        Assert(ClickTrack.Sample(-1, rate, ClickTrack.PitchA) == 0 && ClickTrack.Sample(-period, rate, ClickTrack.PitchA) == 0, "delayed stream is silent before its first click");
        float peak = Enumerable.Range(0, rate * 4 / 1000).Max(i => Math.Abs(ClickTrack.Sample(i, rate, ClickTrack.PitchA)));
        Assert(peak > 0.3f && peak <= 0.8f, "click starts on the beat with bounded amplitude");
        Assert(ClickTrack.Sample(period / 2, rate, ClickTrack.PitchA) == 0 && ClickTrack.Sample(rate * 5 / 1000, rate, ClickTrack.PitchA) == 0, "silence between clicks keeps alignment audible");
        float accent = Enumerable.Range(0, 192).Max(i => Math.Abs(ClickTrack.Sample(i, rate, ClickTrack.PitchB)));
        float normal = Enumerable.Range(0, 192).Max(i => Math.Abs(ClickTrack.Sample(period + i, rate, ClickTrack.PitchB)));
        Assert(accent > normal * 1.5f, "first beat of each bar is accented to catch whole-beat offsets");
        Assert(Enumerable.Range(0, (int)period * 8).All(i => Math.Abs(ClickTrack.Sample(i, rate, ClickTrack.PitchA)) <= 1f), "click track never clips");

        double a, b;
        ClickTrack.Delays(30, out a, out b); Assert(a == 0 && b == 30, "positive offset delays B only");
        ClickTrack.Delays(-12, out a, out b); Assert(a == 12 && b == 0, "negative offset delays A only");
        ClickTrack.Delays(0, out a, out b); Assert(a == 0 && b == 0, "zero offset delays neither");
        Assert(ClickTrack.DelayFrames(30, rate) == 1440 && ClickTrack.DelayFrames(1, 44100) == 44, "delay converts to whole frames at the device rate");
        // A stream renders Sample(frame - delayFrames): with 30 ms delay its click lands 1440 frames later.
        long delay = ClickTrack.DelayFrames(30, rate);
        Assert(ClickTrack.Sample(10 - delay, rate, ClickTrack.PitchA) == 0 && Math.Abs(ClickTrack.Sample(1450 - delay, rate, ClickTrack.PitchA) - ClickTrack.Sample(10, rate, ClickTrack.PitchA)) < 1e-6, "a 30 ms delay shifts the click by exactly 1440 frames");

        Assert(Math.Abs(ClickTrack.DriftMs(10, 70.003, 5, 65) - 3) < 1e-6, "device clock running 3 ms ahead over a minute reads +3 ms");
        Assert(Math.Abs(ClickTrack.DriftMs(0, 59.998, 0, 60) + 2) < 1e-6, "device clock running behind reads negative drift");

        var marks = new List<SyncMark> { new SyncMark { ElapsedSeconds = 10, OffsetMs = 18 } };
        Assert(SyncMark.OffsetChangeMs(marks) == null, "one alignment is a starting point, not a drift result");
        marks.Add(new SyncMark { ElapsedSeconds = 610, OffsetMs = 25 });
        Assert(SyncMark.OffsetChangeMs(marks) == 7, "drift is the change between first and latest alignment");
        return checks;
    }
}

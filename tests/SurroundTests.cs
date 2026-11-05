using System;
using System.Linq;
using SoundcoreDesk;

static class SurroundTests
{
    static int checks;
    static void Assert(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++; Console.WriteLine("PASS: " + label);
    }
    static bool Near(float a, float b) { return Math.Abs(a - b) < 1e-3f; }

    // Interleaved source frames with a constant value per channel.
    static float[] Frames(int frames, params float[] perChannel)
    {
        var data = new float[frames * perChannel.Length];
        for (int f = 0; f < frames; f++) for (int c = 0; c < perChannel.Length; c++) data[f * perChannel.Length + c] = perChannel[c];
        return data;
    }

    public static int Run()
    {
        const uint m51 = 0x3F, m51side = 0x60F, m71 = 0x63F, stereo = 0x3;
        Assert(SurroundMatrix.DefaultMask(6) == m51 && SurroundMatrix.DefaultMask(8) == m71 && SurroundMatrix.DefaultMask(2) == stereo, "default layouts follow Windows channel masks");
        var speakers = SurroundMatrix.Speakers(m51side, 6);
        Assert(speakers.SequenceEqual(new uint[] { SurroundMatrix.FL, SurroundMatrix.FR, SurroundMatrix.FC, SurroundMatrix.LFE, SurroundMatrix.SL, SurroundMatrix.SR }), "interleaved channels follow the mask's bit order");
        Assert(!SurroundMatrix.HasRear(stereo) && SurroundMatrix.HasRear(m51) && SurroundMatrix.HasRear(m51side), "rear detection covers back and side layouts");
        Assert(SurroundMatrix.LayoutName(m71, 8) == "7.1" && SurroundMatrix.LayoutName(m51side, 6) == "5.1" && SurroundMatrix.LayoutName(stereo, 2) == "立體聲", "layout names for the source hint");

        var m = SurroundMatrix.Build(m51, 6, 0.5f);
        Assert(m[0, 0] == 1 && m[0, 1] == 0 && m[1, 1] == 1 && m[1, 0] == 0, "front left/right go to the front pair only");
        Assert(Near(m[2, 0], 0.7071f) && Near(m[2, 1], 0.7071f) && m[2, 2] == 0 && m[2, 3] == 0, "center folds equally into front left and right");
        Assert(m[3, 0] == 0.5f && m[3, 1] == 0.5f && m[3, 2] == 0, "LFE folds into the front pair at the chosen level");
        Assert(m[4, 2] == 1 && m[4, 0] == 0 && m[5, 3] == 1 && m[5, 1] == 0, "5.1 back channels go to the rear pair only");
        var m7 = SurroundMatrix.Build(m71, 8, 0.5f);
        Assert(Near(m7[4, 2], 0.7071f) && Near(m7[6, 2], 0.7071f) && Near(m7[5, 3], 0.7071f) && Near(m7[7, 3], 0.7071f), "7.1 back and side share the rear pair without doubling level");

        const int rate = 48000;
        var output = new float[rate * 4];
        // 5.1 movie frame: distinct values per channel, rear content present.
        var mixer = new SurroundMixer(m51, 6, rate, 0.5f);
        mixer.Process(Frames(rate / 2, 0.1f, 0.2f, 0, 0, 0.3f, 0.4f), rate / 2, output);
        int last = (rate / 2 - 1) * 4;
        Assert(Near(output[last], 0.1f) && Near(output[last + 1], 0.2f) && Near(output[last + 2], 0.3f) && Near(output[last + 3], 0.4f), "surround content keeps each channel in its own speaker");

        // Stereo music on a 5.1 device: rear channels silent → rear receives a crossfaded copy of the front.
        var music = new SurroundMixer(m51, 6, rate, 0.5f) { Fill = RearFill.Copy };
        music.Process(Frames(rate, 0.5f, -0.5f, 0, 0, 0, 0), rate, output);
        last = (rate - 1) * 4;
        Assert(Near(output[last + 2], 0.4f) && Near(output[last + 3], -0.4f) && music.UpmixActive, "stereo content is copied to the rear at 80%");
        music.Process(Frames(rate, 0.5f, -0.5f, 0, 0, 0.25f, 0.25f), rate, output);
        Assert(Near(output[last + 2], 0.25f) && Near(output[last + 3], 0.25f) && !music.UpmixActive && music.SourceRearActive, "real rear content switches the copy off");
        var off = new SurroundMixer(stereo, 2, rate, 0.5f) { Fill = RearFill.Off };
        off.Process(Frames(rate, 0.5f, 0.5f), rate, output);
        Assert(output[last + 2] == 0 && output[last + 3] == 0, "with filling off a stereo source leaves the rear silent");

        var levels = new SurroundMixer(m51, 6, rate, 0.5f) { FrontLevel = 0.5f, RearLevel = 1.5f, Fill = RearFill.Off };
        levels.Process(Frames(100, 0.4f, 0.4f, 0, 0, 0.4f, 0.4f), 100, output);
        Assert(Near(output[99 * 4], 0.2f) && Near(output[99 * 4 + 2], 0.6f), "front and rear levels scale their own pair");
        var loud = new SurroundMixer(m51, 6, rate, 0.5f) { Fill = RearFill.Off };
        loud.Process(Frames(100, 1f, 1f, 1f, 1f, 1f, 1f), 100, output);
        Assert(output.Take(400).All(v => v <= 1f && v >= -1f), "summed channels are clamped instead of wrapping");

        var tone = new SurroundMixer(m51, 6, rate, 0.5f);
        tone.RequestTest(SurroundMatrix.RearRight);
        tone.Process(null, rate / 10, output);
        float peakRR = 0, others = 0;
        for (int f = 0; f < rate / 10; f++) { peakRR = Math.Max(peakRR, Math.Abs(output[f * 4 + 3])); others = Math.Max(others, Math.Max(Math.Abs(output[f * 4]), Math.Max(Math.Abs(output[f * 4 + 1]), Math.Abs(output[f * 4 + 2])))); }
        Assert(peakRR > 0.25f && others == 0, "channel test beeps only the requested speaker, even with no source audio");
        tone.Process(null, rate, output);
        Assert(Math.Abs(output[(rate - 1) * 4 + 3]) == 0, "channel test stops by itself");

        var timeline = new SurroundTimeline(rate, 1);
        var block = new float[400];
        for (int f = 0; f < 100; f++) { block[f * 4] = f; block[f * 4 + 1] = -f; block[f * 4 + 2] = 1000 + f; block[f * 4 + 3] = -1000 - f; }
        timeline.Write(block, 100);
        var stereoOut = new float[20];
        int missing = timeline.Read(95, 10, 1, stereoOut);
        Assert(missing == 5 && stereoOut[0] == 1095 && stereoOut[1] == -1095 && stereoOut[10] == 0, "rear pair reads its own channels; unwritten frames are silence");
        missing = timeline.Read(-3, 4, 0, stereoOut);
        Assert(missing == 3 && stereoOut[6] == 0 && stereoOut[7] == 0, "positions before the stream start are silence");
        for (int i = 0; i < 12; i++) timeline.Write(new float[rate / 10 * 4], rate / 10);
        missing = timeline.Read(0, 10, 0, stereoOut);
        Assert(missing == 10, "frames older than the ring are never replayed as stale audio");
        Assert(Math.Abs(timeline.Anchor - timeline.Now) < rate, "anchor tracks the writer on the shared timeline");
        Ambience(rate);
        return checks;
    }

    // Low-frequency noise (one-pole lowpass, mostly below ~1 kHz) so the 6–9 kHz rear lowpass barely matters.
    static float[] Noise(Random random, int frames, float gain)
    {
        var data = new float[frames]; float y = 0;
        for (int i = 0; i < frames; i++) { y = 0.9f * y + 0.1f * (float)(random.NextDouble() * 2 - 1); data[i] = gain * y; }
        return data;
    }
    static float[] Stereo(float[] left, float[] right)
    {
        var data = new float[left.Length * 2];
        for (int i = 0; i < left.Length; i++) { data[i * 2] = left[i]; data[i * 2 + 1] = right[i]; }
        return data;
    }
    static double Energy(float[] output, int channel, int from, int to)
    { double sum = 0; for (int f = from; f < to; f++) sum += output[f * 4 + channel] * (double)output[f * 4 + channel]; return sum; }
    /// <summary>Normalised correlation of output channel a at frame f with channel b at frame f − lag.</summary>
    static double Correlation(float[] output, int a, int b, int lag, int from, int to)
    {
        double ab = 0, aa = 0, bb = 0;
        for (int f = from; f < to; f++) { double x = output[f * 4 + a], y = output[(f - lag) * 4 + b]; ab += x * y; aa += x * x; bb += y * y; }
        return ab / Math.Sqrt(aa * bb + 1e-30);
    }
    /// <summary>Amplitude of a sine at hz in one output channel over [from, to), which must hold whole cycles.</summary>
    static double Tone(float[] output, int channel, double hz, int rate, int from, int to)
    {
        double re = 0, im = 0;
        for (int f = from; f < to; f++) { double p = 2 * Math.PI * hz * f / rate; re += output[f * 4 + channel] * Math.Cos(p); im += output[f * 4 + channel] * Math.Sin(p); }
        return 2 * Math.Sqrt(re * re + im * im) / (to - from);
    }

    static void Ambience(int rate)
    {
        const uint stereo = 0x3, m51 = 0x3F;
        int length = rate * 2, latency = new AmbienceExtractor(rate).Latency;
        var random = new Random(7);
        var left = Noise(random, length, 1); var right = Noise(random, length, 1);
        var output = new float[length * 4];
        Assert(latency == 512 && Math.Abs(latency * 1000.0 / rate - 10.67) < 0.01, "ambience analysis needs one 512-sample frame (about 11 ms at 48 kHz)");
        Assert(AmbienceExtractor.LowpassGain(1000) == 1 && AmbienceExtractor.LowpassGain(12000) == 0 && AmbienceExtractor.LowpassGain(7500) > 0 && AmbienceExtractor.LowpassGain(7500) < 1, "rear ambience fades out between 6 and 9 kHz");

        // Left and right completely different (pure room sound): most of it reaches the rear.
        var room = new SurroundMixer(stereo, 2, rate, 0.5f) { Fill = RearFill.Ambience };
        room.Process(Stereo(left, right), length, output);
        double ratio = Energy(output, 2, rate, length) / Energy(output, 0, rate, length);
        Assert(ratio > 0.3 && ratio < 1.1 && room.UpmixActive, "left/right content that differs is ambience and reaches the rear (" + ratio.ToString("0.00") + " of the front's energy)");
        bool exact = true;
        for (int f = rate; f < rate + 2000; f++) if (Math.Abs(output[f * 4] - left[f - latency]) > 1e-6f) exact = false;
        Assert(exact, "the front is the source unchanged, held back by exactly the analysis frame");
        int delay = (int)Math.Round(0.015 * rate);
        double atDelay = Correlation(output, 2, 0, delay, rate, length), atZero = Correlation(output, 2, 0, 0, rate, length);
        Assert(atDelay > 0.6 && atZero < 0.2, "rear ambience plays 15 ms after the same sound in front (" + atDelay.ToString("0.00") + " vs " + atZero.ToString("0.00") + " at 0 ms)");
        Assert(Correlation(output, 2, 3, 0, rate, length) < 0.3, "rear left and right stay as different as the room sound was");
        var together = new SurroundMixer(stereo, 2, rate, 0.5f) { Fill = RearFill.Ambience, AmbienceDelayMs = 0 };
        together.Process(Stereo(left, right), length, output);
        Assert(Correlation(output, 2, 0, 0, rate, length) > 0.6, "with 0 ms the ambience plays together with the front");

        // The same signal on both sides (a centred vocal) or on one side only (a hard-panned guitar) is direct sound.
        var centred = new SurroundMixer(stereo, 2, rate, 0.5f) { Fill = RearFill.Ambience };
        centred.Process(Stereo(left, left), length, output);
        Assert(Energy(output, 2, rate, length) / Energy(output, 0, rate, length) < 0.001, "a sound centred between left and right stays in front");
        var panned = new SurroundMixer(stereo, 2, rate, 0.5f) { Fill = RearFill.Ambience };
        panned.Process(Stereo(left, new float[length]), length, output);
        Assert(Energy(output, 2, rate, length) / Energy(output, 0, rate, length) < 0.001, "a sound panned to one side stays in front");

        // A centred 500 Hz "vocal" over room noise: the rear gets the room, not the vocal.
        var roomLeft = Noise(random, length, 1); var roomRight = Noise(random, length, 1);
        var mixL = new float[length]; var mixR = new float[length];
        for (int f = 0; f < length; f++) { float v = (float)(0.5 * Math.Sin(2 * Math.PI * 500 * f / rate)); mixL[f] = v + roomLeft[f]; mixR[f] = v + roomRight[f]; }
        var vocal = new SurroundMixer(stereo, 2, rate, 0.5f) { Fill = RearFill.Ambience };
        vocal.Process(Stereo(mixL, mixR), length, output);
        double frontVocal = Tone(output, 0, 500, rate, rate, length), rearVocal = Tone(output, 2, 500, rate, rate, length);
        double roomEnergy = 0; for (int f = rate; f < length; f++) roomEnergy += roomLeft[f - latency] * (double)roomLeft[f - latency];
        Assert(rearVocal < frontVocal * 0.1, "a centred vocal reaches the rear at least 20 dB quieter (" + (20 * Math.Log10(rearVocal / frontVocal)).ToString("0") + " dB)");
        Assert(Energy(output, 2, rate, length) > roomEnergy * 0.3, "while the room sound around it still reaches the rear");

        // Real rear channels take over at once; silence stays silent.
        var movie = new SurroundMixer(m51, 6, rate, 0.5f) { Fill = RearFill.Ambience };
        var frames = new float[length * 6];
        for (int f = 0; f < length; f++) { frames[f * 6] = left[f]; frames[f * 6 + 1] = right[f]; if (f >= rate) { frames[f * 6 + 4] = 0.25f; frames[f * 6 + 5] = 0.25f; } }
        movie.Process(frames, length, output);
        int lastFrame = (length - 1) * 4;
        Assert(Near(output[lastFrame + 2], 0.25f) && Near(output[lastFrame + 3], 0.25f) && movie.SourceRearActive && !movie.UpmixActive, "real rear channels switch the ambience off");
        var quiet = new SurroundMixer(stereo, 2, rate, 0.5f) { Fill = RearFill.Ambience };
        quiet.Process(null, rate, output);
        bool silent = true; for (int i = 0; i < rate * 4; i++) if (output[i] != 0) silent = false;
        Assert(silent, "silence in gives silence out (no noise or NaN from the analysis)");

        Assert(new SurroundSettings().EffectiveFill() == RearFill.Ambience && new SurroundSettings { StereoToRear = false }.EffectiveFill() == RearFill.Off && new SurroundSettings { RearMode = 1 }.EffectiveFill() == RearFill.Copy, "older settings map: copy on → ambience, copy off → off; a saved choice wins");
    }
}

using System;

namespace SoundcoreDesk
{
    /// <summary>
    /// Splits a stereo stream into its ambience for the rear pair: the part that differs between left and
    /// right (reverb, room sound, wide pads). The part both channels share — vocals, lead, drums, anything
    /// panned to one position — gets no rear share, so it stays in front.
    ///
    /// Short-time FFT (sqrt-Hann, 50% overlap). Per frequency bin the 2×2 covariance of L/R is tracked over
    /// ~80 ms; modelling each bin as one panned direct sound plus equal, independent ambience in both
    /// channels, the ambience power is the covariance matrix's smaller eigenvalue (primary–ambient
    /// extraction as in Avendano–Jot / Faller). Each channel keeps ambience / own power of itself (a Wiener
    /// gain), so rear left and right stay as different as the room sound was, and everything above ~7 kHz is
    /// faded out so sibilance does not move behind the listener. Separation is statistical: where a direct
    /// sound and room sound share a bin, a reduced share of the direct sound still reaches the rear.
    ///
    /// Output lags input by exactly <see cref="Latency"/> frames.
    /// </summary>
    public sealed class AmbienceExtractor
    {
        public const double LowpassStartHz = 6000, LowpassEndHz = 9000, SmoothingSeconds = 0.08;
        readonly int n, hop, bins;
        readonly float[] window, lowpass;
        readonly float[] inL, inR, olaL, olaR, outL, outR;
        readonly double[] reL, imL, reR, imR, powerL, powerR, crossRe, crossIm, cosTable, sinTable;
        readonly int[] bitReverse;
        readonly double alpha;
        int position;

        /// <summary>Frames between a sample going in and its ambience coming out (one FFT frame).</summary>
        public int Latency { get { return n; } }

        public AmbienceExtractor(int rate)
        {
            n = rate > 48000 ? 1024 : 512; hop = n / 2; bins = n / 2 + 1;   // ~11 ms frames at 44.1–96 kHz
            window = new float[n]; lowpass = new float[bins];
            for (int i = 0; i < n; i++) window[i] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
            for (int k = 0; k < bins; k++) lowpass[k] = LowpassGain(k * (double)rate / n);
            inL = new float[n]; inR = new float[n]; olaL = new float[n]; olaR = new float[n]; outL = new float[hop]; outR = new float[hop];
            reL = new double[n]; imL = new double[n]; reR = new double[n]; imR = new double[n];
            powerL = new double[bins]; powerR = new double[bins]; crossRe = new double[bins]; crossIm = new double[bins];
            alpha = Math.Exp(-hop / (SmoothingSeconds * rate));
            cosTable = new double[n / 2]; sinTable = new double[n / 2];
            for (int i = 0; i < n / 2; i++) { cosTable[i] = Math.Cos(2 * Math.PI * i / n); sinTable[i] = Math.Sin(2 * Math.PI * i / n); }
            int bits = 0; while ((1 << bits) < n) bits++;
            bitReverse = new int[n];
            for (int i = 0; i < n; i++) { int r = 0; for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b); bitReverse[i] = r; }
        }

        /// <summary>Rear ambience passes fully up to 6 kHz and fades out (raised cosine) by 9 kHz.</summary>
        public static float LowpassGain(double hz)
        {
            if (hz <= LowpassStartHz) return 1;
            if (hz >= LowpassEndHz) return 0;
            return (float)(0.5 + 0.5 * Math.Cos(Math.PI * (hz - LowpassStartHz) / (LowpassEndHz - LowpassStartHz)));
        }

        /// <summary>One stereo frame in; the ambience of the frame <see cref="Latency"/> frames earlier out.</summary>
        public void Process(float left, float right, out float ambienceLeft, out float ambienceRight)
        {
            ambienceLeft = outL[position]; ambienceRight = outR[position];
            inL[hop + position] = left; inR[hop + position] = right;
            if (++position == hop) { Frame(); position = 0; }
        }

        void Frame()
        {
            for (int i = 0; i < n; i++) { reL[i] = inL[i] * window[i]; imL[i] = 0; reR[i] = inR[i] * window[i]; imR[i] = 0; }
            Fft(reL, imL); Fft(reR, imR);
            double keep = 1 - alpha;
            for (int k = 0; k < bins; k++)
            {
                double lr = reL[k], li = imL[k], rr = reR[k], ri = imR[k];
                powerL[k] = alpha * powerL[k] + keep * (lr * lr + li * li);
                powerR[k] = alpha * powerR[k] + keep * (rr * rr + ri * ri);
                crossRe[k] = alpha * crossRe[k] + keep * (lr * rr + li * ri);   // L · conj(R)
                crossIm[k] = alpha * crossIm[k] + keep * (li * rr - lr * ri);
                double sum = powerL[k] + powerR[k], difference = powerL[k] - powerR[k];
                double cross = crossRe[k] * crossRe[k] + crossIm[k] * crossIm[k];
                double ambience = 0.5 * (sum - Math.Sqrt(difference * difference + 4 * cross));
                if (ambience < 0) ambience = 0;
                // Wiener gain (ambience share of this channel's power): close to 1 where the bin is mostly room
                // sound, close to 0 where a direct sound dominates it.
                double gainL = powerL[k] > 1e-12 ? Math.Min(1, ambience / powerL[k]) * lowpass[k] : 0;
                double gainR = powerR[k] > 1e-12 ? Math.Min(1, ambience / powerR[k]) * lowpass[k] : 0;
                reL[k] *= gainL; imL[k] *= gainL; reR[k] *= gainR; imR[k] *= gainR;
                if (k > 0 && k < n / 2) { reL[n - k] *= gainL; imL[n - k] *= gainL; reR[n - k] *= gainR; imR[n - k] *= gainR; }
            }
            // Inverse FFT via conjugation: ifft(x) = conj(fft(conj(x))) / n; only the real part is used.
            for (int i = 0; i < n; i++) { imL[i] = -imL[i]; imR[i] = -imR[i]; }
            Fft(reL, imL); Fft(reR, imR);
            for (int i = 0; i < n; i++) { olaL[i] += (float)(reL[i] / n) * window[i]; olaR[i] += (float)(reR[i] / n) * window[i]; }
            // The first half is now complete (two overlapping frames, sqrt-Hann² sums to 1): hand it out.
            Array.Copy(olaL, 0, outL, 0, hop); Array.Copy(olaR, 0, outR, 0, hop);
            Array.Copy(olaL, hop, olaL, 0, hop); Array.Clear(olaL, hop, hop);
            Array.Copy(olaR, hop, olaR, 0, hop); Array.Clear(olaR, hop, hop);
            Array.Copy(inL, hop, inL, 0, hop); Array.Copy(inR, hop, inR, 0, hop);
        }

        /// <summary>In-place iterative radix-2 FFT (forward, unnormalised).</summary>
        void Fft(double[] re, double[] im)
        {
            for (int i = 0; i < n; i++)
            {
                int j = bitReverse[i];
                if (j > i) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size >> 1, step = n / size;
                for (int start = 0; start < n; start += size)
                    for (int k = 0; k < half; k++)
                    {
                        double wr = cosTable[k * step], wi = -sinTable[k * step];
                        int a = start + k, b = a + half;
                        double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    }
            }
        }
    }
}

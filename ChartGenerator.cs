using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MP4Levels
{
    /// <summary>Mono audio buffer used for analysis.</summary>
    public sealed class AudioData
    {
        public float[] Samples;
        public int SampleRate;
        public double Duration => Samples.Length / (double)SampleRate;
    }

    public static class WavReader
    {
        /// <summary>Reads a 16-bit PCM wav, downmixes to mono and decimates to roughly 22.05 kHz.</summary>
        public static AudioData ReadMono(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var br = new BinaryReader(fs))
            {
                if (Ascii(br.ReadBytes(4)) != "RIFF") throw new InvalidDataException("not a RIFF file");
                br.ReadInt32();
                if (Ascii(br.ReadBytes(4)) != "WAVE") throw new InvalidDataException("not a WAVE file");

                int channels = 0, rate = 0, bits = 0;
                while (fs.Position + 8 <= fs.Length)
                {
                    string id = Ascii(br.ReadBytes(4));
                    long size = br.ReadUInt32();
                    if (id == "fmt ")
                    {
                        long start = fs.Position;
                        br.ReadInt16(); // format tag
                        channels = br.ReadInt16();
                        rate = br.ReadInt32();
                        br.ReadInt32(); br.ReadInt16();
                        bits = br.ReadInt16();
                        fs.Position = start + size;
                    }
                    else if (id == "data")
                    {
                        if (bits != 16 || channels < 1) throw new InvalidDataException("expected 16-bit PCM wav");
                        long avail = fs.Length - fs.Position;
                        if (size == 0 || size > avail) size = avail;
                        return ReadPcm16(fs, size, channels, rate);
                    }
                    else fs.Position += size + (size & 1);
                }
                throw new InvalidDataException("wav has no data chunk");
            }
        }

        static AudioData ReadPcm16(Stream s, long size, int channels, int rate)
        {
            int decim = Math.Max(1, (int)Math.Round(rate / 22050.0));
            long frames = size / (2 * channels);
            var outBuf = new float[frames / decim];
            var buf = new byte[2 * channels * decim * 4096];
            long outIdx = 0;
            while (outIdx < outBuf.Length)
            {
                int want = (int)Math.Min(buf.Length, (outBuf.Length - outIdx) * 2 * channels * decim);
                int got = 0;
                while (got < want) { int n = s.Read(buf, got, want - got); if (n <= 0) break; got += n; }
                int groups = got / (2 * channels * decim);
                if (groups == 0) break;
                for (int g = 0; g < groups; g++)
                {
                    float acc = 0;
                    int baseByte = g * 2 * channels * decim;
                    for (int k = 0; k < channels * decim; k++)
                        acc += (short)(buf[baseByte + 2 * k] | (buf[baseByte + 2 * k + 1] << 8));
                    outBuf[outIdx++] = acc / (32768f * channels * decim);
                }
                if (got < want) break;
            }
            if (outIdx < outBuf.Length) Array.Resize(ref outBuf, (int)outIdx);
            return new AudioData { Samples = outBuf, SampleRate = rate / decim };
        }

        static string Ascii(byte[] b) => Encoding.ASCII.GetString(b);
    }

    public sealed class SongMeta
    {
        public string Title = "";
        public string Artist = "";
        public string AudioFilename = "audio.wav";
    }

    public sealed class GeneratorSettings
    {
        public int OffsetMs = 0;          // shifts every generated note (positive = later)
        public bool Flips = true;         // side switches
        public bool Holds = true;
        public double Density = 1.0;      // global multiplier on note count
    }

    public sealed class ChartResult
    {
        public string Difficulty;
        public string Text;
        public int NoteCount;
        public int Level;
    }

    /// <summary>
    /// Turns raw audio into UNBEATABLE charts (osu!mania 6K style text).
    /// Pipeline: STFT -> band spectral flux -> tempo (autocorrelation) -> DP beat tracking
    /// -> beat-synchronous candidate grid -> per-difficulty note selection -> lanes/holds/flips.
    /// </summary>
    public static class ChartGenerator
    {
        public const int Version = 3; // bump to invalidate cached charts

        public static readonly string[] Difficulties = { "Beginner", "Easy", "Normal", "Hard", "UNBEATABLE" };

        // osu x positions -> lanes (laneNumber = x*6/512 + 1)
        const int XTop = 213;   // lane 3 => Top
        const int XLow = 298;   // lane 4 => Low
        const int XFlip = 384;  // lane 5 => side flip

        const int N = 1024, Hop = 256;

        sealed class Profile
        {
            public string Name;
            public double TargetNps;
            public double MinGapMs;
            public double MinGapBeats;
            public double[] LevelWeight; // beat, half-beat, quarter-beat (0 = disallowed)
            public double MinStrength;
            public double DoubleEveryBeats; // <=0 disables doubles
            public double HoldMinGapBeats;
            public double FlipEveryBeats;
            public double FlipMinGapBeats;
            public double HoldShare;        // max fraction of notes that may become holds
        }

        static readonly Profile[] Profiles =
        {
            new Profile { Name = "Beginner",   TargetNps = 0.55, MinGapMs = 900, MinGapBeats = 2,    LevelWeight = new[] { 1.0, 0.0, 0.0 },  MinStrength = 0.30, DoubleEveryBeats = 0,  HoldMinGapBeats = 3.5, FlipEveryBeats = 48, FlipMinGapBeats = 2.5, HoldShare = 0.30 },
            new Profile { Name = "Easy",       TargetNps = 1.10, MinGapMs = 430, MinGapBeats = 1,    LevelWeight = new[] { 1.0, 0.55, 0.0 }, MinStrength = 0.25, DoubleEveryBeats = 48, HoldMinGapBeats = 3.0, FlipEveryBeats = 32, FlipMinGapBeats = 2.0, HoldShare = 0.22 },
            new Profile { Name = "Normal",     TargetNps = 2.00, MinGapMs = 220, MinGapBeats = 0.5,  LevelWeight = new[] { 1.0, 0.8, 0.0 },  MinStrength = 0.20, DoubleEveryBeats = 16, HoldMinGapBeats = 2.0, FlipEveryBeats = 32, FlipMinGapBeats = 1.5, HoldShare = 0.14 },
            new Profile { Name = "Hard",       TargetNps = 3.30, MinGapMs = 125, MinGapBeats = 0.25, LevelWeight = new[] { 1.0, 0.85, 0.6 }, MinStrength = 0.16, DoubleEveryBeats = 6,  HoldMinGapBeats = 2.0, FlipEveryBeats = 24, FlipMinGapBeats = 1.25, HoldShare = 0.08 },
            new Profile { Name = "UNBEATABLE", TargetNps = 4.80, MinGapMs = 95,  MinGapBeats = 0.25, LevelWeight = new[] { 1.0, 0.9, 0.8 },  MinStrength = 0.12, DoubleEveryBeats = 3,  HoldMinGapBeats = 1.5, FlipEveryBeats = 16, FlipMinGapBeats = 1.0, HoldShare = 0.05 },
        };

        // ---------------------------------------------------------------- analysis

        public sealed class Analysis
        {
            public double Fps;
            public double FrameTimeOffset; // seconds to add to frame*hop/sr
            public float[] Onset, Low, High, Rms;
            public double[] Beats;      // seconds
            public double Bpm;
            public double GridBeatLen;  // seconds (median beat length)
            public List<double[]> TimingPoints; // { start seconds, beat length seconds }
            public double SteadyFraction;       // share of beats inside steady-tempo runs
            public int PrependedBeats;
            public double Duration;
            public double FrameToTime(double f) => f / Fps + FrameTimeOffset;
            public int TimeToFrame(double t) => (int)Math.Round((t - FrameTimeOffset) * Fps);
        }

        public static Analysis Analyze(AudioData audio)
        {
            var x = audio.Samples;
            int sr = audio.SampleRate;
            int frames = Math.Max(0, (x.Length - N) / Hop + 1);
            if (frames < 200) throw new InvalidDataException("audio too short");

            var window = new double[N];
            for (int i = 0; i < N; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N);

            double binHz = sr / (double)N;
            int lowA = Math.Max(1, (int)(30 / binHz)), lowB = (int)(170 / binHz);
            int highA = (int)(250 / binHz), highB = Math.Min(N / 2 - 1, (int)(6000 / binHz));
            int fullB = Math.Min(N / 2 - 1, (int)(9000 / binHz));

            var re = new double[N]; var im = new double[N];
            var prev = new double[N / 2]; var cur = new double[N / 2];
            var onset = new float[frames]; var low = new float[frames]; var high = new float[frames]; var rms = new float[frames];

            for (int f = 0; f < frames; f++)
            {
                int o = f * Hop;
                double e = 0;
                for (int i = 0; i < N; i++) { double v = x[o + i]; e += v * v; re[i] = v * window[i]; im[i] = 0; }
                rms[f] = (float)Math.Sqrt(e / N);
                Fft(re, im);
                for (int k = 0; k < N / 2; k++) cur[k] = Math.Log(1 + 20 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                if (f > 0)
                {
                    double sf = 0, sl = 0, sh = 0;
                    for (int k = 1; k <= fullB; k++)
                    {
                        // SuperFlux-style: compare against max of neighbouring bins in the previous frame
                        double p = prev[k];
                        if (prev[k - 1] > p) p = prev[k - 1];
                        if (k + 1 < N / 2 && prev[k + 1] > p) p = prev[k + 1];
                        double d = cur[k] - p;
                        if (d <= 0) continue;
                        sf += d;
                        if (k >= lowA && k <= lowB) sl += d;
                        if (k >= highA && k <= highB) sh += d;
                    }
                    onset[f] = (float)sf; low[f] = (float)sl; high[f] = (float)sh;
                }
                var t = prev; prev = cur; cur = t;
            }

            var a = new Analysis
            {
                Fps = sr / (double)Hop,
                FrameTimeOffset = (N / 2.0) / sr + 0.008, // +8 ms measured flux-peak latency
                Onset = Smooth(onset), Low = Smooth(low), High = Smooth(high), Rms = rms,
                Duration = audio.Duration,
            };

            // detrend + normalise the onset envelope for tempo/beat tracking
            var env = HighPass(a.Onset, (int)(a.Fps * 1.0));
            double sd = Std(env);
            if (sd <= 1e-9) throw new InvalidDataException("audio is silent");
            for (int i = 0; i < env.Length; i++) env[i] = (float)(env[i] / sd);

            a.Bpm = EstimateTempo(env, a.Fps);
            var beatFrames = TrackBeats(env, a.Fps, a.Bpm);
            if (beatFrames.Length < 8) throw new InvalidDataException("could not find a beat");
            a.Beats = beatFrames.Select(f => a.FrameToTime(RefinePeak(a.Onset, f, 2))).ToArray();

            // Split the beats into runs that sit on a straight line (steady tempo). Each long run is snapped
            // onto its fitted line (removes beat jitter) and gets its own timing point.
            var beats = a.Beats;
            int nb = beats.Length;
            var segs = new List<int[]>();
            int s0 = 0;
            while (s0 < nb - 1)
            {
                int e = Math.Min(nb - 1, s0 + 1);
                while (e + 1 < nb)
                {
                    FitLine(beats, s0, e + 1, out _, out _, out double maxRes);
                    if (maxRes > 0.015) break;
                    e++;
                }
                segs.Add(new[] { s0, e });
                s0 = e;
            }
            var snapped = (double[])beats.Clone();
            a.TimingPoints = new List<double[]>();
            int steadyBeats = 0;
            foreach (var sg in segs)
            {
                int len = sg[1] - sg[0];
                if (len < 8) continue;
                steadyBeats += len;
                FitLine(beats, sg[0], sg[1], out double slope, out double icpt, out _);
                for (int i = sg[0]; i <= sg[1]; i++) snapped[i] = icpt + (i - sg[0]) * slope;
                double startT = snapped[sg[0]];
                if (a.TimingPoints.Count > 0)
                {
                    var last = a.TimingPoints[a.TimingPoints.Count - 1];
                    double k = Math.Round((startT - last[0]) / last[1]);
                    bool samePhase = Math.Abs(last[0] + k * last[1] - startT) < 0.006;
                    if (samePhase && Math.Abs(slope / last[1] - 1) < 0.002) continue; // same tempo, keep one timing point
                }
                a.TimingPoints.Add(new[] { startT, slope });
            }
            a.Beats = snapped;
            a.SteadyFraction = steadyBeats / (double)Math.Max(1, nb - 1);

            var intervals = new List<double>();
            for (int i = 1; i < nb; i++) intervals.Add(a.Beats[i] - a.Beats[i - 1]);
            intervals.Sort();
            a.GridBeatLen = intervals[intervals.Count / 2];
            if (a.TimingPoints.Count == 0) a.TimingPoints.Add(new[] { a.Beats[0], a.GridBeatLen });

            // extend the grid through intros/outros the tracker trimmed, so strong onsets there still get notes
            var ext = new List<double>();
            double firstLen = a.TimingPoints[0][1];
            for (double t = a.Beats[0] - firstLen; t > 0.25; t -= firstLen) ext.Insert(0, t);
            a.PrependedBeats = ext.Count;
            ext.AddRange(a.Beats);
            double lastLen = a.TimingPoints[a.TimingPoints.Count - 1][1];
            for (double t = a.Beats[nb - 1] + lastLen; t < a.Duration - 0.25; t += lastLen) ext.Add(t);
            a.Beats = ext.ToArray();
            if (a.PrependedBeats > 0)
            {
                var tp0 = a.TimingPoints[0];
                a.TimingPoints[0] = new[] { tp0[0] - Math.Floor((tp0[0] - 0.0) / tp0[1]) * tp0[1], tp0[1] };
            }
            return a;
        }

        static void FitLine(double[] b, int i0, int i1, out double slope, out double icpt, out double maxRes)
        {
            int n = i1 - i0 + 1;
            double mx = (n - 1) / 2.0, my = 0;
            for (int i = i0; i <= i1; i++) my += b[i];
            my /= n;
            double sxy = 0, sxx = 0;
            for (int i = 0; i < n; i++) { sxy += (i - mx) * (b[i0 + i] - my); sxx += (i - mx) * (i - mx); }
            slope = sxx > 0 ? sxy / sxx : 0;
            icpt = my - slope * mx;
            maxRes = 0;
            for (int i = 0; i < n; i++) maxRes = Math.Max(maxRes, Math.Abs(b[i0 + i] - (icpt + i * slope)));
        }

        /// <summary>Moves a frame index to the strongest nearby envelope peak, with sub-frame parabolic interpolation.</summary>
        static double RefinePeak(float[] env, int f, int radius)
        {
            int best = f;
            for (int k = f - radius; k <= f + radius; k++)
                if (k > 0 && k < env.Length - 1 && env[k] > env[best]) best = k;
            if (best <= 0 || best >= env.Length - 1) return best;
            double y0 = env[best - 1], y1 = env[best], y2 = env[best + 1];
            double den = y0 - 2 * y1 + y2;
            double frac = Math.Abs(den) > 1e-12 ? 0.5 * (y0 - y2) / den : 0;
            return best + Math.Max(-0.5, Math.Min(0.5, frac));
        }

        static float[] Smooth(float[] v)
        {
            var o = new float[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                float l = i > 0 ? v[i - 1] : v[i], r = i + 1 < v.Length ? v[i + 1] : v[i];
                o[i] = 0.25f * l + 0.5f * v[i] + 0.25f * r;
            }
            return o;
        }

        static float[] HighPass(float[] v, int win)
        {
            // subtract a centred moving average, keep positive part
            var o = new float[v.Length];
            double acc = 0; int half = win / 2;
            var pre = new double[v.Length + 1];
            for (int i = 0; i < v.Length; i++) pre[i + 1] = pre[i] + v[i];
            for (int i = 0; i < v.Length; i++)
            {
                int a = Math.Max(0, i - half), b = Math.Min(v.Length, i + half + 1);
                acc = (pre[b] - pre[a]) / (b - a);
                o[i] = (float)Math.Max(0, v[i] - acc);
            }
            return o;
        }

        static double Std(float[] v)
        {
            double m = 0; foreach (var f in v) m += f; m /= v.Length;
            double s = 0; foreach (var f in v) s += (f - m) * (f - m);
            return Math.Sqrt(s / v.Length);
        }

        static double EstimateTempo(float[] env, double fps)
        {
            int n = env.Length;
            double mean = 0; foreach (var f in env) mean += f; mean /= n;
            int minLag = (int)Math.Floor(fps * 60 / 210), maxLag = (int)Math.Ceiling(fps * 60 / 55);
            var ac = new double[maxLag + 2];
            for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
            {
                double s = 0;
                for (int i = 0; i + lag < n; i++) s += (env[i] - mean) * (env[i + lag] - mean);
                ac[lag] = s / (n - lag);
            }
            double best = double.NegativeInfinity; int bestLag = minLag;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double bpm = 60 * fps / lag;
                double w = Math.Exp(-0.5 * Math.Pow(Math.Log(bpm / 120, 2) / 0.9, 2));
                // reward lags whose double is also periodic (helps pick the right octave)
                double score = (ac[lag] + 0.5 * (2 * lag <= maxLag ? ac[2 * lag] : 0)) * w;
                if (score > best) { best = score; bestLag = lag; }
            }
            // parabolic refinement
            double y0 = ac[bestLag - 1], y1 = ac[bestLag], y2 = ac[bestLag + 1];
            double denom = y0 - 2 * y1 + y2;
            double frac = Math.Abs(denom) > 1e-12 ? 0.5 * (y0 - y2) / denom : 0;
            if (Math.Abs(frac) > 1) frac = 0;
            double bpmOut = 60 * fps / (bestLag + frac);
            while (bpmOut < 75) bpmOut *= 2;
            while (bpmOut > 200) bpmOut /= 2;
            return bpmOut;
        }

        /// <summary>Dynamic-programming beat tracker (Ellis 2007, as in librosa).</summary>
        static int[] TrackBeats(float[] env, double fps, double bpm, double tightness = 100)
        {
            int n = env.Length;
            double period = 60 * fps / bpm;

            // local score: onset envelope smoothed with a gaussian ~ period/32 wide
            int half = (int)Math.Round(period);
            var kern = new double[2 * half + 1];
            for (int i = -half; i <= half; i++) kern[i + half] = Math.Exp(-0.5 * Math.Pow(i * 32.0 / period, 2));
            var local = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int k = -half; k <= half; k++) { int j = i + k; if (j >= 0 && j < n) s += env[j] * kern[k + half]; }
                local[i] = s;
            }

            int wStart = (int)Math.Round(-2 * period), wEnd = -(int)Math.Round(period / 2);
            int wl = wEnd - wStart + 1;
            var txwt = new double[wl];
            for (int k = 0; k < wl; k++) { double w = wStart + k; txwt[k] = -tightness * Math.Pow(Math.Log(-w / period), 2); }

            var cum = new double[n]; var back = new int[n];
            double maxLocal = local.Max();
            bool first = true;
            for (int i = 0; i < n; i++)
            {
                double best = double.NegativeInfinity; int bestJ = -1;
                for (int k = 0; k < wl; k++)
                {
                    int j = i + wStart + k;
                    if (j < 0) continue;
                    double v = cum[j] + txwt[k];
                    if (v > best) { best = v; bestJ = j; }
                }
                if (bestJ < 0) { cum[i] = local[i]; back[i] = -1; }
                else { cum[i] = local[i] + best; back[i] = bestJ; }
                if (first && local[i] < 0.01 * maxLocal) back[i] = -1; else first = false;
            }

            // last beat: the last local max of cum that is reasonably large
            var maxima = new List<int>();
            for (int i = 1; i < n - 1; i++) if (cum[i] > cum[i - 1] && cum[i] >= cum[i + 1]) maxima.Add(i);
            if (maxima.Count == 0) return new int[0];
            var vals = maxima.Select(i => cum[i]).OrderBy(v => v).ToList();
            double med = vals[vals.Count / 2];
            int tail = maxima.Last(i => cum[i] >= 0.5 * med);

            var beats = new List<int>();
            for (int b = tail; b >= 0; b = back[b]) { beats.Add(b); if (back[b] >= b) break; }
            beats.Reverse();

            // trim weak beats at the edges (silence/fade)
            double thr = 0.5 * Math.Sqrt(beats.Average(b => local[b] * local[b]));
            int s0 = 0, s1 = beats.Count - 1;
            while (s0 < s1 && local[beats[s0]] < thr) s0++;
            while (s1 > s0 && local[beats[s1]] < thr) s1--;
            return beats.Skip(s0).Take(s1 - s0 + 1).ToArray();
        }

        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { double t = re[i]; re[i] = re[j]; re[j] = t; t = im[i]; im[i] = im[j]; im[j] = t; }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = a + len / 2;
                        double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                        double ncr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = ncr;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- chart building

        sealed class Cand
        {
            public double Time;      // seconds
            public double BeatPos;   // beat index (fractional)
            public int Level;        // 0 beat, 1 half, 2 quarter
            public double Strength;  // normalised onset strength
            public double Low, High; // band strengths relative to song average
        }

        sealed class Note
        {
            public double Time, End = -1; // seconds
            public double BeatPos;
            public int X;
            public bool IsFlip;
        }

        public static List<ChartResult> Generate(AudioData audio, SongMeta meta, GeneratorSettings settings, out Analysis analysis)
        {
            var a = Analyze(audio);
            analysis = a;
            var cands = BuildCandidates(a);
            var results = new List<ChartResult>();
            foreach (var p in Profiles)
            {
                var notes = BuildChart(a, cands, p, settings);
                results.Add(Render(a, meta, p, notes, settings));
            }
            return results;
        }

        static List<Cand> BuildCandidates(Analysis a)
        {
            double p90 = Percentile(a.Onset, 0.90);
            double lowMean = Math.Max(1e-9, NonZeroMean(a.Low)), highMean = Math.Max(1e-9, NonZeroMean(a.High));
            var list = new List<Cand>();
            var beats = a.Beats;
            for (int i = 0; i + 1 < beats.Length; i++)
            {
                double b0 = beats[i], b1 = beats[i + 1];
                for (int s = 0; s < 4; s++)
                {
                    double t = b0 + (b1 - b0) * s / 4.0;
                    int f = a.TimeToFrame(t);
                    double best = 0, lo = 0, hi = 0;
                    for (int k = f - 2; k <= f + 2; k++)
                    {
                        if (k < 0 || k >= a.Onset.Length) continue;
                        if (a.Onset[k] > best) best = a.Onset[k];
                        if (a.Low[k] > lo) lo = a.Low[k];
                        if (a.High[k] > hi) hi = a.High[k];
                    }
                    list.Add(new Cand
                    {
                        Time = t, BeatPos = i + s / 4.0, Level = s == 0 ? 0 : s == 2 ? 1 : 2,
                        Strength = best / p90, Low = lo / lowMean, High = hi / highMean,
                    });
                }
            }
            return list;
        }

        static List<Note> BuildChart(Analysis a, List<Cand> cands, Profile p, GeneratorSettings st)
        {
            double beatLen = a.GridBeatLen;
            double minGap = Math.Max(p.MinGapMs / 1000.0, p.MinGapBeats * beatLen * 0.98);
            double songRms = Math.Max(1e-6, Median(a.Rms.Where(v => v > 1e-4)));
            double startT = 1.0, endT = a.Duration - 0.5;

            // --- pick note times, 8 beats at a time, strongest first, within a loudness-scaled budget
            var chosen = new List<Cand>();
            var times = new List<double>(); // sorted
            const int windowBeats = 8;
            int windows = (int)Math.Ceiling((a.Beats.Length - 1) / (double)windowBeats);
            for (int w = 0; w < windows; w++)
            {
                double bpA = w * windowBeats, bpB = bpA + windowBeats;
                var group = cands.Where(c => c.BeatPos >= bpA && c.BeatPos < bpB && c.Time >= startT && c.Time <= endT).ToList();
                if (group.Count == 0) continue;
                double tA = group.First().Time, tB = group.Last().Time + beatLen / 4;
                double loud = WindowRms(a, tA, tB) / songRms;
                double loudFactor = Clamp(Math.Pow(loud, 0.7), 0.25, 1.4);
                int budget = (int)Math.Round(p.TargetNps * st.Density * (tB - tA) * loudFactor);
                if (budget <= 0) continue;

                var ranked = group.Where(c => p.LevelWeight[c.Level] > 0 && c.Strength >= p.MinStrength)
                                  .OrderByDescending(c => c.Strength * p.LevelWeight[c.Level]).ToList();
                int taken = 0;
                foreach (var c in ranked)
                {
                    if (taken >= budget) break;
                    int idx = times.BinarySearch(c.Time);
                    if (idx < 0) idx = ~idx;
                    if (idx > 0 && c.Time - times[idx - 1] < minGap) continue;
                    if (idx < times.Count && times[idx] - c.Time < minGap) continue;
                    times.Insert(idx, c.Time);
                    chosen.Add(c);
                    taken++;
                }
            }
            chosen.Sort((x, y) => x.Time.CompareTo(y.Time));

            // --- lanes: kick-heavy -> low, snare/melody-heavy -> top, ambiguous -> alternate
            var notes = new List<Note>();
            int lastX = XTop, sameRun = 0;
            double lastDoubleBeat = double.NegativeInfinity;
            double strongCut = chosen.Count > 0 ? Percentile(chosen.Select(c => (float)c.Strength).ToArray(), 0.85) : 1;
            foreach (var c in chosen)
            {
                int x;
                if (c.Low > 1.3 * c.High) x = XLow;
                else if (c.High > 1.3 * c.Low) x = XTop;
                else x = lastX == XTop ? XLow : XTop;

                // break up very long single-lane runs on the easier charts
                if (x == lastX) sameRun++; else sameRun = 0;
                if (p.TargetNps < 2.5 && sameRun >= 4) { x = lastX == XTop ? XLow : XTop; sameRun = 0; }

                bool dbl = p.DoubleEveryBeats > 0 && c.Level == 0 && c.Strength >= strongCut
                           && c.Low > 1.2 && c.High > 1.2 && c.BeatPos - lastDoubleBeat >= p.DoubleEveryBeats;
                double t = c.Time;
                notes.Add(new Note { Time = t, BeatPos = c.BeatPos, X = x });
                if (dbl)
                {
                    notes.Add(new Note { Time = t, BeatPos = c.BeatPos, X = x == XTop ? XLow : XTop });
                    lastDoubleBeat = c.BeatPos;
                }
                lastX = x;
            }

            // --- holds: a note followed by a long gap over sustained sound becomes a hold
            if (st.Holds)
            {
                var noteTimes = notes.Select(n => n.Time).Distinct().OrderBy(t => t).ToList();
                var singles = notes.GroupBy(n => n.Time).Where(g => g.Count() == 1).Select(g => g.First()).ToList();
                var eligible = new List<KeyValuePair<Note, double[]>>(); // note -> { score, lengthBeats }
                foreach (var n0 in singles)
                {
                    int idx = noteTimes.BinarySearch(n0.Time);
                    if (idx < 0 || idx + 1 >= noteTimes.Count) continue;
                    double nextT = noteTimes[idx + 1];
                    double gapBeats = (nextT - n0.Time) / beatLen;
                    if (gapBeats < p.HoldMinGapBeats) continue;
                    double at = WindowRms(a, n0.Time, n0.Time + beatLen * 0.25);
                    double during = WindowRms(a, n0.Time + beatLen * 0.25, nextT - beatLen * 0.5);
                    if (at <= 1e-6 || during < 0.55 * at) continue;
                    double lenBeats = Math.Floor((gapBeats - 1.0) * 2) / 2.0;
                    if (lenBeats < 1) continue;
                    eligible.Add(new KeyValuePair<Note, double[]>(n0, new[] { (during / at) * Math.Min(lenBeats, 4), lenBeats }));
                }
                int maxHolds = (int)Math.Round(p.HoldShare * notes.Count);
                foreach (var kv in eligible.OrderByDescending(kv => kv.Value[0]).Take(maxHolds))
                    kv.Key.End = kv.Key.Time + kv.Value[1] * beatLen;
            }

            // --- side flips in roomy gaps, spread out
            if (st.Flips)
            {
                var flips = new List<Note>();
                var sorted = notes.OrderBy(n => n.Time).ToList();
                double lastFlipBeat = 0;
                for (int i = 0; i + 1 < sorted.Count; i++)
                {
                    var n0 = sorted[i]; var n1 = sorted[i + 1];
                    double end0 = Math.Max(n0.Time, n0.End);
                    double gapBeats = (n1.Time - end0) / beatLen;
                    if (gapBeats < p.FlipMinGapBeats) continue;
                    if (n1.BeatPos - lastFlipBeat < p.FlipEveryBeats) continue;
                    double ft = end0 + Math.Min(gapBeats * 0.5, Math.Max(0.5, gapBeats - 1)) * beatLen;
                    flips.Add(new Note { Time = ft, X = XFlip, IsFlip = true });
                    lastFlipBeat = n1.BeatPos;
                }
                notes.AddRange(flips);
            }

            notes.Sort((x, y) => x.Time != y.Time ? x.Time.CompareTo(y.Time) : x.X.CompareTo(y.X));
            return notes;
        }

        static ChartResult Render(Analysis a, SongMeta meta, Profile p, List<Note> notes, GeneratorSettings st)
        {
            var playable = notes.Where(n => !n.IsFlip).ToList();
            double span = playable.Count > 1 ? playable.Last().Time - playable.First().Time : 1;
            double nps = playable.Count / Math.Max(10, span);
            int level = (int)Clamp(Math.Round(1 + 2.4 * nps), 1, 20);

            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            void L(string s) { sb.Append(s).Append("\r\n"); }
            L("osu file format v14");
            L("");
            L("[General]");
            L("AudioFilename: " + meta.AudioFilename);
            L("AudioLeadIn: 0");
            L("PreviewTime: -1");
            L("Countdown: 0");
            L("SampleSet: Soft");
            L("StackLeniency: 0.7");
            L("Mode: 3");
            L("LetterboxInBreaks: 0");
            L("SpecialStyle: 0");
            L("WidescreenStoryboard: 1");
            L("");
            L("[Metadata]");
            L("Title:" + meta.Title);
            L("TitleUnicode:" + meta.Title);
            L("Artist:" + meta.Artist);
            L("ArtistUnicode:" + meta.Artist);
            L("Creator:MP4 Levels auto-charter");
            L("Version:" + p.Name + " [Lv. " + level + "]");
            L("Source:MP4 Levels");
            L("Tags:auto-generated bpm" + a.Bpm.ToString("0.0", ci));
            L("BeatmapID:0");
            L("BeatmapSetID:-1");
            L("");
            L("[Difficulty]");
            L("HPDrainRate:5");
            L("CircleSize:6");
            L("OverallDifficulty:5");
            L("ApproachRate:5");
            L("SliderMultiplier:1.4");
            L("SliderTickRate:1");
            L("");
            L("[Events]");
            L("//Background and Video events");
            L("");
            L("[TimingPoints]");
            foreach (var tp in a.TimingPoints)
            {
                int tms = (int)Math.Round(tp[0] * 1000 + st.OffsetMs);
                L(Math.Max(0, tms).ToString(ci) + "," + (tp[1] * 1000).ToString("0.############", ci) + ",4,2,0,100,1,0");
            }
            L("");
            L("");
            L("[HitObjects]");
            foreach (var n in notes)
            {
                int t = (int)Math.Round(n.Time * 1000 + st.OffsetMs);
                if (n.End > n.Time)
                {
                    int e = (int)Math.Round(n.End * 1000 + st.OffsetMs);
                    L(n.X + ",192," + t + ",128,0," + e + ":0:0:0:0:");
                }
                else L(n.X + ",192," + t + ",1,0,0:0:0:0:");
            }
            return new ChartResult { Difficulty = p.Name, Text = sb.ToString(), NoteCount = playable.Count, Level = level };
        }

        // ---------------------------------------------------------------- helpers

        static double WindowRms(Analysis a, double t0, double t1)
        {
            int f0 = Math.Max(0, a.TimeToFrame(t0)), f1 = Math.Min(a.Rms.Length - 1, a.TimeToFrame(t1));
            if (f1 < f0) return 0;
            double s = 0; for (int f = f0; f <= f1; f++) s += a.Rms[f];
            return s / (f1 - f0 + 1);
        }

        static double Percentile(float[] v, double q)
        {
            if (v.Length == 0) return 1;
            var c = (float[])v.Clone(); Array.Sort(c);
            double r = c[(int)Math.Min(c.Length - 1, Math.Max(0, q * (c.Length - 1)))];
            return r <= 1e-12 ? 1 : r;
        }

        static double Median(IEnumerable<float> v)
        {
            var l = v.ToList(); if (l.Count == 0) return 0;
            l.Sort(); return l[l.Count / 2];
        }

        static double NonZeroMean(float[] v)
        {
            double s = 0; int n = 0; foreach (var f in v) if (f > 0) { s += f; n++; }
            return n == 0 ? 0 : s / n;
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }
}

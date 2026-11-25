using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.MusicHub;

namespace Emby.Server.Implementations.MusicHub;

/// <summary>
/// Measures a song from its decoded sound (mono, 11025 Hz), a piece at a time so a long song does not fill the memory.
/// </summary>
public sealed class AudioFeatureExtractor
{
    /// <summary>
    /// The sample rate the sound must have.
    /// </summary>
    public const int SampleRate = 11025;

    private const int FrameSize = 2048;
    private const int Hop = 256;
    private const int HalfSize = FrameSize / 2;
    private const int ChromaEvery = 4;
    private const int WaveformPoints = 96;
    private const double FramesPerSecond = (double)SampleRate / Hop;
    private const double MinSeconds = 5;
    private const double SilenceLevel = 0.0003;
    private const double MaxIntroSeconds = 45;
    private const double MaxOutroSeconds = 30;

    // How much each note belongs to a key (Krumhansl-Kessler)
    private static readonly double[] _majorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] _minorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    private readonly float[] _frame = new float[FrameSize];
    private readonly double[] _window = new double[FrameSize];
    private readonly double[] _real = new double[FrameSize];
    private readonly double[] _imaginary = new double[FrameSize];
    private readonly double[] _cos = new double[HalfSize];
    private readonly double[] _sin = new double[HalfSize];
    private readonly int[] _reversed = new int[FrameSize];
    private readonly double[] _magnitudes = new double[HalfSize];
    private readonly double[] _previous = new double[HalfSize];
    private readonly int[] _pitchOfBin = new int[HalfSize];
    private readonly double[] _chroma = new double[12];
    private readonly List<float> _levels = [];
    private readonly List<float> _flux = [];
    private readonly int _fluxBins;
    private readonly int _lowBins;
    private readonly double _windowSum;

    private int _filled;
    private long _frames;
    private double _centroidSum;
    private double _flatnessSum;
    private double _spectrumFrames;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioFeatureExtractor"/> class.
    /// </summary>
    public AudioFeatureExtractor()
    {
        var bits = (int)Math.Log2(FrameSize);
        for (var i = 0; i < FrameSize; i++)
        {
            _window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1)));
            _windowSum += _window[i];
            var reversed = 0;
            for (var bit = 0; bit < bits; bit++)
            {
                reversed |= ((i >> bit) & 1) << (bits - 1 - bit);
            }

            _reversed[i] = reversed;
        }

        for (var i = 0; i < HalfSize; i++)
        {
            _cos[i] = Math.Cos(2 * Math.PI * i / FrameSize);
            _sin[i] = -Math.Sin(2 * Math.PI * i / FrameSize);
            var hz = BinHz(i);
            _pitchOfBin[i] = hz is >= 65 and <= 2000
                ? (((int)Math.Round(12 * Math.Log2(hz / 440)) + 9) % 12 + 12) % 12
                : -1;
        }

        _fluxBins = (int)(4000.0 * FrameSize / SampleRate);
        _lowBins = (int)(250.0 * FrameSize / SampleRate);
    }

    /// <summary>
    /// Adds the next piece of the song.
    /// </summary>
    /// <param name="samples">The sound.</param>
    public void Add(ReadOnlySpan<float> samples)
    {
        while (!samples.IsEmpty)
        {
            var count = Math.Min(FrameSize - _filled, samples.Length);
            samples[..count].CopyTo(_frame.AsSpan(_filled));
            _filled += count;
            samples = samples[count..];
            if (_filled == FrameSize)
            {
                ProcessFrame();
                Array.Copy(_frame, Hop, _frame, 0, FrameSize - Hop);
                _filled = FrameSize - Hop;
            }
        }
    }

    /// <summary>
    /// Gets the measures of everything that was added.
    /// </summary>
    /// <returns>The measures, or null if the sound is too short or silent.</returns>
    public MusicAudioFeatures? Finish()
    {
        var seconds = _levels.Count / FramesPerSecond;
        if (seconds < MinSeconds || _spectrumFrames < FramesPerSecond)
        {
            return null;
        }

        var heard = _levels.Where(l => l > SilenceLevel).Select(l => (double)l).OrderBy(l => l).ToArray();
        if (heard.Length < FramesPerSecond)
        {
            return null;
        }

        var loudness = Decibels(Math.Sqrt(heard.Average(l => l * l)));
        var dynamics = Decibels(heard[(int)(heard.Length * 0.95)]) - Decibels(heard[(int)(heard.Length * 0.2)]);
        var (bpm, beat) = FindTempo();
        var (root, minor, keyConfidence) = FindKey();

        var centroid = _centroidSum / _spectrumFrames;
        var flatness = _flatnessSum / _spectrumFrames;
        var attack = _flux.Count > 0 ? _flux.Average() : 0;

        var loud = Unit((loudness + 32) / 24);
        var calm = Unit((dynamics - 6) / 24);
        var brightness = Unit((centroid - 500) / 2000);
        var punch = Unit(attack / 0.55);
        var pace = bpm.HasValue ? Math.Exp(-0.5 * Math.Pow((bpm.Value - 120) / 35, 2)) : 0.2;
        var energy = (loud * 0.5) + (punch * 0.25) + (brightness * 0.15) + ((1 - calm) * 0.1);
        var danceability = (Unit(beat / 0.45) * 0.55) + (pace * 0.25) + ((1 - calm) * 0.2);
        var mode = minor ? 0.5 - (keyConfidence * 0.5) : 0.5 + (keyConfidence * 0.5);
        var speed = bpm.HasValue ? Unit((bpm.Value - 60) / 100) : 0.4;
        var positivity = (mode * 0.35) + (speed * 0.25) + (brightness * 0.2) + (danceability * 0.2);
        var acoustic = ((1 - Unit(flatness / 0.25)) * 0.5) + ((1 - brightness) * 0.3) + (calm * 0.2);
        var (intro, mixOut) = FindBody(seconds);

        return new MusicAudioFeatures(
            bpm.HasValue ? Math.Round(bpm.Value, 1) : null,
            root,
            minor,
            Camelot(root, minor),
            Math.Round(keyConfidence, 3),
            Math.Round(Unit(energy), 3),
            Math.Round(Unit(danceability), 3),
            Math.Round(Unit(positivity), 3),
            Math.Round(brightness, 3),
            Math.Round(Unit(acoustic), 3),
            Math.Round(loudness, 1),
            Math.Round(dynamics, 1),
            Math.Round(intro, 1),
            Math.Round(mixOut, 1),
            Math.Round(seconds, 1),
            Waveform());
    }

    private static double BinHz(int bin) => (double)bin * SampleRate / FrameSize;

    private static double Unit(double value) => Math.Clamp(value, 0, 1);

    private static double Decibels(double level) => 20 * Math.Log10(Math.Max(level, 1e-9));

    private static string Camelot(int root, bool minor)
    {
        // A minor key sits on the same number as its relative major
        var major = minor ? (root + 3) % 12 : root;
        var number = ((((7 * major) % 12) + 7) % 12) + 1;
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture) + (minor ? "A" : "B");
    }

    private static double Correlation(double[] notes, double[] profile, int root)
    {
        var meanNotes = notes.Average();
        var meanProfile = profile.Average();
        double top = 0, a = 0, b = 0;
        for (var i = 0; i < 12; i++)
        {
            var x = notes[(i + root) % 12] - meanNotes;
            var y = profile[i] - meanProfile;
            top += x * y;
            a += x * x;
            b += y * y;
        }

        return a > 0 && b > 0 ? top / Math.Sqrt(a * b) : 0;
    }

    private void ProcessFrame()
    {
        double squares = 0;
        for (var i = FrameSize - Hop; i < FrameSize; i++)
        {
            squares += _frame[i] * _frame[i];
        }

        var level = Math.Sqrt(squares / Hop);
        _levels.Add((float)level);

        Transform();
        for (var i = 0; i < HalfSize; i++)
        {
            _magnitudes[i] = Math.Sqrt((_real[i] * _real[i]) + (_imaginary[i] * _imaginary[i])) * 2 / _windowSum;
        }

        double flux = 0;
        for (var i = 1; i < _fluxBins; i++)
        {
            var compressed = Math.Log(1 + (1000 * _magnitudes[i]));
            if (_frames > 0 && compressed > _previous[i])
            {
                flux += compressed - _previous[i];
            }

            _previous[i] = compressed;
        }

        _flux.Add((float)(flux / _fluxBins));
        if (level > SilenceLevel)
        {
            MeasureSpectrum();
        }

        _frames++;
    }

    private void MeasureSpectrum()
    {
        double weighted = 0, total = 0, logs = 0, power = 0;
        for (var i = 1; i < _fluxBins; i++)
        {
            var magnitude = _magnitudes[i];
            weighted += BinHz(i) * magnitude;
            total += magnitude;
            var p = (magnitude * magnitude) + 1e-14;
            logs += Math.Log(p);
            power += p;
        }

        if (total <= 0)
        {
            return;
        }

        var bins = _fluxBins - 1;
        _centroidSum += weighted / total;
        _flatnessSum += Math.Exp(logs / bins) / (power / bins);
        _spectrumFrames++;

        if (_frames % ChromaEvery == 0)
        {
            for (var i = _lowBins / 4; i < HalfSize; i++)
            {
                var pitch = _pitchOfBin[i];
                if (pitch >= 0)
                {
                    _chroma[pitch] += _magnitudes[i] / total;
                }
            }
        }
    }

    private void Transform()
    {
        for (var i = 0; i < FrameSize; i++)
        {
            _real[_reversed[i]] = _frame[i] * _window[i];
            _imaginary[i] = 0;
        }

        for (var size = 2; size <= FrameSize; size *= 2)
        {
            var half = size / 2;
            var step = FrameSize / size;
            for (var start = 0; start < FrameSize; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var c = _cos[k * step];
                    var s = _sin[k * step];
                    var a = start + k;
                    var b = a + half;
                    var re = (_real[b] * c) - (_imaginary[b] * s);
                    var im = (_real[b] * s) + (_imaginary[b] * c);
                    _real[b] = _real[a] - re;
                    _imaginary[b] = _imaginary[a] - im;
                    _real[a] += re;
                    _imaginary[a] += im;
                }
            }
        }
    }

    private (double? Bpm, double Strength) FindTempo()
    {
        // How much the sound attacks, above what it was doing in the last second
        var count = Math.Min(_flux.Count, (int)(FramesPerSecond * 240));
        var skip = (_flux.Count - count) / 2;
        var onset = new double[count];
        var radius = (int)(FramesPerSecond / 2);
        double running = 0;
        var inside = 0;
        for (var i = 0; i < count; i++)
        {
            var enter = i + radius;
            if (enter < count)
            {
                running += _flux[skip + enter];
                inside++;
            }

            var leave = i - radius - 1;
            if (leave >= 0)
            {
                running -= _flux[skip + leave];
                inside--;
            }

            onset[i] = Math.Max(0, _flux[skip + i] - (running / Math.Max(1, inside)));
        }

        var mean = onset.Average();
        for (var i = 0; i < count; i++)
        {
            onset[i] -= mean;
        }

        var shortest = (int)Math.Floor(60 * FramesPerSecond / 200);
        var longest = (int)Math.Ceiling(60 * FramesPerSecond / 55);
        var furthest = (longest * 4) + 4;
        if (count <= furthest * 2)
        {
            return (null, 0);
        }

        var correlation = new double[furthest + 1];
        for (var lag = 0; lag <= furthest; lag++)
        {
            double sum = 0;
            for (var i = lag; i < count; i++)
            {
                sum += onset[i] * onset[i - lag];
            }

            correlation[lag] = sum / (count - lag);
        }

        if (correlation[0] <= 0)
        {
            return (null, 0);
        }

        var best = -1;
        var bestScore = double.MinValue;
        for (var lag = shortest; lag <= longest; lag++)
        {
            if (correlation[lag] < correlation[lag - 1] || correlation[lag] < correlation[lag + 1])
            {
                continue;
            }

            var bpm = 60 * FramesPerSecond / lag;
            var prior = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120) / 0.8, 2));
            var score = (correlation[lag] + (0.5 * correlation[lag * 2]) + (0.25 * correlation[lag * 4])) * prior;
            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        if (best < 0)
        {
            return (null, 0);
        }

        var strength = Unit(correlation[best] / correlation[0]);
        if (strength < 0.05)
        {
            return (null, strength);
        }

        // Four beats later the same peak gives a four times finer measure
        var far = best * 4;
        for (var lag = Math.Max(1, (best * 4) - 3); lag <= Math.Min(furthest - 1, (best * 4) + 3); lag++)
        {
            if (correlation[lag] > correlation[far])
            {
                far = lag;
            }
        }

        var left = correlation[far - 1];
        var middle = correlation[far];
        var right = correlation[far + 1];
        var bend = left - (2 * middle) + right;
        var offset = Math.Abs(bend) > 1e-12 ? Math.Clamp(0.5 * (left - right) / bend, -0.5, 0.5) : 0;
        var tempo = 60 * FramesPerSecond / ((far + offset) / 4);
        while (tempo < 70)
        {
            tempo *= 2;
        }

        while (tempo > 180)
        {
            tempo /= 2;
        }

        return (tempo, strength);
    }

    private (int Root, bool Minor, double Confidence) FindKey()
    {
        var bestRoot = 0;
        var bestMinor = false;
        var best = double.MinValue;
        for (var root = 0; root < 12; root++)
        {
            var major = Correlation(_chroma, _majorProfile, root);
            if (major > best)
            {
                (best, bestRoot, bestMinor) = (major, root, false);
            }

            var minor = Correlation(_chroma, _minorProfile, root);
            if (minor > best)
            {
                (best, bestRoot, bestMinor) = (minor, root, true);
            }
        }

        return (bestRoot, bestMinor, Unit(best));
    }

    private (double Intro, double MixOut) FindBody(double seconds)
    {
        var whole = (int)seconds;
        var perSecond = new double[whole];
        for (var s = 0; s < whole; s++)
        {
            var from = (int)(s * FramesPerSecond);
            var to = Math.Min(_levels.Count, (int)((s + 1) * FramesPerSecond));
            double sum = 0;
            for (var i = from; i < to; i++)
            {
                sum += _levels[i] * _levels[i];
            }

            perSecond[s] = Math.Sqrt(sum / Math.Max(1, to - from));
        }

        var sorted = perSecond.OrderBy(l => l).ToArray();
        var threshold = sorted[(int)(sorted.Length * 0.75)] * 0.5;
        var first = Array.FindIndex(perSecond, l => l >= threshold);
        var last = Array.FindLastIndex(perSecond, l => l >= threshold);
        var intro = first > 0 && first <= MaxIntroSeconds ? first : 0;
        var mixOut = last + 1.0;
        return (intro, seconds - mixOut <= MaxOutroSeconds ? Math.Min(mixOut, seconds) : seconds);
    }

    private byte[] Waveform()
    {
        var points = new double[WaveformPoints];
        for (var p = 0; p < WaveformPoints; p++)
        {
            var from = (int)((long)p * _levels.Count / WaveformPoints);
            var to = Math.Max(from + 1, (int)((long)(p + 1) * _levels.Count / WaveformPoints));
            double sum = 0;
            for (var i = from; i < to; i++)
            {
                sum += _levels[i] * _levels[i];
            }

            points[p] = Math.Sqrt(sum / (to - from));
        }

        var top = points.Max();
        return points.Select(v => top > 0 ? (byte)Math.Round(100 * v / top) : (byte)0).ToArray();
    }
}

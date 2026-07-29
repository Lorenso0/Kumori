using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ManagedBass;

namespace Kumori.App.Skins;

/// <summary>
/// Produces a container/metadata-independent fingerprint from decoded PCM.
/// Failures are deliberately non-fatal so unsupported codecs remain usable
/// and fall back to their byte hash.
/// </summary>
public static class SkinAudioCanonicalizer
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, AudioHashes> cache =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCachedHashes = 100_000;
    private const int SimilarityBins = 48;
    private const int SimilarityFeaturesPerBin = 3;
    private const int MaxSimilaritySeconds = 120;
    private const float SilenceThreshold = 0.0001f;
    public static string SilentHash { get; } = Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes("kumori:silent-audio")));

    public static bool TryHash(byte[] bytes, string byteHash, out string hash)
    {
        return TryHash(bytes, byteHash, out hash, out _);
    }

    public static bool TryHash(
        byte[] bytes,
        string byteHash,
        out string hash,
        out string? similarityHash)
    {
        hash = "";
        similarityHash = null;
        if (bytes.Length == 0)
        {
            hash = SilentHash;
            similarityHash = "audio:silent";
            return true;
        }
        if (!OperatingSystem.IsWindows()) return false;
        lock (gate)
        {
            if (cache.TryGetValue(byteHash, out var cached))
            {
                hash = cached.SemanticHash;
                similarityHash = cached.SimilarityHash;
                return true;
            }
            var stream = 0;
            try
            {
                stream = Bass.CreateStream(
                    bytes,
                    0,
                    bytes.LongLength,
                    BassFlags.Decode | BassFlags.Float);
                if (stream == 0)
                {
                    Bass.Init(
                        Bass.NoSoundDevice,
                        44_100,
                        (DeviceInitFlags)0,
                        IntPtr.Zero,
                        IntPtr.Zero);
                    stream = Bass.CreateStream(
                        bytes,
                        0,
                        bytes.LongLength,
                        BassFlags.Decode | BassFlags.Float);
                }
                if (stream == 0) return false;

                var info = Bass.ChannelGetInfo(stream);
                using var canonical = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                canonical.AppendData(BitConverter.GetBytes(info.Frequency));
                canonical.AppendData(BitConverter.GetBytes(info.Channels));
                var samples = new float[8192];
                var similaritySamples = new List<float>(
                    Math.Min(info.Frequency * 10, info.Frequency * MaxSimilaritySeconds));
                var maxSimilarityFrames = info.Frequency * MaxSimilaritySeconds;
                var hasAudibleSample = false;
                while (true)
                {
                    var bytesRead = Bass.ChannelGetData(
                        stream,
                        samples,
                        samples.Length * sizeof(float));
                    if (bytesRead <= 0) break;
                    var decoded = samples.AsSpan(0, bytesRead / sizeof(float));
                    if (!hasAudibleSample)
                        foreach (var sample in decoded)
                            if (float.IsFinite(sample)
                                && Math.Abs(sample) > SilenceThreshold)
                            {
                                hasAudibleSample = true;
                                break;
                            }
                    canonical.AppendData(MemoryMarshal.AsBytes(decoded));

                    var channels = Math.Max(1, info.Channels);
                    var frames = decoded.Length / channels;
                    for (var frame = 0;
                         frame < frames && similaritySamples.Count < maxSimilarityFrames;
                         frame++)
                    {
                        var mixed = 0f;
                        for (var channel = 0; channel < channels; channel++)
                        {
                            var sample = decoded[frame * channels + channel];
                            if (float.IsFinite(sample))
                                mixed += sample;
                        }
                        similaritySamples.Add(mixed / channels);
                    }
                }
                hash = hasAudibleSample
                    ? Convert.ToHexString(canonical.GetHashAndReset()).ToLowerInvariant()
                    : SilentHash;
                similarityHash = hasAudibleSample
                    ? BuildSimilarityHash(similaritySamples, info.Frequency)
                    : "audio:silent";
                if (cache.Count >= MaxCachedHashes)
                    cache.Clear();
                cache[byteHash] = new AudioHashes(hash, similarityHash);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (stream != 0)
                {
                    try { Bass.StreamFree(stream); }
                    catch { }
                }
            }
        }
    }

    public static bool AreSimilar(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
            return left.StartsWith("audio:", StringComparison.OrdinalIgnoreCase);
        if (!TryParseSimilarityHash(left, out var leftDuration, out var leftFeatures)
            || !TryParseSimilarityHash(right, out var rightDuration, out var rightFeatures))
            return false;

        var longestDuration = Math.Max(leftDuration, rightDuration);
        if (Math.Abs(leftDuration - rightDuration) > Math.Max(35, longestDuration * 0.05))
            return false;

        long envelopeDifference = 0;
        long movementDifference = 0;
        long crossingDifference = 0;
        for (var index = 0; index < leftFeatures.Length; index += SimilarityFeaturesPerBin)
        {
            envelopeDifference += Math.Abs(leftFeatures[index] - rightFeatures[index]);
            movementDifference += Math.Abs(leftFeatures[index + 1] - rightFeatures[index + 1]);
            crossingDifference += Math.Abs(leftFeatures[index + 2] - rightFeatures[index + 2]);
        }

        return envelopeDifference <= SimilarityBins * 12
               && movementDifference <= SimilarityBins * 18
               && crossingDifference <= SimilarityBins * 24;
    }

    private static string BuildSimilarityHash(IReadOnlyList<float> samples, int frequency)
    {
        if (samples.Count == 0 || frequency <= 0)
            return "audio:silent";

        var peak = 0f;
        for (var index = 0; index < samples.Count; index++)
            peak = Math.Max(peak, Math.Abs(samples[index]));
        if (peak <= SilenceThreshold)
            return "audio:silent";

        var edgeThreshold = Math.Max(SilenceThreshold, peak * 0.01f);
        var start = 0;
        while (start < samples.Count && Math.Abs(samples[start]) <= edgeThreshold)
            start++;
        var end = samples.Count - 1;
        while (end >= start && Math.Abs(samples[end]) <= edgeThreshold)
            end--;
        if (end < start)
            return "audio:silent";

        var length = end - start + 1;
        var durationMilliseconds = (int)Math.Round(length * 1000d / frequency);
        var features = new byte[SimilarityBins * SimilarityFeaturesPerBin];
        var crossingFloor = peak * 0.001f;
        for (var bin = 0; bin < SimilarityBins; bin++)
        {
            var binStart = start + (int)((long)length * bin / SimilarityBins);
            var binEnd = start + (int)((long)length * (bin + 1) / SimilarityBins);
            if (binEnd <= binStart)
                binEnd = Math.Min(end + 1, binStart + 1);

            double energy = 0;
            double movement = 0;
            var crossings = 0;
            var previous = samples[binStart];
            for (var index = binStart; index < binEnd; index++)
            {
                var sample = samples[index];
                energy += sample * sample;
                if (index > binStart)
                {
                    var difference = sample - previous;
                    movement += difference * difference;
                    if (Math.Abs(previous) > crossingFloor
                        && Math.Abs(sample) > crossingFloor
                        && Math.Sign(previous) != Math.Sign(sample))
                        crossings++;
                }
                previous = sample;
            }

            var count = Math.Max(1, binEnd - binStart);
            var rms = Math.Sqrt(energy / count) / peak;
            var movementRms = Math.Sqrt(movement / Math.Max(1, count - 1)) / (peak * 2);
            var crossingRate = (double)crossings / Math.Max(1, count - 1);
            var offset = bin * SimilarityFeaturesPerBin;
            features[offset] = QuantizeFeature(rms);
            features[offset + 1] = QuantizeFeature(movementRms);
            features[offset + 2] = QuantizeFeature(crossingRate);
        }

        return $"audio:v1:{durationMilliseconds}:{Convert.ToHexStringLower(features)}";
    }

    private static byte QuantizeFeature(double value) =>
        (byte)Math.Clamp((int)Math.Round(Math.Sqrt(Math.Clamp(value, 0, 1)) * 255), 0, 255);

    private static bool TryParseSimilarityHash(
        string value,
        out int durationMilliseconds,
        out byte[] features)
    {
        durationMilliseconds = 0;
        features = [];
        var parts = value.Split(':');
        if (parts.Length != 4
            || !parts[0].Equals("audio", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Equals("v1", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[2], out durationMilliseconds))
            return false;
        try
        {
            features = Convert.FromHexString(parts[3]);
            return features.Length == SimilarityBins * SimilarityFeaturesPerBin;
        }
        catch
        {
            return false;
        }
    }

    private sealed record AudioHashes(string SemanticHash, string? SimilarityHash);
}

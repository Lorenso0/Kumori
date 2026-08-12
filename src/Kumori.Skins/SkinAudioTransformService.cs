using ManagedBass;
using System.Buffers.Binary;

namespace Kumori.Skins;

public sealed record SkinAudioAnalysis(
    int SampleRate,
    int Channels,
    double DurationMilliseconds,
    float Peak,
    IReadOnlyList<float> Waveform);

public sealed record SkinAudioNormalizationResult(
    byte[] PcmWav,
    SkinAudioAnalysis Analysis,
    double Gain);

/// <summary>
/// Decodes skin audio through the same BASS backend used by lazer and produces
/// bounded waveform metadata or a standard 16-bit PCM WAV.
/// </summary>
public sealed class SkinAudioTransformService
{
    private const int waveform_bins = 48;
    private const int maximum_seconds = 300;
    private const float silence_threshold = 0.0001f;
    private static readonly object bass_gate = new();

    public SkinAudioAnalysis Analyze(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (bass_gate)
        {
            var decoded = decode(bytes);
            return buildAnalysis(decoded.Samples, decoded.SampleRate, decoded.Channels);
        }
    }

    public SkinAudioNormalizationResult NormalizeToPcmWav(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (bass_gate)
        {
            var decoded = decode(bytes);
            var analysis = buildAnalysis(
                decoded.Samples,
                decoded.SampleRate,
                decoded.Channels);
            var gain = analysis.Peak <= silence_threshold
                ? 1d
                : Math.Min(16d, 0.95d / analysis.Peak);
            return new SkinAudioNormalizationResult(
                writePcmWav(
                    decoded.Samples,
                    decoded.SampleRate,
                    decoded.Channels,
                    gain),
                analysis,
                gain);
        }
    }

    private static DecodedAudio decode(byte[] bytes)
    {
        if (tryDecodePcmWav(bytes, out var wav))
            return wav;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Skin audio decoding requires Windows.");
        if (bytes.Length == 0)
            throw new InvalidDataException("Audio is empty.");
        var stream = Bass.CreateStream(
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
        if (stream == 0)
            throw new InvalidDataException("The audio format could not be decoded.");
        try
        {
            var info = Bass.ChannelGetInfo(stream);
            if (info.Frequency <= 0 || info.Channels is <= 0 or > 8)
                throw new InvalidDataException("The decoded audio format is invalid.");
            var maximumSamples = checked(info.Frequency * info.Channels * maximum_seconds);
            var samples = new List<float>(
                Math.Min(maximumSamples, info.Frequency * info.Channels * 10));
            var buffer = new float[8192];
            while (true)
            {
                var bytesRead = Bass.ChannelGetData(
                    stream,
                    buffer,
                    buffer.Length * sizeof(float));
                if (bytesRead <= 0)
                    break;
                var count = bytesRead / sizeof(float);
                if (samples.Count > maximumSamples - count)
                {
                    throw new InvalidDataException(
                        $"Audio exceeds the {maximum_seconds}-second normalization limit.");
                }
                for (var index = 0; index < count; index++)
                {
                    var sample = buffer[index];
                    samples.Add(float.IsFinite(sample) ? sample : 0);
                }
            }
            if (samples.Count == 0)
                throw new InvalidDataException("Audio decoded to no samples.");
            return new DecodedAudio(samples.ToArray(), info.Frequency, info.Channels);
        }
        finally
        {
            Bass.StreamFree(stream);
        }
    }

    private static bool tryDecodePcmWav(
        ReadOnlySpan<byte> bytes,
        out DecodedAudio decoded)
    {
        decoded = null!;
        if (bytes.Length < 12
            || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }
        ushort format = 0;
        ushort channels = 0;
        var sampleRate = 0;
        ushort bitsPerSample = 0;
        ReadOnlySpan<byte> data = default;
        var offset = 12;
        while (offset <= bytes.Length - 8)
        {
            var chunkId = bytes.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(offset + 4, 4));
            offset += 8;
            if (chunkSize > int.MaxValue
                || offset > bytes.Length - (int)chunkSize)
            {
                return false;
            }
            var chunk = bytes.Slice(offset, (int)chunkSize);
            if (chunkId.SequenceEqual("fmt "u8) && chunk.Length >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                data = chunk;
            }
            offset += checked((int)chunkSize + ((int)chunkSize & 1));
        }
        if (format != 1
            || channels is 0 or > 8
            || sampleRate <= 0
            || bitsPerSample != 16
            || data.IsEmpty
            || data.Length % sizeof(short) != 0)
        {
            return false;
        }
        var maximumSamples = checked(sampleRate * channels * maximum_seconds);
        var sampleCount = data.Length / sizeof(short);
        if (sampleCount > maximumSamples)
        {
            throw new InvalidDataException(
                $"Audio exceeds the {maximum_seconds}-second normalization limit.");
        }
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(
                data.Slice(index * sizeof(short), sizeof(short))) / 32768f;
        }
        decoded = new DecodedAudio(samples, sampleRate, channels);
        return true;
    }

    private static SkinAudioAnalysis buildAnalysis(
        IReadOnlyList<float> samples,
        int sampleRate,
        int channels)
    {
        var frames = samples.Count / channels;
        var peak = 0f;
        var waveform = new float[waveform_bins];
        for (var frame = 0; frame < frames; frame++)
        {
            var framePeak = 0f;
            for (var channel = 0; channel < channels; channel++)
                framePeak = Math.Max(framePeak, Math.Abs(samples[frame * channels + channel]));
            peak = Math.Max(peak, framePeak);
            var bin = Math.Min(
                waveform_bins - 1,
                (int)((long)frame * waveform_bins / Math.Max(1, frames)));
            waveform[bin] = Math.Max(waveform[bin], framePeak);
        }
        return new SkinAudioAnalysis(
            sampleRate,
            channels,
            frames * 1000d / sampleRate,
            peak,
            waveform);
    }

    private static byte[] writePcmWav(
        IReadOnlyList<float> samples,
        int sampleRate,
        int channels,
        double gain)
    {
        var dataBytes = checked(samples.Count * sizeof(short));
        using var output = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8);
        writer.Write(checked(36 + dataBytes));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(checked(sampleRate * channels * sizeof(short)));
        writer.Write((short)(channels * sizeof(short)));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in samples)
        {
            var scaled = Math.Clamp(sample * gain, -1d, 1d);
            writer.Write((short)Math.Round(scaled * short.MaxValue));
        }
        writer.Flush();
        return output.ToArray();
    }

    private sealed record DecodedAudio(float[] Samples, int SampleRate, int Channels);
}

using Kumori.Skins;
using Xunit;

namespace Kumori.Skins.Tests;

public sealed class SkinAudioCanonicalizerTests
{
    [Fact]
    public void Empty_audio_has_a_stable_cross_container_identity()
    {
        Assert.True(SkinAudioCanonicalizer.TryHash(
            [],
            SkinDraftWorkspaceService.Hash([]),
            out var semantic,
            out var similarity));

        Assert.Equal(SkinAudioCanonicalizer.SilentHash, semantic);
        Assert.Equal("audio:silent", similarity);
        Assert.True(SkinAudioCanonicalizer.AreSimilar(similarity, "audio:silent"));
    }

    [Fact]
    public void Audio_analysis_and_normalization_produce_bounded_pcm_waveform()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var input = createPcmWav(8_000, 1, 800, 0.1);
        var service = new SkinAudioTransformService();

        var analysis = service.Analyze(input);
        var normalized = service.NormalizeToPcmWav(input);
        var output = service.Analyze(normalized.PcmWav);

        Assert.Equal(8_000, analysis.SampleRate);
        Assert.Equal(1, analysis.Channels);
        Assert.InRange(analysis.DurationMilliseconds, 99, 101);
        Assert.Equal(48, analysis.Waveform.Count);
        Assert.InRange(analysis.Peak, 0.09f, 0.11f);
        Assert.InRange(normalized.Gain, 9.4, 9.6);
        Assert.InRange(output.Peak, 0.94f, 0.96f);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(
            normalized.PcmWav,
            0,
            4));
    }

    private static byte[] createPcmWav(
        int sampleRate,
        int channels,
        int frames,
        double amplitude)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        var sampleCount = frames * channels;
        var dataBytes = sampleCount * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * sizeof(short));
        writer.Write((short)(channels * sizeof(short)));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (short)Math.Round(
                Math.Sin(frame * Math.PI * 2 * 440 / sampleRate)
                * amplitude
                * short.MaxValue);
            for (var channel = 0; channel < channels; channel++)
                writer.Write(sample);
        }
        return output.ToArray();
    }
}

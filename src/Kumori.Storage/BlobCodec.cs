using System.IO.Compression;
using System.Text.Json;

namespace Kumori.Storage;

/// <summary>
/// Decodes the Python tracker's compressed blobs:
/// zlib.compress(json.dumps(values).encode()) → double[].
/// ZLibStream understands the zlib wrapper (RFC 1950) that Python emits.
/// </summary>
public static class BlobCodec
{
    public static byte[] EncodeOffsets(IReadOnlyList<double> offsets)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            JsonSerializer.Serialize(zlib, offsets);
        }
        return output.ToArray();
    }

    public static double[] DecodeOffsets(byte[] zlibBlob)
    {
        using var input = new MemoryStream(zlibBlob);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return JsonSerializer.Deserialize<double[]>(output.ToArray()) ?? Array.Empty<double>();
    }
}

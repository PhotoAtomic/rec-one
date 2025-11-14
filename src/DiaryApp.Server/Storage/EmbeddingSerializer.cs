using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace DiaryApp.Server.Storage;

internal static class EmbeddingSerializer
{
    public static byte[] SerializeBinary(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] DeserializeBinary(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return Array.Empty<float>();
        }

        if (buffer.Length % sizeof(float) != 0)
        {
            return Array.Empty<float>();
        }

        var floats = new float[buffer.Length / sizeof(float)];
        buffer.CopyTo(MemoryMarshal.AsBytes<float>(floats.AsSpan()));
        return floats;
    }

    public static float[]? DeserializeLegacy(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var data = Convert.FromBase64String(payload);
            if (TryDecompressQuantized(data, out var vector))
            {
                return vector;
            }

            var gzip = TryDecompressGzip(data);
            if (gzip is not null)
            {
                return gzip;
            }

            if (data.Length % sizeof(float) == 0)
            {
                return DeserializeBinary(data);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryDecompressQuantized(ReadOnlySpan<byte> buffer, out float[] result)
    {
        result = Array.Empty<float>();
        if (buffer.Length < sizeof(int) + sizeof(float))
        {
            return false;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        var expectedLength = sizeof(int) + sizeof(float) + length;
        if (length < 0 || buffer.Length != expectedLength)
        {
            return false;
        }

        var scale = BitConverter.ToSingle(buffer.Slice(sizeof(int), sizeof(float)));
        result = new float[length];
        if (scale <= 0f)
        {
            return true;
        }

        var data = buffer.Slice(sizeof(int) + sizeof(float));
        for (var i = 0; i < length; i++)
        {
            var quantized = unchecked((sbyte)data[i]);
            result[i] = quantized * scale;
        }

        return true;
    }

    private static float[]? TryDecompressGzip(ReadOnlySpan<byte> buffer)
    {
        try
        {
            using var input = new MemoryStream(buffer.ToArray());
            using var gzip = new System.IO.Compression.GZipStream(
                input,
                System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);

            var raw = output.ToArray();
            if (raw.Length == 0)
            {
                return Array.Empty<float>();
            }

            var floats = new float[raw.Length / sizeof(float)];
            Buffer.BlockCopy(raw, 0, floats, 0, raw.Length);
            return floats;
        }
        catch
        {
            return null;
        }
    }
}

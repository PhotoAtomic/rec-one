using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiaryApp.Server.Storage;

internal static class EmbeddingFileStore
{
    public static string GetEmbeddingPath(string videoPath)
    {
        return string.IsNullOrWhiteSpace(videoPath)
            ? string.Empty
            : videoPath + ".embeddings";
    }

    public static async Task WriteEmbeddingAsync(string? videoPath, float[]? embedding, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return;
        }

        var path = GetEmbeddingPath(videoPath);
        if (embedding is null || embedding.Length == 0)
        {
            DeleteEmbedding(videoPath);
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bytes = EmbeddingSerializer.SerializeBinary(embedding);
        await using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    public static async Task<float[]?> ReadEmbeddingAsync(string? videoPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return null;
        }

        var path = GetEmbeddingPath(videoPath);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[stream.Length];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return EmbeddingSerializer.DeserializeBinary(buffer);
    }

    public static void DeleteEmbedding(string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return;
        }

        var path = GetEmbeddingPath(videoPath);
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

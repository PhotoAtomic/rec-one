using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DiaryApp.Server.Storage;

internal static class TranscriptFileStore
{
    public static string GetTranscriptPath(string videoPath)
        => Path.ChangeExtension(videoPath, ".txt") ?? $"{videoPath}.txt";

    public static bool Exists(string videoPath)
        => File.Exists(GetTranscriptPath(videoPath));

    public static async Task<string?> ReadTranscriptAsync(string videoPath, CancellationToken cancellationToken)
        => await ReadTranscriptByPathAsync(GetTranscriptPath(videoPath), cancellationToken);

    public static async Task<string?> ReadTranscriptByPathAsync(string transcriptPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(transcriptPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(transcriptPath, cancellationToken);
    }

    public static Task WriteTranscriptAsync(string videoPath, string transcript, CancellationToken cancellationToken)
        => WriteTranscriptByPathAsync(GetTranscriptPath(videoPath), transcript, cancellationToken);

    public static async Task WriteTranscriptByPathAsync(string transcriptPath, string transcript, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(transcriptPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(transcriptPath, transcript, cancellationToken);
    }
}

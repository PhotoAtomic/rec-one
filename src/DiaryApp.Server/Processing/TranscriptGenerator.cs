using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using DiaryApp.Server.Storage;
using DiaryApp.Shared.Abstractions;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xabe.FFmpeg;

namespace DiaryApp.Server.Processing;

public sealed class TranscriptGenerator : ITranscriptGenerator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly TranscriptOptions _options;
    private readonly IVideoEntryStore _store;
    private readonly ILogger<TranscriptGenerator> _logger;
    private readonly AzureSpeechSettings? _azureSettings;
    private readonly string? _ffmpegPath;
    private bool _ffmpegConfigured;
    private readonly object _ffmpegInitGate = new();

    public TranscriptGenerator(
        IOptions<TranscriptOptions> options,
        ILogger<TranscriptGenerator> logger,
        IVideoEntryStore store)
    {
        _options = options.Value;
        _logger = logger;
        _store = store;

        if (string.Equals(_options.Provider, "AzureSpeech", StringComparison.OrdinalIgnoreCase))
        {
            _azureSettings = AzureSpeechSettings.TryCreate(_options, _logger);
            _ffmpegPath = GetSetting(_options, "FFmpegPath");
        }
    }

    public async Task<string?> GenerateAsync(VideoEntryDto entry, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(entry.VideoPath))
        {
            return entry.Transcript;
        }

        if (!File.Exists(entry.VideoPath))
        {
            _logger.LogWarning("Unable to generate transcript: missing video file for entry {EntryId}", entry.Id);
            return entry.Transcript;
        }

        var transcriptLanguage = await ResolveTranscriptLanguageAsync(cancellationToken);
        var transcriptPath = GetTranscriptPath(entry.VideoPath);
        var fileLock = FileLocks.GetOrAdd(transcriptPath, static _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(transcriptPath))
            {
                return await File.ReadAllTextAsync(transcriptPath, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(entry.Transcript))
            {
                await WriteTranscriptAsync(transcriptPath, entry.Transcript, cancellationToken);
                return entry.Transcript;
            }

            var provider = _options.Provider;
            if (string.Equals(provider, "AzureSpeech", StringComparison.OrdinalIgnoreCase))
            {
                var transcript = await GenerateWithAzureSpeechAsync(entry.VideoPath, transcriptLanguage, cancellationToken);
                if (!string.IsNullOrWhiteSpace(transcript))
                {
                    await WriteTranscriptAsync(transcriptPath, transcript, cancellationToken);
                }
                return transcript;
            }

            _logger.LogWarning("No transcription provider configured for entry {EntryId}", entry.Id);
            return null;
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task<string> ResolveTranscriptLanguageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var preferences = await _store.GetPreferencesAsync(cancellationToken);
            var language = preferences?.TranscriptLanguage;
            return string.IsNullOrWhiteSpace(language)
                ? UserMediaPreferences.Default.TranscriptLanguage
                : language.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve transcript language preference. Defaulting to {Language}.",
                UserMediaPreferences.Default.TranscriptLanguage);
            return UserMediaPreferences.Default.TranscriptLanguage;
        }
    }

    private async Task<string?> GenerateWithAzureSpeechAsync(string videoPath, string language, CancellationToken cancellationToken)
    {
        if (_azureSettings is null)
        {
            _logger.LogWarning("Azure Speech selected but configuration is incomplete.");
            return null;
        }

        (string Path, bool Cleanup)? audioFile = null;
        try
        {
            audioFile = await ExtractAudioAsync(videoPath, cancellationToken);
            var transcript = await RecognizeSpeechAsync(audioFile.Value.Path, language, cancellationToken);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                _logger.LogWarning("Azure Speech SDK completed but returned no transcript for {VideoPath}.", videoPath);
            }

            return transcript;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Speech SDK transcription failed for {VideoPath}.", videoPath);
            return null;
        }
        finally
        {
            if (audioFile is { Cleanup: true })
            {
                TryDelete(audioFile.Value.Path);
            }
        }
    }

    private async Task<(string Path, bool CleanupRequired)> ExtractAudioAsync(
        string videoPath,
        CancellationToken cancellationToken)
    {
        EnsureFfmpegConfigured();

        var outputFile = Path.Combine(
            Path.GetTempPath(),
            $"diaryapp-audio-{Guid.NewGuid():N}.wav");

        try
        {
            var conversion = FFmpeg.Conversions.New();
            conversion.SetOverwriteOutput(true);
            conversion.AddParameter(
                $"-y -i {QuotePath(videoPath)} -vn -acodec pcm_s16le -ar 16000 -ac 1 {QuotePath(outputFile)}");

            await conversion.Start(cancellationToken);

            if (!File.Exists(outputFile))
            {
                throw new IOException($"FFmpeg reported success but output file '{outputFile}' was not created.");
            }

            return (outputFile, true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputFile);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(outputFile);
            throw new InvalidOperationException(
                "Unable to extract audio using FFmpeg. Ensure FFmpeg is installed and accessible via PATH or Transcription:Settings:FFmpegPath.",
                ex);
        }
    }

    private async Task<string?> RecognizeSpeechAsync(string wavFile, string language, CancellationToken cancellationToken)
    {
        if (_azureSettings is null)
        {
            return null;
        }

        var speechConfig = _azureSettings.CreateSpeechConfig();
        speechConfig.SpeechRecognitionLanguage = language;
        speechConfig.OutputFormat = OutputFormat.Detailed;

        using var audioConfig = AudioConfig.FromWavFileInput(wavFile);
        using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        SpeechRecognitionResult result;
        try
        {
            result = await recognizer.RecognizeOnceAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure Speech SDK invocation failed for {AudioFile}.", wavFile);
            return null;
        }

        switch (result.Reason)
        {
            case ResultReason.RecognizedSpeech:
                return string.IsNullOrWhiteSpace(result.Text) ? null : result.Text;
            case ResultReason.NoMatch:
                _logger.LogWarning("Azure Speech could not recognize any speech in {AudioFile}.", wavFile);
                return null;
            case ResultReason.Canceled:
                var cancellation = CancellationDetails.FromResult(result);
                if (cancellation.Reason == CancellationReason.Error)
                {
                    _logger.LogError(
                        "Azure Speech canceled recognition for {AudioFile}. Error: {Details}",
                        wavFile,
                        cancellation.ErrorDetails);
                }
                else
                {
                    _logger.LogWarning(
                        "Azure Speech canceled recognition for {AudioFile}. Reason: {Reason}",
                        wavFile,
                        cancellation.Reason);
                }
                return null;
            default:
                _logger.LogWarning("Azure Speech returned unexpected result ({Reason}) for {AudioFile}.", result.Reason, wavFile);
                return null;
        }
    }

    private static async Task WriteTranscriptAsync(string transcriptPath, string transcript, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(transcriptPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(transcriptPath, transcript, cancellationToken);
    }

    private static string GetTranscriptPath(string videoPath)
        => Path.ChangeExtension(videoPath, ".txt") ?? $"{videoPath}.txt";

    private sealed class AzureSpeechSettings
    {
        private readonly Uri? _endpoint;

        private AzureSpeechSettings(string key, string? region, Uri? endpoint, ILogger logger)
        {
            Key = key;
            Region = region;
            _endpoint = endpoint;
            _logger = logger;
        }

        public string Key { get; }
        public string? Region { get; }
        private readonly ILogger _logger;

        public static AzureSpeechSettings? TryCreate(TranscriptOptions options, ILogger logger)
        {
            var key = GetSetting(options, "SpeechKey");
            if (string.IsNullOrWhiteSpace(key))
            {
                logger.LogWarning("Transcription:Settings:SpeechKey is missing.");
                return null;
            }

            var region = GetSetting(options, "SpeechRegion");
            var endpointValue = GetSetting(options, "SpeechToTextEndpoint");
            Uri? endpoint = null;
            if (!string.IsNullOrWhiteSpace(endpointValue))
            {
                if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out endpoint))
                {
                    logger.LogWarning("Invalid SpeechToTextEndpoint value: {Endpoint}", endpointValue);
                    endpoint = null;
                }
            }

            if (endpoint is null && string.IsNullOrWhiteSpace(region))
            {
                logger.LogWarning("Provide either SpeechRegion or a valid SpeechToTextEndpoint for Azure Speech.");
                return null;
            }

            return new AzureSpeechSettings(key, region, endpoint, logger);
        }

        public SpeechConfig CreateSpeechConfig()
        {
            if (_endpoint is null)
            {
                return SpeechConfig.FromSubscription(Key, Region!);
            }

            var endpoint = NormalizeEndpoint(_endpoint);
            return SpeechConfig.FromEndpoint(endpoint, Key);
        }

        private Uri NormalizeEndpoint(Uri endpoint)
        {
            if (endpoint.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
            {
                return endpoint;
            }

            if (endpoint.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(endpoint)
                {
                    Scheme = endpoint.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                    Port = -1
                };

                return builder.Uri;
            }

            _logger.LogWarning("Unsupported Speech endpoint scheme '{Scheme}'. Falling back to region-based endpoint if available.", endpoint.Scheme);
            if (!string.IsNullOrWhiteSpace(Region))
            {
                return new Uri($"wss://{Region}.stt.speech.microsoft.com/speech/universal/v2", UriKind.Absolute);
            }

            return endpoint;
        }
    }

    private static string? GetSetting(TranscriptOptions options, string key)
    {
        if (options.Settings is null)
        {
            return null;
        }

        foreach (var kvp in options.Settings)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(kvp.Value) ? null : kvp.Value;
            }
        }

        return null;
    }

    private void EnsureFfmpegConfigured()
    {
        if (_ffmpegConfigured)
        {
            return;
        }

        lock (_ffmpegInitGate)
        {
            if (_ffmpegConfigured)
            {
                return;
            }

            var resolvedPath = ResolveFfmpegDirectory();
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                const string message = "FFmpeg executables not found. Install FFmpeg and ensure it is on PATH or configure Transcription:Settings:FFmpegPath.";
                _logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            try
            {
                FFmpeg.SetExecutablesPath(resolvedPath);
                _ffmpegConfigured = true;
                _logger.LogInformation("FFmpeg executables resolved at '{Path}'.", resolvedPath);
            }
            catch (Exception ex)
            {
                var message = $"Failed to configure FFmpeg executables path '{resolvedPath}'.";
                _logger.LogError(ex, message);
                throw new InvalidOperationException(message, ex);
            }
        }
    }

    private string? ResolveFfmpegDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            return NormalizeDirectoryCandidate(_ffmpegPath);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        var ffmpegExecutable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var candidateFile = Path.Combine(segment, ffmpegExecutable);
            if (File.Exists(candidateFile))
            {
                return Path.GetDirectoryName(candidateFile);
            }
        }

        return null;
    }

    private static string? NormalizeDirectoryCandidate(string path)
    {
        if (Directory.Exists(path))
        {
            return path;
        }

        if (File.Exists(path))
        {
            return Path.GetDirectoryName(path);
        }

        var directoryCandidate = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directoryCandidate) || !Directory.Exists(directoryCandidate)
            ? null
            : directoryCandidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string QuotePath(string value)
        => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

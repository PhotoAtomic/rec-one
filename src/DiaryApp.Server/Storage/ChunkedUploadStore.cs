using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiaryApp.Server.Storage;

public sealed class ChunkedUploadStore
{
    private const string DefaultUserSegment = "default";

    private readonly ConcurrentDictionary<Guid, UploadSession> _sessions = new();
    private readonly StorageOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<ChunkedUploadStore> _logger;

    public ChunkedUploadStore(
        IOptions<StorageOptions> options,
        IHttpContextAccessor httpContextAccessor,
        ILogger<ChunkedUploadStore> logger)
    {
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public UploadSession Start(string fileName, long totalBytes)
    {
        var segment = GetCurrentUserSegment();
        var sanitizedName = SanitizeFileName(fileName);
        var sessionId = Guid.NewGuid();
        var tempPath = GetTemporaryFilePath(segment, sessionId, sanitizedName);
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        using (File.Create(tempPath))
        {
        }

        var session = new UploadSession(
            sessionId,
            segment,
            tempPath,
            sanitizedName,
            totalBytes,
            0,
            DateTimeOffset.UtcNow);

        _sessions[sessionId] = session;
        return session;
    }

    public async Task<long?> AppendChunkAsync(Guid uploadId, Stream chunk, long offset, long totalBytes, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(uploadId, out var session) || !IsOwnedByCurrentUser(session))
        {
            return null;
        }

        var targetOffset = offset >= 0 ? offset : session.UploadedBytes;
        await using var fileStream = new FileStream(session.TempFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (targetOffset > fileStream.Length)
        {
            _logger.LogWarning("Received out-of-order chunk for upload {UploadId}. Expected offset up to {Expected} but got {Offset}.", uploadId, fileStream.Length, targetOffset);
            targetOffset = fileStream.Length;
        }

        fileStream.Position = targetOffset;
        await chunk.CopyToAsync(fileStream, cancellationToken);

        var uploadedBytes = fileStream.Length;
        var updated = session with { UploadedBytes = uploadedBytes, TotalBytes = totalBytes > 0 ? totalBytes : session.TotalBytes };
        _sessions[uploadId] = updated;

        if (updated.TotalBytes > 0 && uploadedBytes > updated.TotalBytes + (1024 * 1024))
        {
            _logger.LogWarning(
                "Bytes received for upload {UploadId} exceeded expected total. Expected {Expected} bytes, got {Received} bytes.",
                uploadId,
                updated.TotalBytes.ToString("N0", CultureInfo.InvariantCulture),
                uploadedBytes.ToString("N0", CultureInfo.InvariantCulture));
        }

        return uploadedBytes;
    }

    public UploadSession? Complete(Guid uploadId)
    {
        if (!_sessions.TryGetValue(uploadId, out var session) || !IsOwnedByCurrentUser(session))
        {
            return null;
        }

        _sessions.TryRemove(uploadId, out _);
        return session;
    }

    public bool Cancel(Guid uploadId)
    {
        if (!_sessions.TryGetValue(uploadId, out var session) || !IsOwnedByCurrentUser(session))
        {
            return false;
        }

        _sessions.TryRemove(uploadId, out _);
        TryDeleteFile(session.TempFilePath);
        return true;
    }

    private string GetTemporaryFilePath(string userSegment, Guid sessionId, string fileName)
    {
        var uploadDirectory = Path.Combine(GetUserRootDirectory(userSegment), "uploads");
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".webm";
        }

        return Path.Combine(uploadDirectory, $"{sessionId:N}{extension}");
    }

    private string GetUserRootDirectory(string userSegment)
        => userSegment == DefaultUserSegment
            ? Path.Combine(_options.RootDirectory, DefaultUserSegment)
            : Path.Combine(_options.RootDirectory, "users", userSegment);

    private string GetCurrentUserSegment()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext?.User?.Identity?.IsAuthenticated == true)
        {
            var name = httpContext.User.Identity?.Name;
            return SanitizeSegment(string.IsNullOrWhiteSpace(name) ? "user" : name);
        }

        return DefaultUserSegment;
    }

    private static string SanitizeSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", value.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? DefaultUserSegment : sanitized;
    }

    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "recording.webm";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "recording.webm" : sanitized;
    }

    private bool IsOwnedByCurrentUser(UploadSession session)
        => string.Equals(session.UserSegment, GetCurrentUserSegment(), StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record UploadSession(
    Guid Id,
    string UserSegment,
    string TempFilePath,
    string OriginalFileName,
    long TotalBytes,
    long UploadedBytes,
    DateTimeOffset CreatedAt);

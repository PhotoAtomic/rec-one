using System;
using System.Globalization;

namespace DiaryApp.Client;

internal static class AssetHelper
{
    private static readonly string CacheBuster = ResolveCacheBuster();

    public static string asset(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var trimmed = relativePath.Trim();
        var separator = trimmed.Contains('?') ? '&' : '?';
        return FormattableString.Invariant($"{trimmed}{separator}v={CacheBuster}");
    }

    private static string ResolveCacheBuster()
    {
        var version = typeof(AssetHelper).Assembly.GetName().Version;
        if (version is not null)
        {
            return version.ToString();
        }

        return DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);
    }
}

using System.Threading;
using DiaryApp.Shared.Abstractions;
using Microsoft.Extensions.Options;

namespace DiaryApp.Server.Processing;

public sealed class TitleGenerator(IOptions<TitleGenerationOptions> options) : ITitleGenerator
{
    private readonly TitleGenerationOptions _options = options.Value;

    public Task<string?> GenerateTitleAsync(VideoEntryDto entry, string? summary, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult<string?>(null);
        }

        var provider = string.IsNullOrWhiteSpace(_options.Provider) ? "title service" : _options.Provider;
        var baseTitle = !string.IsNullOrWhiteSpace(summary) ? summary.Split('.').FirstOrDefault()?.Trim() : entry.Title;
        return Task.FromResult<string?>($"{baseTitle} ({provider})");
    }
}

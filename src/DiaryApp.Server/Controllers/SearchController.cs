using System.Collections.Generic;
using System.Threading;
using DiaryApp.Server.Processing;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DiaryApp.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class SearchController : ControllerBase
{
    private readonly ISearchIndex _searchIndex;

    public SearchController(ISearchIndex searchIndex)
    {
        _searchIndex = searchIndex;
    }

    [HttpPost]
    public async Task<ActionResult<IReadOnlyCollection<VideoEntrySearchResult>>> SearchAsync([FromBody] SearchQuery query, CancellationToken cancellationToken)
    {
        var results = await _searchIndex.SearchAsync(query, cancellationToken);
        return Ok(results);
    }
}

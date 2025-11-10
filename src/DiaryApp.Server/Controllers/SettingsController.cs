using System.Threading;
using System.Threading.Tasks;
using DiaryApp.Server.Storage;
using DiaryApp.Shared.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DiaryApp.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class SettingsController : ControllerBase
{
    private readonly IVideoEntryStore _store;

    public SettingsController(IVideoEntryStore store)
    {
        _store = store;
    }

    [HttpGet("media")]
    public async Task<ActionResult<UserMediaPreferences>> GetMediaAsync(CancellationToken cancellationToken)
        => Ok(await _store.GetPreferencesAsync(cancellationToken));

    [HttpPut("media")]
    public async Task<IActionResult> UpdateMediaAsync([FromBody] UserMediaPreferences preferences, CancellationToken cancellationToken)
    {
        await _store.UpdatePreferencesAsync(preferences, cancellationToken);
        return NoContent();
    }
}

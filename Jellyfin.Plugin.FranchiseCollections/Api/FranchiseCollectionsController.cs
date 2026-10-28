using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FranchiseCollections.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FranchiseCollections.Api;

/// <summary>
/// Endpoints used by the plugin's settings page.
/// </summary>
[ApiController]
[Route("FranchiseCollections")]
[Authorize(Policy = "RequiresElevation")]
public class FranchiseCollectionsController : ControllerBase
{
    private readonly SyncNotifier _notifier;

    /// <summary>
    /// Initializes a new instance of the <see cref="FranchiseCollectionsController"/> class.
    /// </summary>
    /// <param name="notifier">Discord notifications.</param>
    public FranchiseCollectionsController(SyncNotifier notifier)
    {
        _notifier = notifier;
    }

    /// <summary>Sends a test message to the Discord webhook in the saved settings.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the test.</returns>
    [HttpPost("TestDiscord")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> TestDiscord(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.DiscordWebhookUrl))
        {
            return new { Ok = false, Message = "Enter a Discord webhook URL first." };
        }

        try
        {
            await _notifier.TestAsync(cancellationToken).ConfigureAwait(false);
            return new { Ok = true, Message = "Sent. Check your Discord channel." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new { Ok = false, ex.Message };
        }
    }
}

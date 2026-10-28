using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FranchiseCollections.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FranchiseCollections.Notifications;

/// <summary>
/// Tells Discord what a sync changed. Never throws: a failed post is only logged, so Discord being down
/// can't fail a sync.
/// </summary>
public class SyncNotifier
{
    private readonly DiscordNotifier _discord;
    private readonly ILogger<SyncNotifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncNotifier"/> class.
    /// </summary>
    /// <param name="discord">Discord webhook sender.</param>
    /// <param name="logger">Logger.</param>
    public SyncNotifier(DiscordNotifier discord, ILogger<SyncNotifier> logger)
    {
        _discord = discord;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Posts a sync's changes, if there are any and a webhook is set.</summary>
    /// <param name="report">What changed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SyncFinishedAsync(SyncReport report, CancellationToken cancellationToken)
    {
        var config = Config;
        if (string.IsNullOrWhiteSpace(config.DiscordWebhookUrl)
            || DiscordMessageBuilder.Build(report, config.NotifyAddedMovies, config.PublicServerUrl) is not { } message)
        {
            return;
        }

        try
        {
            await _discord.SendAsync(config.DiscordWebhookUrl, [message], cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Told Discord about the franchise collection sync");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Couldn't post the franchise collection sync to Discord");
        }
    }

    /// <summary>Sends a test message to the saved webhook.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task. Throws with a readable message if it couldn't send.</returns>
    public Task TestAsync(CancellationToken cancellationToken)
        => _discord.SendAsync(Config.DiscordWebhookUrl, [DiscordMessageBuilder.BuildTest()], cancellationToken);
}

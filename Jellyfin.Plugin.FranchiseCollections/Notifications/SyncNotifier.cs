using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FranchiseCollections.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FranchiseCollections.Notifications;

/// <summary>
/// Tells Discord what a sync changed. Never throws: a failed post is only logged, so Discord being down
/// can't fail a sync.
/// </summary>
public class SyncNotifier
{
    private readonly DiscordNotifier _discord;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SyncNotifier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncNotifier"/> class.
    /// </summary>
    /// <param name="discord">Discord webhook sender.</param>
    /// <param name="libraryManager">Library manager, for posters.</param>
    /// <param name="logger">Logger.</param>
    public SyncNotifier(DiscordNotifier discord, ILibraryManager libraryManager, ILogger<SyncNotifier> logger)
    {
        _discord = discord;
        _libraryManager = libraryManager;
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
        if (string.IsNullOrWhiteSpace(config.DiscordWebhookUrl) || report.IsEmpty)
        {
            return;
        }

        var attachments = new List<DiscordAttachment>();
        DiscordPictures? pictures = null;
        if (config.DiscordImages && DiscordMessageBuilder.Headliner(report, config.NotifyAddedMovies) is { } headliner)
        {
            pictures = Pictures(headliner.Change, attachments);
        }

        if (DiscordMessageBuilder.Build(report, config.NotifyAddedMovies, config.PublicServerUrl, pictures) is not { } message)
        {
            return;
        }

        try
        {
            await _discord.SendAsync(config.DiscordWebhookUrl, [(message, (IReadOnlyList<DiscordAttachment>)attachments)], cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The headliner's movie posters in a row and the collection's own poster, added to <paramref name="attachments"/>.
    /// Anything that can't be read is left out, so the message still goes, just with fewer pictures.
    /// </summary>
    private DiscordPictures? Pictures(CollectionChange change, List<DiscordAttachment> attachments)
    {
        string? strip = null, poster = null;
        try
        {
            var posters = new List<byte[]>();
            foreach (var id in change.MovieIds ?? [])
            {
                if (posters.Count == DiscordImages.StripPosters)
                {
                    break;
                }

                if (ReadPoster(id) is { } data)
                {
                    posters.Add(data);
                }
            }

            if (posters.Count > 0 && DiscordImages.PosterStrip(posters) is { } stripData)
            {
                var file = new DiscordAttachment("movies.jpg", stripData, "image/jpeg");
                attachments.Add(file);
                strip = file.Reference;
            }

            // A brand-new collection often has no poster yet (Jellyfin fetches it later); then the card goes without.
            if (ReadPoster(change.CollectionId) is { } own && DiscordImages.Shrink(own, DiscordImages.ThumbnailHeight) is { } ownData)
            {
                var file = new DiscordAttachment("collection.jpg", ownData, "image/jpeg");
                attachments.Add(file);
                poster = file.Reference;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't make the pictures for {Name}; posting to Discord without them", change.Name);
        }

        return strip is null && poster is null ? null : new DiscordPictures(strip, poster);
    }

    /// <summary>An item's poster file from disk, or null if it has none there.</summary>
    private byte[]? ReadPoster(Guid itemId)
    {
        try
        {
            var image = _libraryManager.GetItemById(itemId)?.GetImageInfo(ImageType.Primary, 0);
            return image is not null && image.IsLocalFile && File.Exists(image.Path) ? File.ReadAllBytes(image.Path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Couldn't read the poster of {Item} for Discord", itemId);
            return null;
        }
    }
}

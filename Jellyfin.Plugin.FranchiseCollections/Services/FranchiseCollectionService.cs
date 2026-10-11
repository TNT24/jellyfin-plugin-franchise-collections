using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FranchiseCollections.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FranchiseCollections.Services;

/// <summary>
/// Groups movies by their TMDb collection id and keeps a matching Jellyfin collection in sync for each franchise.
/// </summary>
public class FranchiseCollectionService
{
    private static readonly string TmdbCollectionKey = MetadataProvider.TmdbCollection.ToString();

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<FranchiseCollectionService> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="FranchiseCollectionService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="collectionManager">Collection manager.</param>
    /// <param name="logger">Logger.</param>
    public FranchiseCollectionService(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILogger<FranchiseCollectionService> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    /// <summary>
    /// Creates missing franchise collections and updates existing ones.
    /// </summary>
    /// <param name="overrideManualChanges">
    /// Make each franchise collection match TMDb exactly: remove every member that isn't one of the franchise's movies,
    /// and take over a same-named collection whose TMDb id doesn't belong to any franchise in the library.
    /// </param>
    /// <param name="progress">Progress reporter (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the sync.</returns>
    public async Task SyncAsync(bool overrideManualChanges, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!await _syncLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Franchise collection sync already running, skipping");
            return;
        }

        try
        {
            await SyncInternalAsync(overrideManualChanges, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task SyncInternalAsync(bool overrideManualChanges, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var minimumMovies = Math.Max(1, config.MinimumMovies);
        var excluded = ParseExcluded(config.ExcludedFranchises);
        var libraryIds = ParseLibraryIds(config.LibraryIds);

        var franchises = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Movie>()
            .Where(movie => libraryIds.Count == 0
                || _libraryManager.GetCollectionFolders(movie).Any(library => libraryIds.Contains(library.Id)))
            .Select(movie => (Movie: movie, TmdbId: movie.GetProviderId(TmdbCollectionKey)))
            .Where(x => !string.IsNullOrWhiteSpace(x.TmdbId))
            .GroupBy(x => x.TmdbId!, x => x.Movie, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var franchiseIds = franchises.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingCollections = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.BoxSet],
                Recursive = true
            })
            .OfType<BoxSet>()
            .ToList();

        var collectionsByTmdbId = new Dictionary<string, BoxSet>(StringComparer.OrdinalIgnoreCase);
        var collectionsByName = new Dictionary<string, BoxSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in existingCollections)
        {
            var tmdbId = collection.GetProviderId(TmdbCollectionKey);
            if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                collectionsByTmdbId.TryAdd(tmdbId, collection);
            }

            if (!string.IsNullOrWhiteSpace(collection.Name))
            {
                collectionsByName.TryAdd(collection.Name, collection);
            }
        }

        _logger.LogInformation(
            "Found {Count} franchises across the movie library{Mode}",
            franchises.Count,
            overrideManualChanges ? ", overriding manual changes" : string.Empty);

        int created = 0, updated = 0;
        for (var i = 0; i < franchises.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var franchise = franchises[i];
            var tmdbId = franchise.Key;
            var movies = franchise.ToList();
            var name = movies
                .Select(m => m.CollectionName)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

            try
            {
                // Prefer the TMDb id; fall back to an identically named collection so we don't duplicate
                // one the user (or Jellyfin's built-in auto-collection option) already made.
                if (!collectionsByTmdbId.TryGetValue(tmdbId, out var collection)
                    && name is not null
                    && collectionsByName.TryGetValue(name, out collection))
                {
                    // Same name but a different franchise id: leave it alone, unless overriding and no franchise
                    // in the library owns that id (then it's a hand-made or mis-tagged collection we can take over).
                    var otherId = collection.GetProviderId(TmdbCollectionKey);
                    if (!string.IsNullOrWhiteSpace(otherId) && (!overrideManualChanges || franchiseIds.Contains(otherId)))
                    {
                        collection = null;
                    }
                }

                var displayName = collection?.Name ?? name ?? string.Empty;
                if (excluded.Contains(displayName) || (name is not null && excluded.Contains(name)))
                {
                    continue;
                }

                if (collection is null)
                {
                    if (movies.Count < minimumMovies || name is null)
                    {
                        continue;
                    }

                    // A different franchise already owns this name; collections are stored as "<name> [boxset]"
                    // folders, so reusing the name would point two collections at the same folder.
                    var createName = collectionsByName.ContainsKey(name)
                        ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", name, tmdbId)
                        : name;

                    var newCollection = await CreateCollectionAsync(tmdbId, createName, movies).ConfigureAwait(false);
                    collectionsByTmdbId.TryAdd(tmdbId, newCollection);
                    collectionsByName.TryAdd(createName, newCollection);
                    created++;
                }
                else if (await UpdateCollectionAsync(collection, tmdbId, movies, config.RemoveStaleMovies, overrideManualChanges).ConfigureAwait(false))
                {
                    updated++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to sync franchise collection {Name} (TMDb {TmdbId})", name, tmdbId);
            }

            progress.Report(100d * (i + 1) / franchises.Count);
        }

        progress.Report(100);
        _logger.LogInformation("Franchise collection sync finished: {Created} created, {Updated} updated", created, updated);
    }

    private async Task<BoxSet> CreateCollectionAsync(string tmdbId, string name, IReadOnlyList<Movie> movies)
    {
        _logger.LogInformation("Creating collection {Name} with {Count} movies", name, movies.Count);

        return await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = name,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TmdbCollectionKey] = tmdbId
            },
            ItemIdList = movies.Select(m => m.Id.ToString("N", CultureInfo.InvariantCulture)).ToArray()
        }).ConfigureAwait(false);
    }

    private async Task<bool> UpdateCollectionAsync(
        BoxSet collection,
        string tmdbId,
        IReadOnlyList<Movie> movies,
        bool removeStale,
        bool overrideManualChanges)
    {
        var members = collection.GetLinkedChildrenInfos()
            .Select(info => info.Item2)
            .Where(item => item is not null)
            .ToList();
        var memberIds = members.Select(item => item.Id).ToHashSet();
        var movieIds = movies.Select(m => m.Id).ToHashSet();

        var toAdd = movies.Where(m => !memberIds.Contains(m.Id)).Select(m => m.Id).ToList();

        // Normally only remove movies that TMDb now places in a *different* franchise, so hand-added extras
        // (spin-offs, specials without TMDb collection data) are left untouched. Overriding removes them too.
        var toRemove = overrideManualChanges
            ? members.Where(item => !movieIds.Contains(item.Id)).Select(item => item.Id).ToList()
            : removeStale
            ? members
                .OfType<Movie>()
                .Where(m =>
                {
                    var id = m.GetProviderId(TmdbCollectionKey);
                    return !string.IsNullOrWhiteSpace(id) && !string.Equals(id, tmdbId, StringComparison.OrdinalIgnoreCase);
                })
                .Select(m => m.Id)
                .ToList()
            : [];

        if (toAdd.Count > 0)
        {
            _logger.LogInformation("Adding {Count} movies to collection {Name}", toAdd.Count, collection.Name);
            await _collectionManager.AddToCollectionAsync(collection.Id, toAdd).ConfigureAwait(false);
        }

        if (toRemove.Count > 0)
        {
            _logger.LogInformation("Removing {Count} movies from collection {Name}", toRemove.Count, collection.Name);
            await _collectionManager.RemoveFromCollectionAsync(collection.Id, toRemove).ConfigureAwait(false);
        }

        // Tag a name-matched (or taken-over) collection with the TMDb id so future syncs match it directly.
        if (!string.Equals(collection.GetProviderId(TmdbCollectionKey), tmdbId, StringComparison.OrdinalIgnoreCase))
        {
            collection.SetProviderId(TmdbCollectionKey, tmdbId);
            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        }

        return toAdd.Count > 0 || toRemove.Count > 0;
    }

    private static HashSet<Guid> ParseLibraryIds(string[]? ids)
    {
        return (ids ?? [])
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(guid => guid != Guid.Empty)
            .ToHashSet();
    }

    private static HashSet<string> ParseExcluded(string? value)
    {
        return (value ?? string.Empty)
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

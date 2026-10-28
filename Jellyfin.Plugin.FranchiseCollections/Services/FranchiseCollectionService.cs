using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FranchiseCollections.Configuration;
using Jellyfin.Plugin.FranchiseCollections.Notifications;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FranchiseCollections.Services;

/// <summary>
/// Groups movies by their TMDb collection id and keeps a matching Jellyfin collection in sync for each franchise.
/// </summary>
public class FranchiseCollectionService
{
    private static readonly string TmdbCollectionKey = MetadataProvider.TmdbCollection.ToString();

    // Jellyfin's own TMDb box set provider (and its built-in auto-collections) stores the franchise id under "Tmdb".
    private static readonly string TmdbKey = MetadataProvider.Tmdb.ToString();

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly CollectionPosterGenerator _posterGenerator;
    private readonly SyncNotifier _notifier;
    private readonly ILogger<FranchiseCollectionService> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    // Collections whose TMDb metadata was requested in the current sync; their poster may still be downloading.
    private readonly HashSet<Guid> _metadataRequested = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="FranchiseCollectionService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="collectionManager">Collection manager.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="posterGenerator">Collection poster generator.</param>
    /// <param name="notifier">Discord notifications.</param>
    /// <param name="logger">Logger.</param>
    public FranchiseCollectionService(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        CollectionPosterGenerator posterGenerator,
        SyncNotifier notifier,
        ILogger<FranchiseCollectionService> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _posterGenerator = posterGenerator;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>
    /// Creates missing franchise collections and updates existing ones.
    /// </summary>
    /// <param name="overrideManualChanges">
    /// Make each franchise collection match TMDb exactly: remove every member that isn't one of the franchise's movies,
    /// take over a same-named collection whose TMDb id doesn't belong to any franchise in the library,
    /// delete duplicate collections of the same franchise, and delete every other collection that isn't a franchise
    /// collection (except excluded names).
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
        _metadataRequested.Clear();
        var minimumMovies = Math.Max(1, config.MinimumMovies);
        var excluded = ParseExcluded(config.ExcludedFranchises);
        var libraryIds = ParseLibraryIds(config.LibraryIds);

        var allMovies = GetMovies(libraryIds);

        // With a library filter, collections are only deleted or trimmed when every member is in those libraries,
        // so movies (and collections) from the other libraries are never touched. Null means no filter.
        var scopeMovieIds = libraryIds.Count == 0 ? null : allMovies.Select(m => m.Id).ToHashSet();

        var franchises = allMovies
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

        // Collections this plugin tagged come first, so they're the ones kept when a franchise has duplicates.
        var collectionsByTmdbId = new Dictionary<string, BoxSet>(StringComparer.OrdinalIgnoreCase);
        var collectionsByName = new Dictionary<string, BoxSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in existingCollections.OrderBy(c => string.IsNullOrWhiteSpace(c.GetProviderId(TmdbCollectionKey))))
        {
            var tmdbId = GetFranchiseId(collection);
            if (tmdbId is not null)
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

        int created = 0, updated = 0, deleted = 0, belowMinimumDeleted = 0;
        var report = new SyncReport();
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
                    var otherId = GetFranchiseId(collection);
                    if (otherId is not null
                        && (!overrideManualChanges || franchiseIds.Contains(otherId) || !IsInScope(collection, scopeMovieIds)))
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
                    report.Created.Add(new CollectionChange(newCollection.Id, createName, Titles(movies)));
                    await ApplyDisplaySettingsAsync(newCollection, movies, config, isNew: true, cancellationToken).ConfigureAwait(false);
                }
                else if (collection.IsLocked)
                {
                    // Locked means the user wants it exactly as it is: no members added or removed, never deleted.
                    _logger.LogInformation("Collection {Name} is locked, leaving it as it is", collection.Name);
                    if (string.IsNullOrWhiteSpace(collection.Overview))
                    {
                        _logger.LogWarning(
                            "Collection {Name} has no description but is locked, so Jellyfin won't fetch it. Untick \"Lock this item\" in its metadata editor to allow it",
                            collection.Name);
                    }

                    if (overrideManualChanges)
                    {
                        deleted += DeleteDuplicateCollections(collection, tmdbId, name, existingCollections, collectionsByTmdbId, collectionsByName, scopeMovieIds, report);
                    }
                }
                else
                {
                    if (overrideManualChanges)
                    {
                        deleted += DeleteDuplicateCollections(collection, tmdbId, name, existingCollections, collectionsByTmdbId, collectionsByName, scopeMovieIds, report);

                        // Only creation checks the minimum, so an adopted or older collection can be left with fewer movies.
                        if (movies.Count < minimumMovies && IsInScope(collection, scopeMovieIds))
                        {
                            _logger.LogInformation(
                                "Deleting collection {Name}: it has {Count} movies, fewer than the minimum of {Minimum}",
                                collection.Name,
                                movies.Count,
                                minimumMovies);
                            _libraryManager.DeleteItem(collection, new DeleteOptions { DeleteFileLocation = true });
                            existingCollections.Remove(collection);
                            collectionsByTmdbId.Remove(tmdbId);
                            if (!string.IsNullOrWhiteSpace(collection.Name))
                            {
                                collectionsByName.Remove(collection.Name);
                            }

                            belowMinimumDeleted++;
                            report.Deleted.Add(collection.Name ?? tmdbId);
                            continue;
                        }
                    }

                    if (await UpdateCollectionAsync(collection, tmdbId, movies, config.RemoveStaleMovies, overrideManualChanges, scopeMovieIds, report).ConfigureAwait(false))
                    {
                        updated++;
                    }

                    await ApplyDisplaySettingsAsync(collection, movies, config, isNew: _metadataRequested.Contains(collection.Id), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to sync franchise collection {Name} (TMDb {TmdbId})", name, tmdbId);
            }

            progress.Report(100d * (i + 1) / franchises.Count);
        }

        var handMadeDeleted = 0;
        if (overrideManualChanges)
        {
            handMadeDeleted = DeleteUnmanagedCollections(existingCollections, excluded, franchises.Count, scopeMovieIds, report, cancellationToken);
        }

        progress.Report(100);
        _logger.LogInformation(
            "Franchise collection sync finished: {Created} created, {Updated} updated, {Deleted} duplicates deleted, {BelowMinimum} below the minimum deleted, {HandMade} other collections deleted",
            created,
            updated,
            deleted,
            belowMinimumDeleted,
            handMadeDeleted);

        await _notifier.SyncFinishedAsync(report, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every collection this plugin doesn't manage (no TMDb collection id), except excluded names,
    /// locked collections and, with a library filter, collections holding movies from other libraries.
    /// Same-named collections were already taken over and tagged by the franchise loop, so they're kept.
    /// The movies themselves are not touched.
    /// </summary>
    private int DeleteUnmanagedCollections(
        List<BoxSet> collections,
        HashSet<string> excluded,
        int franchiseCount,
        HashSet<Guid>? scopeMovieIds,
        SyncReport report,
        CancellationToken cancellationToken)
    {
        // No franchises usually means missing metadata or no matching library, not that every collection should go.
        if (franchiseCount == 0)
        {
            _logger.LogWarning("No franchises found, so no other collections are deleted");
            return 0;
        }

        var unmanaged = collections
            .Where(c => string.IsNullOrWhiteSpace(c.GetProviderId(TmdbCollectionKey)))
            .Where(c => string.IsNullOrWhiteSpace(c.Name) || !excluded.Contains(c.Name))
            .Where(c => !c.IsLocked && IsInScope(c, scopeMovieIds))
            .ToList();

        var count = 0;
        foreach (var collection in unmanaged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _logger.LogInformation("Deleting collection {Name}, which isn't a franchise collection", collection.Name);
                _libraryManager.DeleteItem(collection, new DeleteOptions { DeleteFileLocation = true });
                collections.Remove(collection);
                count++;
                report.Deleted.Add(collection.Name ?? string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to delete collection {Name}", collection.Name);
            }
        }

        return count;
    }

    /// <summary>
    /// Deletes the other collections of a franchise: those tagged with its TMDb id, and untagged ones with its name.
    /// Locked collections and, with a library filter, collections holding movies from other libraries are kept.
    /// The movies themselves are not touched.
    /// </summary>
    private int DeleteDuplicateCollections(
        BoxSet keep,
        string tmdbId,
        string? name,
        List<BoxSet> collections,
        Dictionary<string, BoxSet> collectionsByTmdbId,
        Dictionary<string, BoxSet> collectionsByName,
        HashSet<Guid>? scopeMovieIds,
        SyncReport report)
    {
        var duplicates = collections
            .Where(c => c.Id != keep.Id && !c.IsLocked && IsInScope(c, scopeMovieIds))
            .Where(c => GetFranchiseId(c) is { } id
                ? string.Equals(id, tmdbId, StringComparison.OrdinalIgnoreCase)
                : name is not null && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var duplicate in duplicates)
        {
            _logger.LogInformation("Deleting duplicate collection {Name} (keeping {Kept})", duplicate.Name, keep.Name);
            _libraryManager.DeleteItem(duplicate, new DeleteOptions { DeleteFileLocation = true });
            collections.Remove(duplicate);
            report.Deleted.Add((duplicate.Name ?? tmdbId) + " (duplicate)");

            if (!string.IsNullOrWhiteSpace(duplicate.Name)
                && collectionsByName.TryGetValue(duplicate.Name, out var named)
                && named.Id == duplicate.Id)
            {
                collectionsByName[duplicate.Name] = keep;
            }
        }

        collectionsByTmdbId[tmdbId] = keep;
        return duplicates.Count;
    }

    /// <summary>
    /// Applies the sort order and generated poster settings to a franchise collection.
    /// </summary>
    /// <param name="isNew">
    /// Whether the collection's TMDb poster may still be downloading (created, or metadata requested, in this sync).
    /// </param>
    private async Task ApplyDisplaySettingsAsync(
        BoxSet collection,
        IReadOnlyList<Movie> movies,
        PluginConfiguration config,
        bool isNew,
        CancellationToken cancellationToken)
    {
        // A locked collection is meant to stay exactly as it is.
        if (collection.IsLocked)
        {
            return;
        }

        var sortOrder = config.CollectionSortOrder;
        if (!string.IsNullOrWhiteSpace(sortOrder)
            && !string.Equals(sortOrder, "Unchanged", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(collection.DisplayOrder, sortOrder, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Setting the order of collection {Name} to {Order}", collection.Name, sortOrder);
            collection.DisplayOrder = sortOrder;
            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _posterGenerator.UpdatePosterAsync(collection, movies, config.GeneratedPosters, isNew, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to make a poster for collection {Name}", collection.Name);
        }
    }

    private static string? GetFranchiseId(BoxSet collection)
    {
        foreach (var key in new[] { TmdbCollectionKey, TmdbKey })
        {
            var id = collection.GetProviderId(key);
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return null;
    }

    private async Task<BoxSet> CreateCollectionAsync(string tmdbId, string name, IReadOnlyList<Movie> movies)
    {
        _logger.LogInformation("Creating collection {Name} with {Count} movies", name, movies.Count);

        return await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = name,
            // Jellyfin's box set metadata lookup reads only the "Tmdb" id; without it, it searches TMDb by name
            // and can pick a different franchise (e.g. "RED Collection" -> "Red Ryder Collection").
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TmdbCollectionKey] = tmdbId,
                [TmdbKey] = tmdbId
            },
            ItemIdList = movies.Select(m => m.Id.ToString("N", CultureInfo.InvariantCulture)).ToArray()
        }).ConfigureAwait(false);
    }

    private async Task<bool> UpdateCollectionAsync(
        BoxSet collection,
        string tmdbId,
        IReadOnlyList<Movie> movies,
        bool removeStale,
        bool overrideManualChanges,
        HashSet<Guid>? scopeMovieIds,
        SyncReport report)
    {
        var members = collection.GetLinkedChildrenInfos()
            .Select(info => info.Item2)
            .Where(item => item is not null)
            .ToList();
        var memberIds = members.Select(item => item.Id).ToHashSet();
        var movieIds = movies.Select(m => m.Id).ToHashSet();

        var toAdd = movies.Where(m => !memberIds.Contains(m.Id)).Select(m => m.Id).ToList();

        // Normally only remove movies that TMDb now places in a *different* franchise, so hand-added extras
        // (spin-offs, specials without TMDb collection data) are left untouched. Overriding removes them too,
        // except movies from libraries outside the library filter, which this sync can't see.
        var toRemove = overrideManualChanges
            ? members
                .Where(item => !movieIds.Contains(item.Id) && (scopeMovieIds is null || scopeMovieIds.Contains(item.Id)))
                .Select(item => item.Id)
                .ToList()
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
            report.Added.Add(new CollectionChange(collection.Id, collection.Name ?? tmdbId, Titles(movies.Where(m => toAdd.Contains(m.Id)))));
        }

        if (toRemove.Count > 0)
        {
            _logger.LogInformation("Removing {Count} movies from collection {Name}", toRemove.Count, collection.Name);
            await _collectionManager.RemoveFromCollectionAsync(collection.Id, toRemove).ConfigureAwait(false);
        }

        // Tag a name-matched (or taken-over) collection with the TMDb id so future syncs match it directly, and give
        // Jellyfin's metadata lookup the same id so it doesn't find a different franchise by name.
        var metadataId = collection.GetProviderId(TmdbKey);
        var wrongMetadata = !string.IsNullOrWhiteSpace(metadataId)
            && !string.Equals(metadataId, tmdbId, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(collection.GetProviderId(TmdbCollectionKey), tmdbId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(metadataId, tmdbId, StringComparison.OrdinalIgnoreCase))
        {
            collection.SetProviderId(TmdbCollectionKey, tmdbId);
            collection.SetProviderId(TmdbKey, tmdbId);
            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        }

        // The name, overview and images came from the wrong franchise: fetch them again from the right one.
        if (wrongMetadata)
        {
            _logger.LogInformation(
                "Collection {Name} had metadata from TMDb collection {WrongId}, refreshing from {TmdbId}",
                collection.Name,
                metadataId,
                tmdbId);
            _providerManager.QueueRefresh(
                collection.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = true,
                    ReplaceAllImages = true
                },
                RefreshPriority.High);
        }
        else if (string.IsNullOrWhiteSpace(metadataId)
            || string.IsNullOrWhiteSpace(collection.Overview)
            || !collection.HasImage(ImageType.Primary))
        {
            // A taken-over collection (hand-made, or tagged before it had a "Tmdb" id) never had TMDb metadata
            // fetched, so it has no overview or poster. Fill in only what's missing, keeping anything already set.
            _logger.LogInformation("Collection {Name} is missing TMDb metadata, fetching it", collection.Name);
            _metadataRequested.Add(collection.Id);
            _providerManager.QueueRefresh(
                collection.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh
                },
                RefreshPriority.High);
        }

        return toAdd.Count > 0 || toRemove.Count > 0;
    }

    /// <summary>
    /// Loads the non-virtual movies of the selected libraries (every library when none are selected).
    /// Querying each library by parent lets the server filter them, instead of looking up every movie's library.
    /// </summary>
    private List<Movie> GetMovies(HashSet<Guid> libraryIds)
    {
        if (libraryIds.Count == 0)
        {
            return _libraryManager.GetItemList(MovieQuery(Guid.Empty)).OfType<Movie>().ToList();
        }

        var movies = new List<Movie>();
        var seen = new HashSet<Guid>();
        foreach (var libraryId in libraryIds)
        {
            // A library that was removed since the settings were saved would make the query throw.
            if (_libraryManager.GetItemById(libraryId) is null)
            {
                _logger.LogWarning("Library {LibraryId} in the settings no longer exists, skipping it", libraryId);
                continue;
            }

            movies.AddRange(_libraryManager.GetItemList(MovieQuery(libraryId)).OfType<Movie>().Where(m => seen.Add(m.Id)));
        }

        return movies;
    }

    /// <summary>Movie titles for Discord, oldest first, e.g. "The Matrix (1999)".</summary>
    private static List<string> Titles(IEnumerable<Movie> movies)
        => movies
            .OrderBy(m => m.PremiereDate ?? DateTime.MaxValue)
            .Select(m => m.ProductionYear is int year ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", m.Name, year) : m.Name)
            .ToList();

    private static InternalItemsQuery MovieQuery(Guid parentId) => new()
    {
        IncludeItemTypes = [BaseItemKind.Movie],
        Recursive = true,
        IsVirtualItem = false,
        ParentId = parentId
    };

    /// <summary>
    /// Whether the sync may delete or trim this collection: always without a library filter, otherwise only when
    /// it has members and all of them are in the selected libraries.
    /// </summary>
    private static bool IsInScope(BoxSet collection, HashSet<Guid>? scopeMovieIds)
    {
        if (scopeMovieIds is null)
        {
            return true;
        }

        var memberIds = collection.GetLinkedChildrenInfos()
            .Select(info => info.Item2)
            .Where(item => item is not null)
            .Select(item => item.Id)
            .ToList();
        return memberIds.Count > 0 && memberIds.All(scopeMovieIds.Contains);
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

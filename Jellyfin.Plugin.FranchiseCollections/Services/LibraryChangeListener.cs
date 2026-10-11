using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FranchiseCollections.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FranchiseCollections.Services;

/// <summary>
/// Watches for movies being added or updated and queues a franchise sync once the library settles.
/// </summary>
public sealed class LibraryChangeListener : IHostedService, IDisposable
{
    // Wait for a quiet period so a large library scan triggers one sync, not hundreds,
    // and so TMDb metadata (which carries the franchise id) has time to be fetched.
    private static readonly TimeSpan Debounce = TimeSpan.FromMinutes(2);

    // Upper bound so a library that never goes quiet (e.g. a long metadata refresh) still gets synced.
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<LibraryChangeListener> _logger;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private DateTime? _firstPendingChangeUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeListener"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="logger">Logger.</param>
    public LibraryChangeListener(ILibraryManager libraryManager, ITaskManager taskManager, ILogger<LibraryChangeListener> logger)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
        _timer = new Timer(OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        // Only movies matter; ignoring BoxSets also stops our own collection edits from re-triggering a sync.
        if (e.Item is not Movie || Plugin.Instance?.Configuration.UpdateOnLibraryChange != true)
        {
            return;
        }

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            _firstPendingChangeUtc ??= now;

            var untilMax = _firstPendingChangeUtc.Value + MaxDelay - now;
            var due = untilMax < Debounce ? untilMax : Debounce;
            _timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimerElapsed(object? state)
    {
        lock (_lock)
        {
            _firstPendingChangeUtc = null;
        }

        // An exception escaping a timer callback would take down the whole server process.
        try
        {
            _logger.LogDebug("Movie library changed, queueing franchise collection sync");
            _taskManager.QueueScheduledTask<SyncFranchiseCollectionsTask>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue franchise collection sync");
        }
    }
}

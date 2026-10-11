using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FranchiseCollections.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.FranchiseCollections.Tasks;

/// <summary>
/// Scheduled task that syncs franchise collections. Shows up under Dashboard → Scheduled Tasks → Library.
/// </summary>
public class SyncFranchiseCollectionsTask : IScheduledTask
{
    private readonly FranchiseCollectionService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncFranchiseCollectionsTask"/> class.
    /// </summary>
    /// <param name="service">Franchise collection service.</param>
    public SyncFranchiseCollectionsTask(FranchiseCollectionService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "Sync franchise collections";

    /// <inheritdoc />
    public string Key => "FranchiseCollectionsSync";

    /// <inheritdoc />
    public string Description => "Creates and updates collections for movie franchises (e.g. The Matrix, Shrek).";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.SyncAsync(false, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
    }
}

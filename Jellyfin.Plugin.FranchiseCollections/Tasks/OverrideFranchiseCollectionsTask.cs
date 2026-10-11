using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FranchiseCollections.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.FranchiseCollections.Tasks;

/// <summary>
/// On-demand task that makes every franchise collection match TMDb exactly, overriding hand-made changes.
/// Started by the "Sync and override" button; it has no default triggers.
/// </summary>
public class OverrideFranchiseCollectionsTask : IScheduledTask
{
    private readonly FranchiseCollectionService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="OverrideFranchiseCollectionsTask"/> class.
    /// </summary>
    /// <param name="service">Franchise collection service.</param>
    public OverrideFranchiseCollectionsTask(FranchiseCollectionService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "Sync franchise collections (override)";

    /// <inheritdoc />
    public string Key => "FranchiseCollectionsOverride";

    /// <inheritdoc />
    public string Description => "Makes every franchise collection match TMDb exactly: removes hand-added items and takes over same-named collections.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.SyncAsync(true, progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}

using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FranchiseCollections.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the minimum number of movies you own from a franchise before a collection is created.
    /// </summary>
    public int MinimumMovies { get; set; } = 2;

    /// <summary>
    /// Gets or sets the ids of the libraries whose movies are used. Empty means every library.
    /// </summary>
    public string[] LibraryIds { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether collections are re-synced shortly after movies are added or updated.
    /// </summary>
    public bool UpdateOnLibraryChange { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether movies that no longer belong to a franchise are removed from its collection.
    /// </summary>
    public bool RemoveStaleMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets franchise names (one per line) that should never get a collection.
    /// </summary>
    public string ExcludedFranchises { get; set; } = string.Empty;
}

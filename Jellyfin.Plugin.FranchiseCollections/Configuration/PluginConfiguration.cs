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
    /// Gets or sets the order movies are shown in inside each franchise collection: a <c>BoxSet.DisplayOrder</c> value
    /// ("PremiereDate", "SortName" or "Default" for date added), or "Unchanged" to leave each collection as it is.
    /// </summary>
    public string CollectionSortOrder { get; set; } = "PremiereDate";

    /// <summary>
    /// Gets or sets when a poster is made from the collection's movie posters:
    /// "Off", "Missing" (only for collections without a poster) or "Always" (replace every franchise collection's poster).
    /// </summary>
    public string GeneratedPosters { get; set; } = "Off";

    /// <summary>
    /// Gets or sets franchise names (one per line) that should never get a collection.
    /// </summary>
    public string ExcludedFranchises { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Discord webhook URL. Empty turns Discord notifications off.
    /// </summary>
    public string DiscordWebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether Discord also hears about movies added to existing collections,
    /// not just new and deleted collections.
    /// </summary>
    public bool NotifyAddedMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets the address Discord can reach the server on (e.g. https://jellyfin.example.com).
    /// When set, notifications link to each collection. Empty leaves the links out.
    /// </summary>
    public string PublicServerUrl { get; set; } = string.Empty;
}

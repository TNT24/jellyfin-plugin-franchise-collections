using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.FranchiseCollections.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FranchiseCollections;

/// <summary>
/// Automatically creates and maintains collections for movie franchises (e.g. "The Matrix Collection").
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Franchise Collections";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7c1f3c2e-5b8a-4d3e-9f61-2a4b8e0d9c17");

    /// <inheritdoc />
    public override string Description => "Automatically creates and updates collections for movie franchises using TMDb collection data.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                // Also list the settings page in the dashboard's side menu.
                EnableInMainMenu = true,
                DisplayName = Name,
                MenuIcon = "collections_bookmark"
            }
        ];
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FranchiseCollections.Services;

/// <summary>
/// Makes a collection poster from the posters of the movies in it: a 2x2 grid in release order.
/// </summary>
public class CollectionPosterGenerator
{
    // Stored on the collection as "<hash of the posters used>:<saved image time>", so a poster made here can be told
    // apart from a TMDb or hand-picked one (any other image has a different time) and remade when the movies change.
    private const string MarkerKey = "FranchiseCollectionsPoster";

    private readonly IImageEncoder _imageEncoder;
    private readonly IProviderManager _providerManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<CollectionPosterGenerator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionPosterGenerator"/> class.
    /// </summary>
    /// <param name="imageEncoder">Image encoder.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public CollectionPosterGenerator(
        IImageEncoder imageEncoder,
        IProviderManager providerManager,
        IApplicationPaths applicationPaths,
        ILogger<CollectionPosterGenerator> logger)
    {
        _imageEncoder = imageEncoder;
        _providerManager = providerManager;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <summary>
    /// Makes or remakes the collection's poster when the setting asks for it.
    /// </summary>
    /// <param name="collection">The franchise collection.</param>
    /// <param name="movies">The franchise's movies.</param>
    /// <param name="mode">"Off", "Missing" (only when the collection has no poster) or "Always".</param>
    /// <param name="isNew">Whether the collection was created in this sync.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the update.</returns>
    public async Task UpdatePosterAsync(
        BoxSet collection,
        IReadOnlyList<Movie> movies,
        string? mode,
        bool isNew,
        CancellationToken cancellationToken)
    {
        var always = string.Equals(mode, "Always", StringComparison.OrdinalIgnoreCase);
        var missing = string.Equals(mode, "Missing", StringComparison.OrdinalIgnoreCase);
        if (!always && !missing)
        {
            return;
        }

        var posters = movies
            .OrderBy(m => m.PremiereDate ?? DateTime.MaxValue)
            .ThenBy(m => m.SortName, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.GetImageInfo(ImageType.Primary, 0))
            .Where(image => image is not null && image.IsLocalFile && File.Exists(image.Path))
            .Select(image => image!)
            .Take(4)
            .ToList();
        if (posters.Count == 0)
        {
            return;
        }

        var hash = HashPosters(posters);
        var current = collection.GetImageInfo(ImageType.Primary, 0);
        var currentHash = GetGeneratedHash(collection, current);

        bool make;
        if (currentHash is not null)
        {
            make = !string.Equals(currentHash, hash, StringComparison.Ordinal);
        }
        else if (always)
        {
            make = true;
        }
        else
        {
            // A new collection's TMDb poster is still downloading, so only fill in a missing poster on a later sync.
            make = current is null && !isNew;
        }

        if (!make)
        {
            return;
        }

        if (posters.Count > 1 && !_imageEncoder.SupportsImageCollageCreation)
        {
            _logger.LogWarning("This server can't create image collages, so no poster is made for {Name}", collection.Name);
            return;
        }

        _logger.LogInformation("Making a poster for collection {Name} from {Count} movie posters", collection.Name, posters.Count);
        await SavePosterAsync(collection, posters, cancellationToken).ConfigureAwait(false);

        var saved = collection.GetImageInfo(ImageType.Primary, 0);
        collection.SetProviderId(MarkerKey, string.Format(CultureInfo.InvariantCulture, "{0}:{1}", hash, ToSeconds(saved)));
        await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }

    private async Task SavePosterAsync(BoxSet collection, IReadOnlyList<ItemImageInfo> posters, CancellationToken cancellationToken)
    {
        // A single movie's poster is used as it is; a grid of four copies of it would look odd.
        if (posters.Count == 1)
        {
            var stream = File.OpenRead(posters[0].Path);
            await using (stream.ConfigureAwait(false))
            {
                await _providerManager.SaveImage(collection, stream, GetMimeType(posters[0].Path), ImageType.Primary, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        Directory.CreateDirectory(_applicationPaths.TempDirectory);
        var output = Path.Combine(_applicationPaths.TempDirectory, "franchise-poster-" + collection.Id.ToString("N", CultureInfo.InvariantCulture) + ".jpg");
        try
        {
            // Poster-shaped output gives poster-shaped cells. Jellyfin fills the 2x2 grid column by column.
            _imageEncoder.CreateImageCollage(
                new ImageCollageOptions
                {
                    InputPaths = ArrangeForGrid(posters.Select(p => p.Path).ToList()),
                    OutputPath = output,
                    Width = 1000,
                    Height = 1500
                },
                null);

            var stream = File.OpenRead(output);
            await using (stream.ConfigureAwait(false))
            {
                await _providerManager.SaveImage(collection, stream, "image/jpeg", ImageType.Primary, null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    /// <summary>
    /// Orders posters for Jellyfin's column-by-column fill (top-left, bottom-left, top-right, bottom-right)
    /// so they read left to right, top to bottom. Two or three posters are repeated diagonally to fill the grid.
    /// </summary>
    private static string[] ArrangeForGrid(IReadOnlyList<string> paths)
    {
        return paths.Count switch
        {
            2 => [paths[0], paths[1], paths[1], paths[0]],
            3 => [paths[0], paths[2], paths[1], paths[0]],
            _ => [paths[0], paths[2], paths[1], paths[3]]
        };
    }

    private static string? GetGeneratedHash(BoxSet collection, ItemImageInfo? current)
    {
        var marker = collection.GetProviderId(MarkerKey);
        if (current is null || string.IsNullOrEmpty(marker))
        {
            return null;
        }

        var parts = marker.Split(':');
        return parts.Length == 2 && string.Equals(parts[1], ToSeconds(current), StringComparison.Ordinal)
            ? parts[0]
            : null;
    }

    private static string HashPosters(IEnumerable<ItemImageInfo> posters)
    {
        var key = string.Join('|', posters.Select(p => p.Path + "@" + ToSeconds(p)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
    }

    // Whole seconds, so a re-read of the file's time with different precision still matches.
    private static string ToSeconds(ItemImageInfo? image)
    {
        return image is null
            ? "0"
            : (image.DateModified.Ticks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
    }

    private static string GetMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
    }
}

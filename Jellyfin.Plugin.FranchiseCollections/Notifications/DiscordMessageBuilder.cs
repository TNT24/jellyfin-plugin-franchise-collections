using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.FranchiseCollections.Notifications;

/// <summary>Movies that went into one collection during a sync.</summary>
/// <param name="CollectionId">The collection.</param>
/// <param name="Name">The collection's name.</param>
/// <param name="Movies">The movies, e.g. "The Matrix (1999)", oldest first.</param>
/// <param name="MovieIds">The same movies' ids, in the same order, for their posters.</param>
public sealed record CollectionChange(Guid CollectionId, string Name, IReadOnlyList<string> Movies, IReadOnlyList<Guid>? MovieIds = null);

/// <summary>The headliner's pictures, as <c>attachment://…</c> references.</summary>
/// <param name="Strip">Its movies' posters side by side, shown full width; null = none.</param>
/// <param name="Poster">The collection's own poster, shown beside the card; null = none.</param>
public sealed record DiscordPictures(string? Strip, string? Poster);

/// <summary>What one sync changed, for Discord.</summary>
public sealed class SyncReport
{
    /// <summary>Gets the collections created, with all their movies.</summary>
    public List<CollectionChange> Created { get; } = [];

    /// <summary>Gets the existing collections that got new movies, with just the new ones.</summary>
    public List<CollectionChange> Added { get; } = [];

    /// <summary>Gets the names of the collections deleted (only "Sync and override" deletes any).</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>Gets a value indicating whether there's anything to report.</summary>
    public bool IsEmpty => Created.Count == 0 && Added.Count == 0 && Deleted.Count == 0;
}

/// <summary>
/// Turns sync results into a Discord webhook message: the biggest change as a headliner card with its movies'
/// posters, the rest of the sync as a short list under it, and deleted collections last. No network access, so it
/// can be unit tested.
/// </summary>
public static class DiscordMessageBuilder
{
    // Discord allows 6000 characters across a message's embeds; the three cards share that.
    private const int MaxDescription = 1800;
    private const int MaxTitle = 250;
    private const int ListMoviesUpTo = 6;
    private const int HeadlinerMoviesUpTo = 10;

    private const int ColorCreated = 0x43B581;
    private const int ColorAdded = 0x00A4DC;
    private const int ColorDeleted = 0xF04747;

    /// <summary>
    /// The change the message leads with: the new collection with the most movies, or else the existing collection
    /// that got the most.
    /// </summary>
    /// <param name="report">What changed.</param>
    /// <param name="includeAdded">Whether movies added to existing collections are posted.</param>
    /// <returns>The change and whether it's a new collection, or null when there's none.</returns>
    public static (CollectionChange Change, bool IsNew)? Headliner(SyncReport report, bool includeAdded)
    {
        static CollectionChange? Biggest(IEnumerable<CollectionChange> changes)
            => changes
                .OrderByDescending(c => c.Movies.Count)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        if (Biggest(report.Created) is { } created)
        {
            return (created, true);
        }

        return includeAdded && Biggest(report.Added) is { } added ? (added, false) : null;
    }

    /// <summary>Builds the message for one sync.</summary>
    /// <param name="report">What changed.</param>
    /// <param name="includeAdded">Whether to list movies added to existing collections.</param>
    /// <param name="publicServerUrl">Server address for links, or empty.</param>
    /// <param name="pictures">The headliner's uploaded pictures; null = none.</param>
    /// <returns>Webhook payload, or null when there's nothing to say.</returns>
    public static JsonObject? Build(SyncReport report, bool includeAdded, string publicServerUrl, DiscordPictures? pictures = null)
    {
        var baseUrl = publicServerUrl.Trim().TrimEnd('/');
        var added = includeAdded ? report.Added : [];
        var headliner = Headliner(report, includeAdded);
        var embeds = new JsonArray();

        if (headliner is { } h)
        {
            embeds.Add(HeadlinerCard(h.Change, h.IsNew, baseUrl, pictures));
        }

        var others = new List<string>();
        others.AddRange(report.Created
            .Where(c => !ReferenceEquals(c, headliner?.Change))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"🆕 {Link(c.Name, baseUrl, c.CollectionId)} · new, {Plural(c.Movies.Count, "movie", "movies")}"));
        others.AddRange(added
            .Where(c => !ReferenceEquals(c, headliner?.Change))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"➕ {Link(c.Name, baseUrl, c.CollectionId)}: {MovieList(c.Movies)}"));
        if (others.Count > 0)
        {
            embeds.Add(Embed("Also in this sync", others, ColorAdded));
        }

        if (report.Deleted.Count > 0)
        {
            var lines = report.Deleted
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => "**" + Escape(n) + "**");
            embeds.Add(Embed("🗑️ Deleted by Sync and override", lines, ColorDeleted));
        }

        if (embeds.Count == 0)
        {
            return null;
        }

        return Message("🎬 **Franchise Collections:** " + Summary(report, added), embeds);
    }

    /// <summary>Builds a test message.</summary>
    /// <returns>Webhook payload.</returns>
    public static JsonObject BuildTest()
        => Message("✅ **Franchise Collections** can post to this channel. You'll get a message here when a sync creates or changes collections.", null);

    /// <summary>Lists movie titles, e.g. "A, B and 3 more".</summary>
    /// <param name="movies">The titles.</param>
    /// <returns>The list.</returns>
    internal static string MovieList(IReadOnlyList<string> movies)
    {
        var shown = movies.Take(ListMoviesUpTo).Select(Escape).ToList();
        if (movies.Count > shown.Count)
        {
            shown.Add((movies.Count - shown.Count).ToString(CultureInfo.InvariantCulture) + " more");
        }

        return JoinAnd(shown);
    }

    /// <summary>"2 new collections, 3 movies added to 2 collections and 1 collection deleted".</summary>
    private static string Summary(SyncReport report, IReadOnlyList<CollectionChange> added)
    {
        var parts = new List<string>();
        if (report.Created.Count > 0)
        {
            parts.Add(Plural(report.Created.Count, "new collection", "new collections"));
        }

        if (added.Count > 0)
        {
            parts.Add(Plural(added.Sum(c => c.Movies.Count), "movie", "movies") + " added to " + Plural(added.Count, "collection", "collections"));
        }

        if (report.Deleted.Count > 0)
        {
            parts.Add(Plural(report.Deleted.Count, "collection", "collections") + " deleted");
        }

        return JoinAnd(parts);
    }

    /// <summary>The headliner: the collection's name (linked), what happened, its movies, and the pictures.</summary>
    private static JsonObject HeadlinerCard(CollectionChange change, bool isNew, string baseUrl, DiscordPictures? pictures)
    {
        var movies = change.Movies.Take(HeadlinerMoviesUpTo).Select(m => "• " + Escape(m)).ToList();
        if (change.Movies.Count > movies.Count)
        {
            movies.Add($"…and {(change.Movies.Count - movies.Count).ToString(CultureInfo.InvariantCulture)} more");
        }

        var card = Embed(
            Truncate(Escape(change.Name), MaxTitle),
            movies,
            isNew ? ColorCreated : ColorAdded);
        card["author"] = new JsonObject
        {
            ["name"] = isNew
                ? "🆕 New collection · " + Plural(change.Movies.Count, "movie", "movies")
                : "➕ " + Plural(change.Movies.Count, "new movie", "new movies") + " in this collection",
        };

        if (baseUrl.Length > 0 && change.CollectionId != Guid.Empty)
        {
            card["url"] = DetailsUrl(baseUrl, change.CollectionId);
        }

        if (pictures?.Strip is { } strip)
        {
            card["image"] = new JsonObject { ["url"] = strip };
        }

        var thumbnail = pictures?.Poster ?? (baseUrl.Length > 0 && change.CollectionId != Guid.Empty
            ? $"{baseUrl}/Items/{change.CollectionId.ToString("N", CultureInfo.InvariantCulture)}/Images/Primary?maxHeight=300"
            : null);
        if (thumbnail is not null)
        {
            card["thumbnail"] = new JsonObject { ["url"] = thumbnail };
        }

        return card;
    }

    private static JsonObject Embed(string title, IEnumerable<string> lines, int color)
    {
        var all = lines.ToList();
        var kept = new List<string>();
        var length = 0;
        foreach (var line in all)
        {
            if (length + line.Length + 1 > MaxDescription)
            {
                break;
            }

            kept.Add(line);
            length += line.Length + 1;
        }

        if (kept.Count < all.Count)
        {
            kept.Add($"…and {(all.Count - kept.Count).ToString(CultureInfo.InvariantCulture)} more");
        }

        return new JsonObject
        {
            ["title"] = title,
            ["description"] = string.Join('\n', kept),
            ["color"] = color,
        };
    }

    private static string Link(string name, string baseUrl, Guid itemId)
    {
        var text = "**" + Escape(name) + "**";
        return baseUrl.Length > 0 && itemId != Guid.Empty
            ? $"[{text}]({DetailsUrl(baseUrl, itemId)})"
            : text;
    }

    private static string DetailsUrl(string baseUrl, Guid itemId)
        => $"{baseUrl}/web/#/details?id={itemId.ToString("N", CultureInfo.InvariantCulture)}";

    private static JsonObject Message(string content, JsonArray? embeds)
    {
        var message = new JsonObject
        {
            ["username"] = "Jellyfin Franchise Collections",
            ["content"] = content,

            // Titles could contain "@everyone"; never let a notification ping anyone.
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
        if (embeds is not null)
        {
            message["embeds"] = embeds;
        }

        return message;
    }

    private static string JoinAnd(List<string> parts)
        => parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    private static string Plural(int count, string one, string many)
        => count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);

    /// <summary>Cuts to <paramref name="length"/> characters plus "…", never leaving a lone backslash before the "…".</summary>
    private static string Truncate(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        var cut = text[..length];
        if ((cut.Length - cut.TrimEnd('\\').Length) % 2 == 1)
        {
            cut = cut[..^1];
        }

        return cut + "…";
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '*' or '_' or '`' or '~' or '|' or '\\' or '>' or '[' or ']')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}

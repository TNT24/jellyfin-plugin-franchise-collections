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
/// <param name="Movies">The movies, e.g. "The Matrix (1999)".</param>
public sealed record CollectionChange(Guid CollectionId, string Name, IReadOnlyList<string> Movies);

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
/// Turns sync results into a Discord webhook message. No network access, so it can be unit tested.
/// </summary>
public static class DiscordMessageBuilder
{
    // Discord allows 6000 characters across a message's embeds; three lists share that.
    private const int MaxDescription = 1800;
    private const int ListMoviesUpTo = 6;

    private const int ColorCreated = 0x43B581;
    private const int ColorAdded = 0x00A4DC;
    private const int ColorDeleted = 0xF04747;

    /// <summary>Builds the message for one sync.</summary>
    /// <param name="report">What changed.</param>
    /// <param name="includeAdded">Whether to list movies added to existing collections.</param>
    /// <param name="publicServerUrl">Server address for links, or empty.</param>
    /// <returns>Webhook payload, or null when there's nothing to say.</returns>
    public static JsonObject? Build(SyncReport report, bool includeAdded, string publicServerUrl)
    {
        var baseUrl = publicServerUrl.Trim().TrimEnd('/');
        var added = includeAdded ? report.Added : [];
        var embeds = new JsonArray();
        var parts = new List<string>();

        if (report.Created.Count > 0)
        {
            parts.Add(Plural(report.Created.Count, "new collection", "new collections"));
            var lines = report.Created
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => $"{Link(c.Name, baseUrl, c.CollectionId)} · {Plural(c.Movies.Count, "movie", "movies")}");
            embeds.Add(Embed("🆕 New collections", lines, ColorCreated, report.Created.Count == 1 ? Thumbnail(baseUrl, report.Created[0].CollectionId) : null));
        }

        if (added.Count > 0)
        {
            var movies = added.Sum(c => c.Movies.Count);
            parts.Add(Plural(movies, "movie", "movies") + " added to " + Plural(added.Count, "collection", "collections"));
            var lines = added
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => $"{Link(c.Name, baseUrl, c.CollectionId)}: {MovieList(c.Movies)}");
            embeds.Add(Embed("➕ Added to collections", lines, ColorAdded, null));
        }

        if (report.Deleted.Count > 0)
        {
            parts.Add(Plural(report.Deleted.Count, "collection", "collections") + " deleted");
            var lines = report.Deleted
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => "**" + Escape(n) + "**");
            embeds.Add(Embed("🗑️ Deleted by Sync and override", lines, ColorDeleted, null));
        }

        if (embeds.Count == 0)
        {
            return null;
        }

        return Message("🎬 **Franchise Collections:** " + JoinAnd(parts), embeds);
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

    private static JsonObject Embed(string title, IEnumerable<string> lines, int color, JsonObject? thumbnail)
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

        var embed = new JsonObject
        {
            ["title"] = title,
            ["description"] = string.Join('\n', kept),
            ["color"] = color,
        };
        if (thumbnail is not null)
        {
            embed["thumbnail"] = thumbnail;
        }

        return embed;
    }

    private static string Link(string name, string baseUrl, Guid itemId)
    {
        var text = "**" + Escape(name) + "**";
        return baseUrl.Length > 0 && itemId != Guid.Empty
            ? $"[{text}]({baseUrl}/web/#/details?id={itemId.ToString("N", CultureInfo.InvariantCulture)})"
            : text;
    }

    private static JsonObject? Thumbnail(string baseUrl, Guid itemId)
        => baseUrl.Length > 0 && itemId != Guid.Empty
            ? new JsonObject { ["url"] = $"{baseUrl}/Items/{itemId.ToString("N", CultureInfo.InvariantCulture)}/Images/Primary?maxHeight=300" }
            : null;

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

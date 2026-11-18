using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.FranchiseCollections.Notifications;

/// <summary>
/// Posts messages to a Discord webhook.
/// </summary>
public sealed class DiscordNotifier
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscordNotifier"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public DiscordNotifier(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Checks that a URL looks like a Discord webhook, so the server never posts somewhere unexpected.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>Whether it's a Discord webhook URL.</returns>
    public static bool IsWebhookUrl(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com")
            && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);

    /// <summary>Posts messages in order, waiting out Discord's rate limit when asked to.</summary>
    /// <param name="webhookUrl">The webhook URL.</param>
    /// <param name="messages">The payloads.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task SendAsync(string webhookUrl, IEnumerable<JsonObject> messages, CancellationToken cancellationToken)
        => SendAsync(webhookUrl, messages.Select(m => (m, (IReadOnlyList<DiscordAttachment>)Array.Empty<DiscordAttachment>())), cancellationToken);

    /// <summary>Posts messages in order, each with the images uploaded alongside it, waiting out Discord's rate limit when asked to.</summary>
    /// <param name="webhookUrl">The webhook URL.</param>
    /// <param name="messages">The payloads (whose embeds refer to their images as <c>attachment://name</c>) and their images.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SendAsync(string webhookUrl, IEnumerable<(JsonObject Message, IReadOnlyList<DiscordAttachment> Attachments)> messages, CancellationToken cancellationToken)
    {
        if (!IsWebhookUrl(webhookUrl))
        {
            throw new ArgumentException("That isn't a Discord webhook URL. It should start with https://discord.com/api/webhooks/");
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        foreach (var (message, attachments) in messages)
        {
            for (var attempt = 0; ; attempt++)
            {
                // Built afresh for each attempt: a sent request body can't be sent again.
                using var content = BuildContent(message, attachments);
                using var response = await client.PostAsync(webhookUrl.Trim(), content, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
                {
                    await Task.Delay(await RetryAfterAsync(response, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new HttpRequestException($"Discord answered {(int)response.StatusCode}: {Truncate(error, 300)}", null, response.StatusCode);
                }

                break;
            }
        }
    }

    /// <summary>
    /// The request body: plain JSON, or with images a multipart form holding the JSON as <c>payload_json</c> (with an
    /// <c>attachments</c> list naming each file) and each image as <c>files[n]</c>.
    /// </summary>
    /// <param name="message">The payload.</param>
    /// <param name="attachments">Its images.</param>
    /// <returns>The body.</returns>
    internal static HttpContent BuildContent(JsonObject message, IReadOnlyList<DiscordAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
        }

        var payload = (JsonObject)message.DeepClone();
        payload["attachments"] = new JsonArray(attachments
            .Select((a, i) => (JsonNode)new JsonObject { ["id"] = i, ["filename"] = a.FileName })
            .ToArray());

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), "payload_json");
        for (var i = 0; i < attachments.Count; i++)
        {
            var file = new ByteArrayContent(attachments[i].Data);
            file.Headers.ContentType = new MediaTypeHeaderValue(attachments[i].MimeType);
            form.Add(file, $"files[{i.ToString(CultureInfo.InvariantCulture)}]", attachments[i].FileName);
        }

        return form;
    }

    private static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan header)
        {
            return header;
        }

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("retry_after", out var seconds) && seconds.TryGetDouble(out var s))
            {
                return TimeSpan.FromSeconds(Math.Clamp(s, 0.5, 60));
            }
        }
        catch (JsonException)
        {
        }

        return TimeSpan.FromSeconds(5);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}

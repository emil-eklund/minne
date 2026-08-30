using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MailSearch.Config;

namespace MailSearch.Mail;

/// <summary>Reads mail through Microsoft Graph using delta queries for incremental sync.</summary>
public sealed class GraphMailSource : IMailSource
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string Select =
        "id,internetMessageId,conversationId,subject,from,toRecipients,ccRecipients,receivedDateTime,hasAttachments,webLink,body";

    private readonly GraphAuth _auth;
    private readonly GraphConfig _config;
    private readonly HttpClient _http;

    public GraphMailSource(GraphAuth auth, GraphConfig config, HttpClient? http = null)
    {
        _auth = auth;
        _config = config;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    public async IAsyncEnumerable<MailChange> GetChangesAsync(
        string folder, string? state, Action<string> onNewState, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = state ?? $"{GraphBase}/me/mailFolders/{Uri.EscapeDataString(folder)}/messages/delta?$select={Select}";
        var isInitialSync = state is null;
        var count = 0;

        while (url is not null)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = await GetJsonAsync(url, ct);
            var root = doc.RootElement;

            if (root.TryGetProperty("value", out var values))
            {
                foreach (var item in values.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString()!;
                    if (item.TryGetProperty("@removed", out _))
                    {
                        yield return new MailChange(id, null);
                        continue;
                    }
                    yield return new MailChange(id, Parse(item, folder));
                    count++;
                }
            }

            if (root.TryGetProperty("@odata.deltaLink", out var delta))
            {
                onNewState(delta.GetString()!);
                url = null;
            }
            else if (root.TryGetProperty("@odata.nextLink", out var next))
            {
                url = next.GetString();
                // Persist progress so an interrupted initial sync resumes from this page instead of restarting.
                onNewState(url!);
                if (isInitialSync && _config.MaxMessagesPerFolder > 0 && count >= _config.MaxMessagesPerFolder)
                    url = null;
            }
            else url = null;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await _auth.GetAccessTokenAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");
            request.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={_config.PageSize}");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var retryable = response.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            if (retryable && attempt < 6)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                Console.Error.WriteLine($"  throttled by Graph, waiting {wait.TotalSeconds:0}s...");
                await Task.Delay(wait, ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Graph returned {(int)response.StatusCode} for {url}\n{body}");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
    }

    internal static MailMessage Parse(JsonElement item, string folder)
    {
        return new MailMessage
        {
            Id = item.GetProperty("id").GetString()!,
            InternetMessageId = GetString(item, "internetMessageId"),
            ConversationId = GetString(item, "conversationId"),
            Folder = folder,
            Subject = GetString(item, "subject") ?? "",
            From = item.TryGetProperty("from", out var from) ? ParseAddress(from) : null,
            To = ParseAddresses(item, "toRecipients"),
            Cc = ParseAddresses(item, "ccRecipients"),
            Received = GetString(item, "receivedDateTime") is { } received
                ? DateTimeOffset.Parse(received, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                : DateTimeOffset.MinValue,
            HasAttachments = item.TryGetProperty("hasAttachments", out var ha) && ha.ValueKind == JsonValueKind.True,
            WebLink = GetString(item, "webLink"),
            Body = item.TryGetProperty("body", out var body) ? GetString(body, "content") ?? "" : "",
        };
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static MailAddress? ParseAddress(JsonElement recipient)
    {
        if (!recipient.TryGetProperty("emailAddress", out var ea)) return null;
        var address = GetString(ea, "address");
        return address is null ? null : new MailAddress(GetString(ea, "name"), address);
    }

    private static IReadOnlyList<MailAddress> ParseAddresses(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<MailAddress>();
        foreach (var r in arr.EnumerateArray())
            if (ParseAddress(r) is { } a) list.Add(a);
        return list;
    }
}

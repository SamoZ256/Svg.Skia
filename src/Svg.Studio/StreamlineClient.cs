// Copyright (c) Wiesław Šoltés. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for details.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Svg.Studio;

/// <summary>Streamline's public API: searching its icons, browsing its families, and downloading an icon.</summary>
/// <remarks>
/// Any answer but a success is thrown as a <see cref="StreamlineException"/>, and a success shaped
/// unlike the reference as a <see cref="JsonException"/>.
/// </remarks>
public sealed class StreamlineClient
{
    public const string BaseAddress = "https://public-api.streamlinehq.com";

    /// <summary>Where the key is kept in the <see cref="Keychain"/>.</summary>
    public const string KeyService = "Svg.Studio";

    public const string KeyAccount = "Streamline API key";

    private static readonly HttpClient s_shared = new();

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly string _key;
    private readonly HttpClient _http;

    /// <param name="http">Given by a test to answer in place of the network.</param>
    public StreamlineClient(string key, HttpClient? http = null)
    {
        _key = key;
        _http = http ?? s_shared;
    }

    public Task<StreamlinePage<StreamlineIcon>> Search(
        string query,
        string productType = "icons",
        int offset = 0,
        int limit = 50,
        string? style = null,
        string? productTier = null,
        CancellationToken cancellation = default) =>
        Page<StreamlineIcon>(
            Path("v1/search/global",
                ("productType", productType),
                ("query", query),
                ("offset", Number(offset)),
                ("limit", Number(limit)),
                ("style", style),
                ("productTier", productTier)),
            "results",
            cancellation);

    /// <summary>One icon's details; <see cref="StreamlineIcon.Svg"/> is filled in only for a Pro account.</summary>
    public async Task<StreamlineIcon> Icon(string hash, CancellationToken cancellation = default) =>
        JsonSerializer.Deserialize<StreamlineIcon>(await Text(Path($"v1/icons/{Escaped(hash)}"), cancellation), s_json)!;

    /// <summary>The icon as SVG, with <paramref name="colors"/> in place of its palette where given.</summary>
    /// <remarks>
    /// <c>responsive</c> and <c>strokeToFill</c> are required by the API: responsive drops the fixed
    /// width and height, and strokes stay strokes so a template can still bind them.
    /// </remarks>
    public Task<string> DownloadSvg(
        string hash,
        int size,
        double? strokeWidth = null,
        IReadOnlyList<string>? colors = null,
        CancellationToken cancellation = default) =>
        Text(
            Path($"v1/icons/{Escaped(hash)}/download/svg",
                ("size", Number(size)),
                ("responsive", "true"),
                ("strokeToFill", "false"),
                ("strokeWidth", strokeWidth is { } width ? width.ToString(CultureInfo.InvariantCulture) : null),
                ("colors", colors is { Count: > 0 } ? string.Join(",", colors) : null)),
            cancellation);

    public async Task<IReadOnlyList<StreamlineFamilyGroup>> FamilyGroups(CancellationToken cancellation = default) =>
        (await Page<StreamlineFamilyGroup>(Path("v1/family-groups"), null, cancellation)).Items;

    public Task<StreamlinePage<StreamlineFamily>> Families(
        string groupHash,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellation = default) =>
        Page<StreamlineFamily>(
            Path($"v1/family-groups/{Escaped(groupHash)}/families", ("offset", Number(offset)), ("limit", Number(limit))),
            null,
            cancellation);

    public Task<StreamlinePage<StreamlineIcon>> FamilyIcons(
        string familyHash,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellation = default) =>
        Page<StreamlineIcon>(
            Path($"v1/families/{Escaped(familyHash)}/icons", ("offset", Number(offset)), ("limit", Number(limit))),
            "icons",
            cancellation);

    /// <summary>An icon's preview image, from <see cref="StreamlineIcon.ImagePreviewUrl"/>.</summary>
    /// <remarks>Sent without the key: the preview lives on Streamline's CDN, a different host from the API.</remarks>
    public async Task<byte[]> Thumbnail(string url, CancellationToken cancellation = default)
    {
        using var response = await _http.GetAsync(url, cancellation);

        await Ensure(response, cancellation);

        return await response.Content.ReadAsByteArrayAsync(cancellation);
    }

    /// <param name="list">The answer's key for the list, where the reference names one.</param>
    /// <remarks>
    /// The family endpoints document one object where a list must be, so without a key the answer is
    /// read as a bare list, that one object, or its only array. Anything else is thrown: an empty page
    /// would pass a wrong guess about the answer off as an empty catalogue.
    /// </remarks>
    private async Task<StreamlinePage<T>> Page<T>(string path, string? list, CancellationToken cancellation)
    {
        using var json = JsonDocument.Parse(await Text(path, cancellation));

        var root = json.RootElement;
        var items = Items<T>(root, list)
            ?? throw new JsonException($"Streamline's answer to {path} holds no list where one was expected.");

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("pagination", out var paging))
        {
            var pagination = paging.Deserialize<Pagination>(s_json)!;

            return new StreamlinePage<T>(
                items,
                pagination.Total,
                pagination.HasMore,
                pagination.NextOffset ?? pagination.Offset + items.Count);
        }

        return new StreamlinePage<T>(items, items.Count, false, items.Count);
    }

    private static List<T>? Items<T>(JsonElement root, string? list)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.Deserialize<List<T>>(s_json);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (list is not null)
        {
            return root.TryGetProperty(list, out var named) && named.ValueKind == JsonValueKind.Array
                ? named.Deserialize<List<T>>(s_json)
                : null;
        }

        if (root.TryGetProperty("hash", out _))
        {
            return new List<T> { root.Deserialize<T>(s_json)! };
        }

        var arrays = root.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Array).ToList();

        return arrays.Count == 1 ? arrays[0].Value.Deserialize<List<T>>(s_json) : null;
    }

    private async Task<string> Text(string path, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        request.Headers.Add("x-api-key", _key);

        using var response = await _http.SendAsync(request, cancellation);

        await Ensure(response, cancellation);

        return await response.Content.ReadAsStringAsync(cancellation);
    }

    private static async Task Ensure(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellation);
        var message = response.ReasonPhrase ?? response.StatusCode.ToString();

        try
        {
            using var json = JsonDocument.Parse(body);

            if (json.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("message", out var said)
                && said.ValueKind == JsonValueKind.String)
            {
                message = said.GetString()!;
            }
        }
        catch (JsonException)
        {
            // A proxy's HTML page or an empty body: the status line is all there is to say.
        }

        throw new StreamlineException(response.StatusCode, message, ResetsAt(response));
    }

    /// <remarks>
    /// The API documents no header for when a limit lifts, so this takes the standard
    /// <c>Retry-After</c> and the common <c>X-RateLimit-Reset</c> (Unix seconds or a date) if either comes.
    /// </remarks>
    private static DateTimeOffset? ResetsAt(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retry)
        {
            return retry.Date ?? (retry.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null);
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && values.FirstOrDefault() is { } reset)
        {
            if (long.TryParse(reset, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            if (DateTimeOffset.TryParse(reset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                return date;
            }
        }

        return null;
    }

    private static string Path(string path, params (string Name, string? Value)[] query)
    {
        var given = query.Where(pair => pair.Value is not null).Select(pair => $"{pair.Name}={Escaped(pair.Value!)}").ToList();

        return $"{BaseAddress}/{path}{(given.Count > 0 ? "?" + string.Join("&", given) : "")}";
    }

    private static string Escaped(string text) => Uri.EscapeDataString(text);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record Pagination(int Total, bool HasMore, int Offset, int? NextOffset);
}

/// <summary>One page of a listing, and where the next one starts.</summary>
public sealed record StreamlinePage<T>(IReadOnlyList<T> Items, int Total, bool HasMore, int NextOffset);

/// <summary>An icon as a search, a family or its own details describe it.</summary>
/// <remarks>
/// One record for all three: <see cref="Colors"/> and <see cref="Svg"/> come only with details and a
/// family's listing, <see cref="HasPremiumAccess"/> only with a search.
/// </remarks>
public sealed record StreamlineIcon(
    string Hash,
    string Name,
    string? ImagePreviewUrl,
    string? FamilySlug,
    string? FamilyName,
    bool IsFree,
    bool HasPremiumAccess = false,
    IReadOnlyList<string>? Colors = null,
    string? Svg = null);

public sealed record StreamlineFamilyGroup(string Hash, string Slug, string Name, string? ProductType);

public sealed record StreamlineFamily(string Hash, string Slug, string Name, bool IsFree, int IconCount);

/// <summary>What Streamline answered in place of a success: 401 for a missing or wrong key, 429 for a spent limit.</summary>
public sealed class StreamlineException(HttpStatusCode status, string message, DateTimeOffset? resetsAt = null)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>When a spent limit lifts, where the answer said.</summary>
    public DateTimeOffset? ResetsAt { get; } = resetsAt;
}

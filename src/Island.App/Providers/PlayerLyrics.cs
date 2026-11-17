using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Island.Core.Events;

namespace Island.App.Providers;

internal static class PlayerLyrics
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(6),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };

    public static async Task<IReadOnlyList<LyricLine>> FindAsync(string player, string title, string artist, TimeSpan duration, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return [];
        try
        {
            return player switch
            {
                "QQ 音乐" => await FindQqAsync(title, artist, duration, token),
                "酷狗音乐" => await FindKugouAsync(title, artist, duration, token),
                _ => []
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return []; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException) { return []; }
    }

    private static async Task<IReadOnlyList<LyricLine>> FindQqAsync(string title, string artist, TimeSpan duration, CancellationToken token)
    {
        var query = Uri.EscapeDataString(title + " " + artist);
        using var search = await GetAsync($"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=8&w={query}", true, token);
        if (search is null || Property(Property(Property(search.RootElement, "data"), "song"), "list") is not { ValueKind: JsonValueKind.Array } tracks)
            return [];
        var candidates = tracks.EnumerateArray().Where(track => Matches(track, "songname", title, artist,
                string.Join("/", Entries(Property(track, "singer")).Select(singer => Text(singer, "name"))), "interval", duration))
            .Select(track => Text(track, "songmid")).Where(id => id.Length is > 0 and <= 32 && id.All(char.IsAsciiLetterOrDigit))
            .Distinct().Take(2).ToArray();
        if (candidates.Length != 1) return [];
        using var result = await GetAsync($"https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?songmid={candidates[0]}&format=json&nobase64=1", true, token);
        return result is null ? [] : LocalLyrics.Parse(WebUtility.HtmlDecode(Text(result.RootElement, "lyric")));
    }

    private static async Task<IReadOnlyList<LyricLine>> FindKugouAsync(string title, string artist, TimeSpan duration, CancellationToken token)
    {
        var query = Uri.EscapeDataString(artist + " - " + title);
        var milliseconds = Math.Max(0, (long)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        using var search = await GetAsync($"https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={query}&duration={milliseconds}", false, token);
        if (search is null) return [];
        foreach (var item in Entries(Property(search.RootElement, "candidates"))
                     .Where(item => Matches(item, "song", title, artist, Text(item, "singer"), "duration", duration, 1000)).Take(2))
        {
            var id = Text(item, "id");
            var access = Text(item, "accesskey");
            if (id.Length == 0 || access.Length == 0) continue;
            using var result = await GetAsync("https://lyrics.kugou.com/download?ver=1&client=pc&fmt=lrc&charset=utf8&id="
                + Uri.EscapeDataString(id) + "&accesskey=" + Uri.EscapeDataString(access), false, token);
            if (result is null || Text(result.RootElement, "content") is not { Length: > 0 } content) continue;
            var lyrics = LocalLyrics.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(content)));
            if (lyrics.Count > 0) return lyrics;
        }
        return [];
    }

    private static bool Matches(JsonElement item, string nameField, string title, string artist, string actualArtist,
        string durationField, TimeSpan duration, double unitsPerSecond = 1)
    {
        if (LocalLyrics.Normalize(Text(item, nameField)) != LocalLyrics.Normalize(title) ||
            LocalLyrics.NormalizeArtists(actualArtist) != LocalLyrics.NormalizeArtists(artist)) return false;
        var reported = Property(item, durationField);
        return duration <= TimeSpan.Zero || (reported.ValueKind == JsonValueKind.Number &&
            reported.TryGetDouble(out double length) && Math.Abs(length / unitsPerSecond - duration.TotalSeconds) <= 3);
    }

    private static async Task<JsonDocument?> GetAsync(string url, bool qq, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 WindowsIsland");
        if (qq) request.Headers.Referrer = new Uri("https://y.qq.com/");
        using var response = await Http.SendAsync(request, token);
        return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token)) : null;
    }

    private static JsonElement Property(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var property) ? property : default;

    private static string Text(JsonElement value, string key) => Property(value, key).ToString();

    private static IEnumerable<JsonElement> Entries(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray() : [];
}

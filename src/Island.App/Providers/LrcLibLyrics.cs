using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Island.Core.Events;

namespace Island.App.Providers;

internal sealed class LrcLibLyrics
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(6),
        MaxResponseContentBufferSize = MaxBytes
    };
    private readonly ConcurrentDictionary<(string, string, string, double), IReadOnlyList<LyricLine>> _cache = new();

    public async Task<IReadOnlyList<LyricLine>> FindAsync(
        string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return [];
        var key = (title, artist, album, Math.Round(duration.TotalSeconds));
        if (_cache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var route = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.UserAgent.ParseAdd("WindowsIsland (https://github.com/Myjianglai/WindowsIsland)");
            using var response = await Http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return [];
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
            var lines = Match(document.RootElement, title, artist, album, duration);
            if (lines.Count > 0)
            {
                if (_cache.Count >= 80) _cache.Clear();
                _cache.TryAdd(key, lines);
            }
            return lines;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return []; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException) { return []; }
    }

    internal static IReadOnlyList<LyricLine> Match(JsonElement matches, string title, string artist, string album, TimeSpan duration)
    {
        if (matches.ValueKind != JsonValueKind.Array) return [];
        var titleKey = LocalLyrics.Normalize(title);
        var artistKey = LocalLyrics.NormalizeArtists(artist);
        var albumKey = LocalLyrics.Normalize(album);
        var candidates = matches.EnumerateArray().Where(track =>
                LocalLyrics.Normalize(ReadText(track, "trackName")) == titleKey &&
                LocalLyrics.NormalizeArtists(ReadText(track, "artistName")) == artistKey &&
                !string.IsNullOrWhiteSpace(ReadText(track, "syncedLyrics")))
            .Select(track => (Text: ReadText(track, "syncedLyrics"),
                AlbumMatch: albumKey.Length > 0 && LocalLyrics.Normalize(ReadText(track, "albumName")) == albumKey,
                Delta: duration > TimeSpan.Zero && track.TryGetProperty("duration", out var value) &&
                    value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double seconds)
                    ? Math.Abs(seconds - duration.TotalSeconds) : duration > TimeSpan.Zero ? double.PositiveInfinity : 0))
            .Where(track => track.Delta <= 3)
            .OrderByDescending(track => track.AlbumMatch).ThenBy(track => track.Delta).ToArray();
        if (candidates.Length == 0) return [];
        var best = candidates[0];
        if (candidates.Skip(1).Any(track => track.AlbumMatch == best.AlbumMatch &&
            Math.Abs(track.Delta - best.Delta) < .5 && track.Text != best.Text))
            return [];
        return LocalLyrics.Parse(best.Text);
    }

    private static string ReadText(JsonElement track, string key) => track.ValueKind == JsonValueKind.Object &&
        track.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

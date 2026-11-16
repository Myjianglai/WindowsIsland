using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Island.Core.Events;

namespace Island.App.Providers;

internal static partial class LocalLyrics
{
    public static IReadOnlyList<LyricLine> Find(string folder, string title, string artist, string? subtitle, string? player)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            return [];
        }
        try
        {
            if (player == "网易云音乐")
            {
                var trackId = GetNeteaseId(subtitle) ?? FindMappedId(folder, title, artist);
                if (trackId is not null && Parse(ReadSmallFile(Path.Combine(folder, trackId + ".lrc"))) is { Count: > 0 } exact)
                {
                    return exact;
                }
            }
            string titleKey = Normalize(title);
            string artists = NormalizeArtists(artist);
            var files = Directory.EnumerateFiles(folder, "*.lrc").Take(10000).ToArray();
            foreach (var path in files)
            {
                var name = Path.GetFileNameWithoutExtension(path).Split(" - ", 2, StringSplitOptions.TrimEntries);
                if (name.Length == 2 && ((Normalize(name[0]) == titleKey && NormalizeArtists(name[1]) == artists)
                    || (Normalize(name[1]) == titleKey && NormalizeArtists(name[0]) == artists)))
                {
                    var matched = Parse(ReadSmallFile(path));
                    if (matched.Count > 0)
                    {
                        return matched;
                    }
                }
            }
            // 标题独立匹配只在文件名完全相等时启用，保留 Live/Remix 等版本标记。
            var titleOnly = files.FirstOrDefault(path => Normalize(Path.GetFileNameWithoutExtension(path)) == titleKey);
            return titleOnly is null ? [] : Parse(ReadSmallFile(titleOnly));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static string? FindMappedId(string folder, string title, string artist)
    {
        foreach (var line in ReadSmallFile(Path.Combine(folder, "netease-index.tsv")).Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length == 3 && fields[0].Length is > 0 and <= 20 && fields[0].All(char.IsAsciiDigit)
                && Normalize(fields[1]) == Normalize(title) && NormalizeArtists(fields[2]) == NormalizeArtists(artist))
            {
                return fields[0];
            }
        }
        return null;
    }

    private static string ReadSmallFile(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length <= 1024 * 1024 ? File.ReadAllText(path) : "";
    }

    internal static IReadOnlyList<LyricLine> Parse(string content)
    {
        var offsetMatch = Offset().Match(content);
        int offset = offsetMatch.Success && int.TryParse(offsetMatch.Groups[1].Value, out int value) ? value : 0;
        var lyrics = new List<LyricLine>();
        foreach (var line in content.Split('\n'))
        {
            var matches = Stamp().Matches(line);
            if (matches.Count == 0)
            {
                continue;
            }
            var last = matches[^1];
            var text = line[(last.Index + last.Length)..].Trim();
            if (text.Length == 0)
            {
                continue;
            }
            foreach (Match match in matches)
            {
                double minutes = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                double seconds = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = match.Groups[3].Value;
                double milliseconds = fraction.Length == 0 ? 0 : double.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);
                lyrics.Add(new LyricLine(TimeSpan.FromMilliseconds(Math.Max(0, minutes * 60000 + seconds * 1000 + milliseconds + offset)), text));
            }
        }
        return lyrics.GroupBy(line => line.At).OrderBy(group => group.Key)
            .Select(group => new LyricLine(group.Key, string.Join(" / ", group.Select(line => line.Text).Distinct()))).ToArray();
    }

    public static int IndexAt(IReadOnlyList<LyricLine> lyrics, TimeSpan at)
    {
        int low = 0, high = lyrics.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (lyrics[middle].At <= at) low = middle + 1;
            else high = middle;
        }
        return low - 1;
    }

    internal static string? GetNeteaseId(string? metadata) => NeteaseId().Match(metadata ?? "") is { Success: true } match
        ? match.Groups[1].Value : null;

    internal static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToUpperInvariant();
    internal static string NormalizeArtists(string text) => string.Join("|",
        ArtistSplit().Split(text).Select(Normalize).Where(value => value.Length > 0).Order(StringComparer.Ordinal));

    [GeneratedRegex(@"\[(\d{1,5}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();

    [GeneratedRegex(@"(?:music\.163\.com/[^\s]*?song\?id=|orpheus://song/)([0-9]{1,20})\b", RegexOptions.IgnoreCase)]
    private static partial Regex NeteaseId();

    [GeneratedRegex(@"\s*(?:[,，、/&]|\b(?:feat\.?|ft\.?)\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSplit();
}

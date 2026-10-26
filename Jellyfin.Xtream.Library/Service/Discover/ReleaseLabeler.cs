// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Library.Service.Discover;

/// <summary>
/// Turns releases into the lines shown in the client's version picker and puts them in the order a viewer wants:
/// healthy torrents first, then the best picture that is not absurdly large.
/// </summary>
public static class ReleaseLabeler
{
    /// <summary>
    /// Filters the releases a title is offered with and orders them best first.
    /// </summary>
    /// <param name="releases">All releases found.</param>
    /// <param name="settings">The Discover settings (seeder floor, size cap, count).</param>
    /// <returns>The releases to offer.</returns>
    public static List<ReleaseOption> Select(IEnumerable<ReleaseOption> releases, DiscoverSettings settings)
    {
        var maxBytes = settings.MaxSizeGb > 0 ? settings.MaxSizeGb * 1024L * 1024L * 1024L : long.MaxValue;
        return releases
            .Where(r => r.Seeders >= settings.MinSeeders && r.SizeBytes <= maxBytes && !string.IsNullOrEmpty(r.TorrentLink))
            .OrderByDescending(Score)
            .Take(settings.MaxReleases)
            .ToList();
    }

    /// <summary>
    /// Scores a release for ordering. A 1080p or 2160p release that people are seeding beats a bigger or thinner one.
    /// </summary>
    /// <param name="release">The release.</param>
    /// <returns>The score, higher is better.</returns>
    public static double Score(ReleaseOption release)
    {
        // Resolution matters most, but only up to the point a typical client plays it: 1080p is the sweet spot.
        double score = release.Resolution switch
        {
            >= 2160 => 700,
            >= 1080 => 1000,
            >= 720 => 500,
            _ => 100,
        };

        // Seeders decide whether it starts quickly; the first few count for most.
        score += Math.Min(release.Seeders, 200) * 2;
        score += Math.Log10(1 + release.Seeders) * 100;

        // Remuxes and huge files are slow to stream and need transcoding; nudge them down.
        var sizeGb = release.SizeBytes / (1024d * 1024d * 1024d);
        if (sizeGb > 40)
        {
            score -= 300;
        }
        else if (sizeGb > 20)
        {
            score -= 100;
        }

        if (release.Rejected)
        {
            score -= 150;
        }

        if (release.Languages.Any(l => string.Equals(l, "English", StringComparison.OrdinalIgnoreCase)))
        {
            score += 150;
        }

        return score;
    }

    /// <summary>
    /// Builds the line the version picker shows, for example "1080p WEB-DL · 6.2 GB · 311 seeders · EZTV".
    /// </summary>
    /// <param name="release">The release.</param>
    /// <returns>The label.</returns>
    public static string Label(ReleaseOption release)
    {
        var quality = !string.IsNullOrEmpty(release.Quality) ? release.Quality : InferQuality(release.Title);
        var size = FormatSize(release.SizeBytes);
        var parts = new List<string> { quality, size, string.Create(CultureInfo.InvariantCulture, $"{release.Seeders} seeders") };
        if (release.IsSeasonPack)
        {
            parts.Insert(1, "season pack");
        }

        if (!string.IsNullOrEmpty(release.Indexer))
        {
            parts.Add(release.Indexer);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Formats a byte count as gigabytes or megabytes.
    /// </summary>
    /// <param name="bytes">The size.</param>
    /// <returns>A short size string.</returns>
    public static string FormatSize(long bytes)
    {
        var gb = bytes / (1024d * 1024d * 1024d);
        return gb >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{gb:0.#} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):0} MB");
    }

    /// <summary>
    /// Guesses a quality label from a release title when Radarr or Sonarr gave none.
    /// </summary>
    /// <param name="title">The release title.</param>
    /// <returns>A label such as "1080p BluRay" or "unknown".</returns>
    public static string InferQuality(string title)
    {
        var resolution = Regex.Match(title, @"\b(2160p|4k|1080p|720p|480p)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var source = Regex.Match(title, @"\b(remux|blu-?ray|web-?dl|webrip|hdtv|dvdrip|hdrip|cam)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var parts = new List<string>();
        if (resolution.Success)
        {
            parts.Add(string.Equals(resolution.Value, "4k", StringComparison.OrdinalIgnoreCase) ? "2160p" : resolution.Value.ToLowerInvariant());
        }

        if (source.Success)
        {
            parts.Add(source.Value);
        }

        return parts.Count > 0 ? string.Join(" ", parts) : "unknown";
    }

    /// <summary>
    /// Picks the release to play when the viewer did not choose one: the best-scored.
    /// </summary>
    /// <param name="releases">The releases on offer, already ordered.</param>
    /// <returns>The best release, or null when there are none.</returns>
    public static ReleaseOption? PickBest(IReadOnlyList<ReleaseOption> releases)
    {
        return releases.Count > 0 ? releases[0] : null;
    }

    /// <summary>
    /// Finds the video file for an episode in a torrent's file list, or the largest video for a movie.
    /// </summary>
    /// <param name="files">The torrent's files as (index, path, size).</param>
    /// <param name="season">The wanted season, 0 for a movie.</param>
    /// <param name="episode">The wanted episode, 0 for a movie.</param>
    /// <returns>The file's index, or -1 when none fits.</returns>
    public static int PickFile(IReadOnlyList<(int Index, string Path, long Size)> files, int season, int episode)
    {
        var videos = files.Where(f => IsVideo(f.Path) && !IsSample(f.Path)).ToList();
        if (videos.Count == 0)
        {
            return -1;
        }

        if (season <= 0 || episode <= 0)
        {
            return videos.OrderByDescending(f => f.Size).First().Index;
        }

        var wanted = videos.Where(f => MatchesEpisode(f.Path, season, episode)).OrderByDescending(f => f.Size).ToList();
        return wanted.Count > 0 ? wanted[0].Index : (videos.Count == 1 ? videos[0].Index : -1);
    }

    /// <summary>
    /// Tells whether a file name carries a given season and episode (S01E05, 1x05 or "Season 1/Episode 5").
    /// </summary>
    /// <param name="path">The file path inside the torrent.</param>
    /// <param name="season">The season.</param>
    /// <param name="episode">The episode.</param>
    /// <returns>Whether it matches.</returns>
    public static bool MatchesEpisode(string path, int season, int episode)
    {
        var s = season.ToString(CultureInfo.InvariantCulture);
        var e = episode.ToString(CultureInfo.InvariantCulture);
        var timeout = TimeSpan.FromMilliseconds(100);
        var tag = Regex.IsMatch(path, $@"(?<![0-9a-z])s0*{s}[\s._-]*e0*{e}(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, timeout);
        var cross = Regex.IsMatch(path, $@"(?<![0-9])0*{s}x0*{e}(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, timeout);
        return tag || cross;
    }

    private static bool IsVideo(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.EndsWith(".mkv", StringComparison.Ordinal) || lower.EndsWith(".mp4", StringComparison.Ordinal)
            || lower.EndsWith(".avi", StringComparison.Ordinal) || lower.EndsWith(".m4v", StringComparison.Ordinal)
            || lower.EndsWith(".ts", StringComparison.Ordinal) || lower.EndsWith(".mov", StringComparison.Ordinal);
    }

    private static bool IsSample(string path)
    {
        return path.Contains("sample", StringComparison.OrdinalIgnoreCase) && path.Length < 400;
    }
}

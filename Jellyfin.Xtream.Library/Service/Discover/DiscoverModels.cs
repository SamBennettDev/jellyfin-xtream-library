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

namespace Jellyfin.Xtream.Library.Service.Discover;

/// <summary>
/// A movie or series in the TMDB catalog.
/// </summary>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="IsMovie">Whether it is a movie (otherwise a series).</param>
/// <param name="Title">The title.</param>
/// <param name="Year">The release (or first air) year, 0 when unknown.</param>
/// <param name="Overview">The plot summary.</param>
/// <param name="Popularity">TMDB's popularity score.</param>
public sealed record CatalogTitle(int TmdbId, bool IsMovie, string Title, int Year, string Overview, double Popularity);

/// <summary>
/// One episode of a series as TMDB lists it.
/// </summary>
/// <param name="Season">The season number.</param>
/// <param name="Episode">The episode number.</param>
/// <param name="Name">The episode name.</param>
/// <param name="Overview">The plot summary.</param>
/// <param name="RuntimeMinutes">The runtime in minutes, 0 when unknown.</param>
public sealed record CatalogEpisode(int Season, int Episode, string Name, string Overview, int RuntimeMinutes);

/// <summary>
/// A release an indexer offers for a title, as Radarr or Sonarr found it.
/// </summary>
public sealed class ReleaseOption
{
    /// <summary>
    /// Gets or sets a short stable id, derived from the indexer and the release guid.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the release title as the indexer lists it.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the quality label, such as "2160p Remux".
    /// </summary>
    public string Quality { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the vertical resolution, 0 when unknown.
    /// </summary>
    public int Resolution { get; set; }

    /// <summary>
    /// Gets or sets the size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of seeders.
    /// </summary>
    public int Seeders { get; set; }

    /// <summary>
    /// Gets or sets the indexer's name.
    /// </summary>
    public string Indexer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the indexer's id in Radarr or Sonarr.
    /// </summary>
    public int IndexerId { get; set; }

    /// <summary>
    /// Gets or sets the release guid Radarr or Sonarr uses to grab it.
    /// </summary>
    public string Guid { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the magnet link, when the indexer gives one.
    /// </summary>
    public string MagnetUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the torrent file address, when the indexer gives one.
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the torrent info hash, when known.
    /// </summary>
    public string InfoHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether it is a whole-season pack (a series release).
    /// </summary>
    public bool IsSeasonPack { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Radarr or Sonarr would turn it down under its own profile rules.
    /// </summary>
    public bool Rejected { get; set; }

    /// <summary>
    /// Gets or sets the languages the release carries.
    /// </summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>
    /// Gets the link TorrServer is given: the magnet when there is one, otherwise the torrent file address.
    /// </summary>
    public string TorrentLink => !string.IsNullOrEmpty(MagnetUrl) ? MagnetUrl : DownloadUrl;
}

/// <summary>
/// What a placeholder file stands for, read back from its STRM address.
/// </summary>
/// <param name="IsMovie">Whether it is a movie (otherwise an episode).</param>
/// <param name="TmdbId">The TMDB id of the movie or series.</param>
/// <param name="Season">The season (episodes only).</param>
/// <param name="Episode">The episode (episodes only).</param>
public sealed record TitleRef(bool IsMovie, int TmdbId, int Season, int Episode)
{
    /// <summary>
    /// Gets the path under the plugin's API that stands for this title.
    /// </summary>
    /// <returns>The path, without host.</returns>
    public string ToPath() => IsMovie
        ? $"/XtreamLibrary/Discover/auto/movie/{TmdbId}"
        : $"/XtreamLibrary/Discover/auto/episode/{TmdbId}/{Season}/{Episode}";

    /// <summary>
    /// Reads a title back from a STRM address or an API path.
    /// </summary>
    /// <param name="text">The address or path.</param>
    /// <param name="title">The title when it parsed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out TitleRef title)
    {
        title = new TitleRef(true, 0, 0, 0);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        const string marker = "/XtreamLibrary/Discover/auto/";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var parts = text[(at + marker.Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0] == "movie" && TryInt(parts[1], out var movieId))
        {
            title = new TitleRef(true, movieId, 0, 0);
            return true;
        }

        if (parts.Length == 4 && parts[0] == "episode" && TryInt(parts[1], out var seriesId)
            && TryInt(parts[2], out var season) && TryInt(parts[3], out var episode))
        {
            title = new TitleRef(false, seriesId, season, episode);
            return true;
        }

        return false;
    }

    private static bool TryInt(string text, out int value)
    {
        var end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return int.TryParse(text.AsSpan(0, end), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value) && value > 0;
    }
}

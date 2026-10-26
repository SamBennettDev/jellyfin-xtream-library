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

namespace Jellyfin.Xtream.Library;

/// <summary>
/// Settings for Discover: a library of every popular English title from TMDB, played by finding a release in
/// Radarr or Sonarr when you press play and streaming it through TorrServer. Off until switched on.
/// </summary>
public class DiscoverSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether Discover is on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the TMDB v3 API key. Empty falls back to the key in Jellyfin's TMDb plugin, if it has one.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original language the catalog is limited to (a TMDB code).
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Gets or sets how many of the most popular movies the catalog holds.
    /// </summary>
    public int MaxMovies { get; set; } = 3000;

    /// <summary>
    /// Gets or sets how many of the newest movies the catalog adds on top (released already, newest first).
    /// </summary>
    public int NewestMovies { get; set; }

    /// <summary>
    /// Gets or sets how many of the most popular series the catalog holds.
    /// </summary>
    public int MaxSeries { get; set; } = 800;

    /// <summary>
    /// Gets or sets how many of the newest series the catalog adds on top (aired already, newest first).
    /// </summary>
    public int NewestSeries { get; set; }

    /// <summary>
    /// Gets or sets how many of the newest episodes each series keeps. Zero keeps every aired episode. Long-running
    /// daily shows have thousands of episodes, which slow Jellyfin's scan to a crawl.
    /// </summary>
    public int MaxEpisodesPerSeries { get; set; }

    /// <summary>
    /// Gets or sets how many votes a title needs on TMDB to count as popular. It keeps obscure titles out.
    /// </summary>
    public int MinVotes { get; set; } = 500;

    /// <summary>
    /// Gets or sets how many votes a title needs to be listed among the newest. New titles have few votes yet.
    /// </summary>
    public int NewestMinVotes { get; set; } = 3;

    /// <summary>
    /// Gets or sets the folder the placeholder files are written to.
    /// </summary>
    public string CatalogPath { get; set; } = "/config/discover";

    /// <summary>
    /// Gets or sets the Radarr address (for example http://radarr:7878).
    /// </summary>
    public string RadarrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Radarr API key.
    /// </summary>
    public string RadarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Sonarr address (for example http://sonarr:8989).
    /// </summary>
    public string SonarrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Sonarr API key.
    /// </summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TorrServer address as Jellyfin reaches it (for example http://torrserver:8090).
    /// </summary>
    public string TorrServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Radarr root folder new movies are added under. Empty uses Radarr's first root folder.
    /// </summary>
    public string RadarrRootFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Sonarr root folder new series are added under. Empty uses Sonarr's first root folder.
    /// </summary>
    public string SonarrRootFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a streamed title is also sent to Radarr or Sonarr to be downloaded
    /// and kept. When it is, the placeholder is replaced by the real file in your library once it arrives.
    /// </summary>
    public bool KeepAfterStreaming { get; set; } = true;

    /// <summary>
    /// Gets or sets the fewest seeders a release needs to be offered.
    /// </summary>
    public int MinSeeders { get; set; } = 3;

    /// <summary>
    /// Gets or sets the most releases offered per title.
    /// </summary>
    public int MaxReleases { get; set; } = 25;

    /// <summary>
    /// Gets or sets the largest release offered, in gigabytes. Zero means no limit.
    /// </summary>
    public int MaxSizeGb { get; set; } = 90;

    /// <summary>
    /// Gets or sets how many seconds to wait for a torrent's file list before giving up.
    /// </summary>
    public int TorrentTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Gets a value indicating whether the settings are filled in enough to browse and play.
    /// </summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(TorrServerUrl)
        && (!string.IsNullOrWhiteSpace(RadarrUrl) || !string.IsNullOrWhiteSpace(SonarrUrl));

    /// <summary>
    /// Clamps every value into its supported range.
    /// </summary>
    public void Validate()
    {
        TmdbApiKey = (TmdbApiKey ?? string.Empty).Trim();
        Language = string.IsNullOrWhiteSpace(Language) ? "en" : Language.Trim().ToLowerInvariant();
        MaxMovies = Math.Clamp(MaxMovies, 0, 100000);
        NewestMovies = Math.Clamp(NewestMovies, 0, 10000);
        MaxSeries = Math.Clamp(MaxSeries, 0, 5000);
        NewestSeries = Math.Clamp(NewestSeries, 0, 10000);
        MaxEpisodesPerSeries = Math.Clamp(MaxEpisodesPerSeries, 0, 20000);
        MinVotes = Math.Clamp(MinVotes, 0, 100000);
        NewestMinVotes = Math.Clamp(NewestMinVotes, 0, 100000);
        MinSeeders = Math.Clamp(MinSeeders, 0, 1000);
        MaxReleases = Math.Clamp(MaxReleases, 1, 100);
        MaxSizeGb = Math.Clamp(MaxSizeGb, 0, 1000);
        TorrentTimeoutSeconds = Math.Clamp(TorrentTimeoutSeconds, 10, 300);
        CatalogPath = string.IsNullOrWhiteSpace(CatalogPath) ? "/config/discover" : CatalogPath.Trim().TrimEnd('/');
        RadarrUrl = (RadarrUrl ?? string.Empty).Trim().TrimEnd('/');
        SonarrUrl = (SonarrUrl ?? string.Empty).Trim().TrimEnd('/');
        TorrServerUrl = (TorrServerUrl ?? string.Empty).Trim().TrimEnd('/');
        RadarrApiKey = (RadarrApiKey ?? string.Empty).Trim();
        SonarrApiKey = (SonarrApiKey ?? string.Empty).Trim();
        RadarrRootFolder = (RadarrRootFolder ?? string.Empty).Trim();
        SonarrRootFolder = (SonarrRootFolder ?? string.Empty).Trim();
    }
}

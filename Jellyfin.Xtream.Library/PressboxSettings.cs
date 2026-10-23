// Copyright (C) 2024  Roland Breitschaft

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

namespace Jellyfin.Xtream.Library;

/// <summary>
/// A saved multiview: one to four channels composed into one channel by ffmpeg, with the sound of one.
/// </summary>
public class MultiviewDefinition
{
    /// <summary>
    /// Gets or sets the id (a short random string, part of the channel id).
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Gets or sets the secret the stream address carries, since Jellyfin's own ffmpeg fetches it without signing in.
    /// </summary>
    public string Token { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the name shown as the channel's name.
    /// </summary>
    public string Name { get; set; } = "Multiview";

    /// <summary>
    /// Gets or sets the layout: "Grid" or "Focus" (the audio tile large, the others small).
    /// </summary>
    public string Layout { get; set; } = "Grid";

    /// <summary>
    /// Gets or sets which tile's sound is heard (an index into <see cref="ChannelIds"/>).
    /// </summary>
    public int AudioIndex { get; set; }

    /// <summary>
    /// Gets or sets the tuner channel ids of the tiles ("xtream_..."), one to four.
    /// </summary>
    public List<string> ChannelIds { get; set; } = new();
}

/// <summary>
/// Settings for the features brought over from PressBox: the default guide and its matching, language
/// filtering, provider stream limits, sports, trending, multiview and the TV on-now ranking.
/// Everything here is off or neutral until switched on, so an upgrade changes nothing by itself.
/// </summary>
public class PressboxSettings
{
    /// <summary>
    /// The guides a fresh setup starts with: epgshare01's US guides (national and cable networks, the team
    /// channels, and the local stations), gzipped XMLTV refreshed there daily.
    /// </summary>
    public const string DefaultGuideList =
        "https://epgshare01.online/epgshare01/epg_ripper_US2.xml.gz\n"
        + "https://epgshare01.online/epgshare01/epg_ripper_US_SPORTS1.xml.gz\n"
        + "https://epgshare01.online/epgshare01/epg_ripper_US_LOCALS1.xml.gz";

    // =====================
    // Default guide
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether channels the provider gives no guide for are matched to a
    /// default guide, only where there is no doubt (by id, by a local station's call sign, or by an exact name).
    /// </summary>
    public bool EnableDefaultGuide { get; set; }

    /// <summary>
    /// Gets or sets the default guide addresses, one per line (gzipped or plain XMLTV). Empty uses <see cref="DefaultGuideList"/>.
    /// </summary>
    public string DefaultGuideUrls { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a matched channel takes the guide's name and logo (the provider's
    /// are kept where the guide's name is only a call sign).
    /// </summary>
    public bool DefaultGuideOverridesNames { get; set; } = true;

    /// <summary>
    /// Gets or sets how often the default guides are fetched again, in hours.
    /// </summary>
    public int DefaultGuideRefreshHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets channel ids pinned to a guide channel by hand: "xtream_channelId=GuideChannelId", one per line. A hand pick always wins.
    /// </summary>
    public string GuideManualMatches { get; set; } = string.Empty;

    // =====================
    // Languages
    // =====================

    /// <summary>
    /// Gets or sets the languages to show, as comma-separated ids ("en,es"); used when applying the language
    /// choice to the category selections. Empty means every language.
    /// </summary>
    public string PreferredLanguages { get; set; } = "en";

    // =====================
    // Stream limits
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether the number of simultaneous live streams per provider account is held
    /// to its limit (Auto reads the panel's own maximum, Limited uses the number set on the provider).
    /// </summary>
    public bool EnableStreamLimits { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the scheduled library sync waits for a quiet minute while someone is watching.
    /// </summary>
    public bool PauseSyncWhileWatching { get; set; } = true;

    // =====================
    // Sports
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether sports fixtures from ESPN are used: games are put on the guide
    /// (marked as sports) for the channels that carry them.
    /// </summary>
    public bool EnableSports { get; set; }

    /// <summary>
    /// Gets or sets the followed leagues, as comma-separated ids ("nfl,nba,nhl").
    /// </summary>
    public string SportsLeagues { get; set; } = "nfl,ncaaf,nba,wnba,ncaam,mlb,nhl,pga,nascar,f1,ufc,mls";

    // =====================
    // Trending
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether the ranking of what is on now is available.
    /// </summary>
    public bool EnableTrending { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the top trending films and series (IMDb's popularity, via Cinemeta)
    /// that are in the library are gathered into collections.
    /// </summary>
    public bool EnableTrendingCollections { get; set; }

    /// <summary>
    /// Gets or sets how many of the top titles are looked for in the library.
    /// </summary>
    public int TrendingCollectionSize { get; set; } = 30;

    // =====================
    // Multiview
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether saved multiviews are offered as channels.
    /// </summary>
    public bool EnableMultiview { get; set; }

    /// <summary>
    /// Gets or sets the path of ffmpeg for multiviews; empty uses the one Jellyfin ships with ("ffmpeg" on the path).
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the saved multiviews.
    /// </summary>
    public List<MultiviewDefinition> Multiviews { get; set; } = new();

    /// <summary>
    /// Clamps every value to a safe range.
    /// </summary>
    public void Validate()
    {
        DefaultGuideRefreshHours = Math.Clamp(DefaultGuideRefreshHours, 1, 168);
        TrendingCollectionSize = Math.Clamp(TrendingCollectionSize, 5, 100);
        Multiviews ??= new List<MultiviewDefinition>();
        foreach (var mv in Multiviews)
        {
            mv.ChannelIds ??= new List<string>();
            if (mv.ChannelIds.Count > 4)
            {
                mv.ChannelIds.RemoveRange(4, mv.ChannelIds.Count - 4);
            }

            mv.AudioIndex = mv.ChannelIds.Count == 0 ? 0 : Math.Clamp(mv.AudioIndex, 0, mv.ChannelIds.Count - 1);
            if (string.IsNullOrWhiteSpace(mv.Id))
            {
                mv.Id = Guid.NewGuid().ToString("N")[..8];
            }

            if (string.IsNullOrWhiteSpace(mv.Token))
            {
                mv.Token = Guid.NewGuid().ToString("N");
            }
        }
    }
}

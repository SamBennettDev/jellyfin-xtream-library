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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service.Discover;

/// <summary>
/// Adds the releases Radarr or Sonarr found as extra versions of a Discover title. They show in the client's version
/// picker next to the audio and subtitle choices. Picking one opens that release in TorrServer, and the client then
/// plays and seeks the TorrServer stream like any other remote file.
/// </summary>
public sealed class DiscoverSourceProvider : IMediaSourceProvider
{
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(40);

    private readonly DiscoverResolver _resolver;
    private readonly ILogger<DiscoverSourceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverSourceProvider"/> class.
    /// </summary>
    /// <param name="resolver">The resolver.</param>
    /// <param name="logger">The logger.</param>
    public DiscoverSourceProvider(DiscoverResolver resolver, ILogger<DiscoverSourceProvider> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance.Configuration.Discover;
        if (!settings.IsConfigured || item is not Video video || !TitleRef.TryParse(video.ShortcutPath ?? video.Path, out var title))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(SearchBudget);
            var releases = await _resolver.GetReleasesAsync(title, budget.Token).ConfigureAwait(false);
            return releases.Select(r => ToSource(title, r)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // No versions to pick from is better than a failed play request; the default source still works.
            _logger.LogWarning(ex, "Discover: could not list releases for {Item}", video.Name);
            return Array.Empty<MediaSourceInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        if (!TryParseToken(openToken, out var title, out var releaseId))
        {
            throw new InvalidOperationException("Not a Discover open token.");
        }

        var opened = await _resolver.OpenAsync(title, releaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("No release of this title could be opened. Try another version, or try again later.");
        var source = new MediaSourceInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = ReleaseLabeler.Label(opened.Release),
            Path = opened.Url,
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Size = opened.SizeBytes,
            Container = Path.GetExtension(opened.FileName).TrimStart('.').ToLowerInvariant(),
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            IsInfiniteStream = false,
            RequiresOpening = false,
            RequiresClosing = false,
            ReadAtNativeFramerate = false,
            AnalyzeDurationMs = 3000,
        };
        return new DiscoverLiveStream(source, openToken);
    }

    private static MediaSourceInfo ToSource(TitleRef title, ReleaseOption release)
    {
        return new MediaSourceInfo
        {
            Id = "discover" + release.Id,
            Name = ReleaseLabeler.Label(release),
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Size = release.SizeBytes,
            RequiresOpening = true,
            OpenToken = MakeToken(title, release.Id),
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            IsInfiniteStream = false,
        };
    }

    private static string MakeToken(TitleRef title, string releaseId) =>
        string.Create(CultureInfo.InvariantCulture, $"{(title.IsMovie ? "m" : "e")}|{title.TmdbId}|{title.Season}|{title.Episode}|{releaseId}");

    private static bool TryParseToken(string token, out TitleRef title, out string releaseId)
    {
        title = new TitleRef(true, 0, 0, 0);
        releaseId = string.Empty;
        var parts = token.Split('|');
        if (parts.Length != 5
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tmdb)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var season)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var episode)
            || tmdb <= 0)
        {
            return false;
        }

        title = new TitleRef(parts[0] == "m", tmdb, season, episode);
        releaseId = parts[4];
        return true;
    }
}

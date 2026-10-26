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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service.Discover;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Api;

/// <summary>
/// API for Discover: the address a placeholder file points at (which picks a release and redirects to its stream),
/// plus administrator endpoints to test the connections, list the releases for a title and run the catalog sync.
/// </summary>
[ApiController]
[Route("XtreamLibrary/Discover")]
public class DiscoverController : ControllerBase
{
    private static readonly object RunLock = new();
    private static volatile bool _syncRunning;
    private static volatile string _lastResult = "Not run yet.";

    private readonly DiscoverResolver _resolver;
    private readonly DiscoverCatalogService _catalog;
    private readonly TmdbClient _tmdb;
    private readonly ArrClient _arr;
    private readonly TorrServerClient _torrServer;
    private readonly ILogger<DiscoverController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverController"/> class.
    /// </summary>
    /// <param name="resolver">The resolver.</param>
    /// <param name="catalog">The catalog service.</param>
    /// <param name="tmdb">The TMDB client.</param>
    /// <param name="arr">The Radarr and Sonarr client.</param>
    /// <param name="torrServer">The TorrServer client.</param>
    /// <param name="logger">The logger.</param>
    public DiscoverController(
        DiscoverResolver resolver,
        DiscoverCatalogService catalog,
        TmdbClient tmdb,
        ArrClient arr,
        TorrServerClient torrServer,
        ILogger<DiscoverController> logger)
    {
        _resolver = resolver;
        _catalog = catalog;
        _tmdb = tmdb;
        _arr = arr;
        _torrServer = torrServer;
        _logger = logger;
    }

    /// <summary>
    /// What a movie placeholder's address does: finds the best release, opens it in TorrServer and redirects to the
    /// stream. Only the server itself may call it (Jellyfin's own ffmpeg does), because it starts searches and torrents.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A redirect to the stream.</returns>
    [HttpGet("auto/movie/{tmdbId:int}")]
    [AllowAnonymous]
    public Task<IActionResult> AutoMovie([FromRoute] int tmdbId, CancellationToken cancellationToken)
    {
        return AutoAsync(new TitleRef(true, tmdbId, 0, 0), cancellationToken);
    }

    /// <summary>
    /// What an episode placeholder's address does.
    /// </summary>
    /// <param name="tmdbId">The series' TMDB id.</param>
    /// <param name="season">The season.</param>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A redirect to the stream.</returns>
    [HttpGet("auto/episode/{tmdbId:int}/{season:int}/{episode:int}")]
    [AllowAnonymous]
    public Task<IActionResult> AutoEpisode([FromRoute] int tmdbId, [FromRoute] int season, [FromRoute] int episode, CancellationToken cancellationToken)
    {
        return AutoAsync(new TitleRef(false, tmdbId, season, episode), cancellationToken);
    }

    /// <summary>
    /// Lists the releases on offer for a title.
    /// </summary>
    /// <param name="kind">"movie" or "episode".</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="season">The season (episodes).</param>
    /// <param name="episode">The episode (episodes).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The releases with their labels.</returns>
    [HttpGet("Releases")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Releases([FromQuery] string kind, [FromQuery] int tmdbId, [FromQuery] int season, [FromQuery] int episode, CancellationToken cancellationToken)
    {
        var title = string.Equals(kind, "movie", StringComparison.OrdinalIgnoreCase)
            ? new TitleRef(true, tmdbId, 0, 0)
            : new TitleRef(false, tmdbId, season, episode);
        try
        {
            var releases = await _resolver.GetReleasesAsync(title, cancellationToken).ConfigureAwait(false);
            return Ok(releases.Select(r => new { r.Id, Label = ReleaseLabeler.Label(r), r.Title, r.Seeders, r.SizeBytes, r.Indexer, r.Rejected }));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Tests each connection Discover depends on.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One line per service.</returns>
    [HttpPost("Test")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Test(CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance.Configuration.Discover;
        var report = new Dictionary<string, string>();
        report["tmdb"] = await Try(async () =>
        {
            return await _tmdb.TestAsync(settings, cancellationToken).ConfigureAwait(false) ? "ok" : "TMDB rejected the key";
        }).ConfigureAwait(false);
        report["radarr"] = string.IsNullOrEmpty(settings.RadarrUrl)
            ? "not set"
            : await Try(async () => $"ok, {(await _arr.ListMoviesAsync(settings.RadarrUrl, settings.RadarrApiKey, cancellationToken).ConfigureAwait(false)).Count} movies").ConfigureAwait(false);
        report["sonarr"] = string.IsNullOrEmpty(settings.SonarrUrl)
            ? "not set"
            : await Try(async () => $"ok, {(await _arr.ListSeriesAsync(settings.SonarrUrl, settings.SonarrApiKey, cancellationToken).ConfigureAwait(false)).Count} series").ConfigureAwait(false);
        report["torrserver"] = string.IsNullOrEmpty(settings.TorrServerUrl)
            ? "not set"
            : await Try(async () =>
            {
                var version = await _torrServer.EchoAsync(settings.TorrServerUrl, cancellationToken).ConfigureAwait(false);
                return version is null ? "no answer" : "ok, " + version;
            }).ConfigureAwait(false);
        return Ok(report);
    }

    /// <summary>
    /// Starts a catalog sync in the background.
    /// </summary>
    /// <returns>Whether it started.</returns>
    [HttpPost("Sync")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult Sync()
    {
        lock (RunLock)
        {
            if (_syncRunning)
            {
                return Conflict(new { error = "A Discover sync is already running." });
            }

            _syncRunning = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _catalog.SyncAsync(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
                _lastResult = $"{result.MoviesWritten} movies, {result.SeriesWritten} series, {result.EpisodesWritten} new episodes, {result.Removed} removed, {result.SkippedOwned} already owned";
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.IO.IOException)
            {
                _logger.LogError(ex, "Discover sync failed");
                _lastResult = "Failed: " + ex.Message;
            }
            finally
            {
                _syncRunning = false;
            }
        });
        return Accepted(new { started = true });
    }

    /// <summary>
    /// Reports whether a sync is running and how the last one ended.
    /// </summary>
    /// <returns>The state.</returns>
    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult Status() => Ok(new { running = _syncRunning, last = _lastResult });

    private static bool IsLocal(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    private static async Task<string> Try(Func<Task<string>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return "failed: " + ex.Message;
        }
    }

    private async Task<IActionResult> AutoAsync(TitleRef title, CancellationToken cancellationToken)
    {
        if (!IsLocal(HttpContext))
        {
            return NotFound();
        }

        if (!Plugin.Instance.Configuration.Discover.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Discover is not set up.");
        }

        try
        {
            var opened = await _resolver.OpenAsync(title, null, cancellationToken).ConfigureAwait(false);
            if (opened is null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "No release of this title is available right now.");
            }

            return Redirect(opened.Url);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Discover: could not open {Title}", title);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }
}

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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Service.Pressbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Api;

/// <summary>
/// API for the PressBox features: category languages, stream limits, default guides, sports, trending and multiview.
/// Everything is for the administrator except the multiview stream itself, which is opened by Jellyfin (and, through
/// it, the viewer's client) and is protected by the multiview's secret token.
/// </summary>
[ApiController]
[Route("XtreamLibrary")]
public class PressboxController : ControllerBase
{
    private readonly IXtreamClient _client;
    private readonly LiveTvService _liveTv;
    private readonly XtreamTunerHost _tuner;
    private readonly StreamLimitService _limits;
    private readonly DefaultGuideService _guide;
    private readonly SportsService _sports;
    private readonly TrendingService _trending;
    private readonly TrendingCollectionService _collections;
    private readonly MultiviewService _multiview;
    private readonly ILogger<PressboxController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PressboxController"/> class.
    /// </summary>
    /// <param name="client">The Xtream client.</param>
    /// <param name="liveTv">The Live TV service.</param>
    /// <param name="tuner">The tuner host.</param>
    /// <param name="limits">The stream limit service.</param>
    /// <param name="guide">The default guide service.</param>
    /// <param name="sports">The sports service.</param>
    /// <param name="trending">The trending service.</param>
    /// <param name="collections">The trending collection service.</param>
    /// <param name="multiview">The multiview service.</param>
    /// <param name="logger">The logger.</param>
    public PressboxController(
        IXtreamClient client,
        LiveTvService liveTv,
        XtreamTunerHost tuner,
        StreamLimitService limits,
        DefaultGuideService guide,
        SportsService sports,
        TrendingService trending,
        TrendingCollectionService collections,
        MultiviewService multiview,
        ILogger<PressboxController> logger)
    {
        _client = client;
        _liveTv = liveTv;
        _tuner = tuner;
        _limits = limits;
        _guide = guide;
        _sports = sports;
        _trending = trending;
        _collections = collections;
        _multiview = multiview;
        _logger = logger;
    }

    /// <summary>
    /// Lists the languages found in a provider's category names, with how many categories each has.
    /// </summary>
    /// <param name="providerIndex">The provider.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The languages found.</returns>
    [HttpGet("PressBox/Languages")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<LanguagesResponse>> GetLanguages([FromQuery] int providerIndex = 0, CancellationToken cancellationToken = default)
    {
        var connection = ConnectionOf(providerIndex);
        if (connection is null)
        {
            return BadRequest("Provider credentials not configured.");
        }

        try
        {
            var vod = await _client.GetVodCategoryAsync(connection, cancellationToken).ConfigureAwait(false);
            var series = await _client.GetSeriesCategoryAsync(connection, cancellationToken).ConfigureAwait(false);
            var live = await _client.GetLiveCategoryAsync(connection, cancellationToken).ConfigureAwait(false);
            var rows = CategoryLanguage.All
                .Select(l => new LanguageRow
                {
                    Id = l.Id,
                    Name = l.Name,
                    Vod = vod.Count(c => CategoryLanguage.LanguagesOf(c.CategoryName).Contains(l.Id)),
                    Series = series.Count(c => CategoryLanguage.LanguagesOf(c.CategoryName).Contains(l.Id)),
                    Live = live.Count(c => CategoryLanguage.LanguagesOf(c.CategoryName).Contains(l.Id)),
                })
                .Where(r => r.Vod + r.Series + r.Live > 0)
                .OrderByDescending(r => r.Vod + r.Series + r.Live)
                .ToList();
            return Ok(new LanguagesResponse
            {
                Languages = rows,
                Preferred = Plugin.Instance.Configuration.Pressbox.PreferredLanguages
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            });
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not list category languages");
            return BadRequest("Could not fetch categories: " + ex.Message);
        }
    }

    /// <summary>
    /// Selects the categories (movies, series and, in custom mode, live) that are in the chosen languages, replacing
    /// the current selection. A category is selected only when its name says it is in a chosen language, so
    /// categories that name no language (plain genres) are not selected.
    /// </summary>
    /// <param name="request">The languages and provider.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many categories were selected.</returns>
    [HttpPost("PressBox/Languages/Apply")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<ApplyLanguagesResponse>> ApplyLanguages([FromBody] ApplyLanguagesRequest request, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var provider = config.Providers.ElementAtOrDefault(request.ProviderIndex);
        var connection = ConnectionOf(request.ProviderIndex);
        if (provider is null || connection is null)
        {
            return BadRequest("Provider credentials not configured.");
        }

        var wanted = request.Languages.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim().ToLowerInvariant()).ToList();
        if (wanted.Count == 0)
        {
            return BadRequest("Choose at least one language.");
        }

        try
        {
            var vod = await _client.GetVodCategoryAsync(connection, cancellationToken).ConfigureAwait(false);
            var series = await _client.GetSeriesCategoryAsync(connection, cancellationToken).ConfigureAwait(false);
            provider.SelectedVodCategoryIds = vod.Where(c => CategoryLanguage.InLanguages(c.CategoryName, wanted)).Select(c => c.CategoryId).ToArray();
            provider.SelectedSeriesCategoryIds = series.Where(c => CategoryLanguage.InLanguages(c.CategoryName, wanted)).Select(c => c.CategoryId).ToArray();

            var liveCount = 0;
            var liveSkipped = false;
            if (request.IncludeLive)
            {
                if (config.LiveChannelMode == LiveChannelSelectionMode.Custom)
                {
                    var live = await _client.GetLiveCategoryAsync(connection, cancellationToken).ConfigureAwait(false);
                    config.SelectedLiveCategoryIds = live.Where(c => CategoryLanguage.InLanguages(c.CategoryName, wanted)).Select(c => c.CategoryId).ToArray();
                    liveCount = config.SelectedLiveCategoryIds.Length;
                }
                else
                {
                    liveSkipped = true;
                }
            }

            config.Pressbox.PreferredLanguages = string.Join(',', wanted);
            Plugin.Instance.SaveConfiguration();
            _liveTv.InvalidateCache();
            return Ok(new ApplyLanguagesResponse
            {
                Vod = provider.SelectedVodCategoryIds.Length,
                Series = provider.SelectedSeriesCategoryIds.Length,
                Live = liveCount,
                LiveSkipped = liveSkipped,
            });
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not apply category languages");
            return BadRequest("Could not fetch categories: " + ex.Message);
        }
    }

    /// <summary>
    /// Gets each provider's stream allowance and how much of it is in use.
    /// </summary>
    /// <returns>One entry per provider.</returns>
    [HttpGet("PressBox/StreamLimits")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<IReadOnlyList<ProviderStreamStatus>> GetStreamLimits() => Ok(_limits.Status());

    /// <summary>
    /// Asks every provider's panel for its connection limit and use.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One entry per provider.</returns>
    [HttpPost("PressBox/StreamLimits/Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<IReadOnlyList<ProviderStreamStatus>>> RefreshStreamLimits(CancellationToken cancellationToken)
        => Ok(await _limits.RefreshAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets the state of each default guide.
    /// </summary>
    /// <returns>One entry per guide.</returns>
    [HttpGet("PressBox/Guide")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<IReadOnlyList<DefaultGuideStatus>> GetGuideStatus() => Ok(_guide.Status());

    /// <summary>
    /// Fetches every default guide now.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The state of each guide afterwards.</returns>
    [HttpPost("PressBox/Guide/Refresh")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<IReadOnlyList<DefaultGuideStatus>>> RefreshGuide(CancellationToken cancellationToken)
    {
        var channels = await _liveTv.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
        _guide.Update(channels);
        await _guide.RefreshNowAsync(cancellationToken).ConfigureAwait(false);
        return Ok(_guide.Status());
    }

    /// <summary>
    /// Lists the channels matched to a default guide, and how each was matched.
    /// </summary>
    /// <returns>The matches.</returns>
    [HttpGet("PressBox/Guide/Matches")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<IEnumerable<GuideMatch>> GetGuideMatches() => Ok(_guide.Matches.Values.OrderBy(m => m.ChannelKey, StringComparer.Ordinal));

    /// <summary>
    /// Searches the default guides' channels, to pick a match by hand.
    /// </summary>
    /// <param name="q">What to look for.</param>
    /// <returns>Matching guide channels.</returns>
    [HttpGet("PressBox/Guide/Search")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<IReadOnlyList<GuideChannel>> SearchGuide([FromQuery] string q) => Ok(_guide.Search(q ?? string.Empty));

    /// <summary>
    /// Matches a channel to a guide channel by hand, or removes the match.
    /// </summary>
    /// <param name="request">The channel and the guide channel (empty to remove).</param>
    /// <returns>No content.</returns>
    [HttpPost("PressBox/Guide/Match")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult SetGuideMatch([FromBody] GuideMatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ChannelId))
        {
            return BadRequest("A channel is required.");
        }

        _guide.SetManualMatch(request.ChannelId, request.GuideChannelId ?? string.Empty);
        return NoContent();
    }

    /// <summary>
    /// Gets the leagues that can be followed.
    /// </summary>
    /// <returns>The leagues.</returns>
    [HttpGet("PressBox/Sports/Leagues")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<IReadOnlyList<SportsLeague>> GetLeagues() => Ok(EspnScoreboard.Leagues);

    /// <summary>
    /// Gets today's games in the followed leagues.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The games, earliest first.</returns>
    [HttpGet("PressBox/Sports/Slate")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<IReadOnlyList<Fixture>>> GetSlate(CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.Pressbox.EnableSports)
        {
            return BadRequest("Sports is switched off.");
        }

        return Ok(await _sports.GetSlateAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Finds the channels showing a game.
    /// </summary>
    /// <param name="id">The game's id from the slate.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The channels, best first.</returns>
    [HttpGet("PressBox/Sports/Games/{id}/Channels")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<IReadOnlyList<GameChannelMatch>>> GetGameChannels([FromRoute] string id, CancellationToken cancellationToken)
    {
        var channels = await _liveTv.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(await _sports.StreamsForAsync(id, channels, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Gets what is worth watching on now.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The things on now, hottest first.</returns>
    [HttpGet("PressBox/Trending")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<IReadOnlyList<TrendingItem>>> GetTrending(CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.Pressbox.EnableTrending)
        {
            return BadRequest("Trending is switched off.");
        }

        var channels = await _liveTv.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(await _trending.GetAsync(channels, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Brings the Trending Now collection up to date now.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many titles it holds.</returns>
    [HttpPost("PressBox/Trending/RefreshCollection")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<int>> RefreshTrendingCollection(CancellationToken cancellationToken)
        => Ok(await _collections.RefreshAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Searches the lineup by channel name, for choosing a multiview's channels.
    /// </summary>
    /// <param name="q">What to look for.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Up to thirty channels.</returns>
    [HttpGet("PressBox/Channels")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<IEnumerable<ChannelRow>>> SearchChannels([FromQuery] string q, CancellationToken cancellationToken)
    {
        var text = (q ?? string.Empty).Trim();
        if (text.Length < 2)
        {
            return Ok(Array.Empty<ChannelRow>());
        }

        var channels = await _liveTv.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(channels
            .Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(30)
            .Select(c => new ChannelRow { Id = XtreamTunerHost.BuildChannelId(c.ProviderIndex, c.StreamId), Name = c.Name }));
    }

    /// <summary>
    /// Lists the multiviews, with the address each is served from.
    /// </summary>
    /// <returns>The multiviews.</returns>
    [HttpGet("PressBox/Multiviews")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<IEnumerable<MultiviewRow>> GetMultiviews()
    {
        var settings = Plugin.Instance.Configuration.Pressbox;
        var baseUrl = _liveTv.GetServerBaseUrl().TrimEnd('/');
        return Ok(settings.Multiviews.Select(m => new MultiviewRow
        {
            Id = m.Id,
            Name = m.Name,
            Layout = m.Layout,
            ChannelIds = m.ChannelIds,
            ChannelNumber = null,
            StreamUrl = baseUrl + "/XtreamLibrary/Multiview/" + m.Token + "/stream.ts",
            Running = _multiview.IsRunning(m.Id),
        }));
    }

    /// <summary>
    /// Serves a multiview as one MPEG-TS stream. The token in the address is the multiview's secret.
    /// </summary>
    /// <param name="token">The multiview's token.</param>
    /// <param name="cancellationToken">Cancelled when the viewer leaves.</param>
    /// <returns>The stream, until the viewer leaves.</returns>
    [HttpGet("Multiview/{token}/stream.ts")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> StreamMultiview([FromRoute] string token, CancellationToken cancellationToken)
    {
        var definition = MultiviewService.ByToken(token);
        if (definition is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        Response.ContentType = "video/mp2t";
        try
        {
            await _multiview.StreamAsync(definition, _tuner.ResolveTileAsync, Response.Body, cancellationToken).ConfigureAwait(false);
            return new EmptyResult();
        }
        catch (StreamLimitException ex)
        {
            return new ContentResult { StatusCode = StatusCodes.Status503ServiceUnavailable, Content = ex.Message, ContentType = "text/plain" };
        }
        catch (InvalidOperationException ex)
        {
            return new ContentResult { StatusCode = StatusCodes.Status409Conflict, Content = ex.Message, ContentType = "text/plain" };
        }
    }

    private static Client.ConnectionInfo? ConnectionOf(int providerIndex)
    {
        var provider = Plugin.Instance.Configuration.Providers.ElementAtOrDefault(providerIndex);
        if (provider is null || string.IsNullOrEmpty(provider.BaseUrl) || string.IsNullOrEmpty(provider.Username))
        {
            return null;
        }

        return new Client.ConnectionInfo(provider.BaseUrl, provider.Username, provider.Password ?? string.Empty);
    }
}

/// <summary>
/// The languages found in a provider's categories.
/// </summary>
public class LanguagesResponse
{
    /// <summary>Gets or sets the languages found, most categories first.</summary>
    public IReadOnlyList<LanguageRow> Languages { get; set; } = Array.Empty<LanguageRow>();

    /// <summary>Gets or sets the language ids chosen before.</summary>
    public IReadOnlyList<string> Preferred { get; set; } = Array.Empty<string>();
}

/// <summary>
/// One language and its category counts.
/// </summary>
public class LanguageRow
{
    /// <summary>Gets or sets the language id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the language name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets how many movie categories.</summary>
    public int Vod { get; set; }

    /// <summary>Gets or sets how many series categories.</summary>
    public int Series { get; set; }

    /// <summary>Gets or sets how many live categories.</summary>
    public int Live { get; set; }
}

/// <summary>
/// A request to select categories by language.
/// </summary>
public class ApplyLanguagesRequest
{
    /// <summary>Gets or sets the provider index.</summary>
    public int ProviderIndex { get; set; }

    /// <summary>Gets or sets the language ids to keep.</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether live categories are selected too (custom live mode only).</summary>
    public bool IncludeLive { get; set; }
}

/// <summary>
/// What selecting by language did.
/// </summary>
public class ApplyLanguagesResponse
{
    /// <summary>Gets or sets the movie categories selected.</summary>
    public int Vod { get; set; }

    /// <summary>Gets or sets the series categories selected.</summary>
    public int Series { get; set; }

    /// <summary>Gets or sets the live categories selected.</summary>
    public int Live { get; set; }

    /// <summary>Gets or sets a value indicating whether live was left alone because channel selection is not in custom mode.</summary>
    public bool LiveSkipped { get; set; }
}

/// <summary>
/// A request to match a channel to a guide channel.
/// </summary>
public class GuideMatchRequest
{
    /// <summary>Gets or sets the tuner channel id.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>Gets or sets the guide channel id; empty removes the match.</summary>
    public string? GuideChannelId { get; set; }
}

/// <summary>
/// A multiview as the settings page shows it.
/// </summary>
public class MultiviewRow
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the layout.</summary>
    public string Layout { get; set; } = string.Empty;

    /// <summary>Gets or sets the channels in it.</summary>
    public IReadOnlyList<string> ChannelIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the channel number (filled in by the page from the tuner's list).</summary>
    public string? ChannelNumber { get; set; }

    /// <summary>Gets or sets the address its stream is served from.</summary>
    public string StreamUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether someone is watching it.</summary>
    public bool Running { get; set; }
}

/// <summary>
/// A lineup channel as the settings page lists it.
/// </summary>
public class ChannelRow
{
    /// <summary>Gets or sets the tuner channel id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel name.</summary>
    public string Name { get; set; } = string.Empty;
}

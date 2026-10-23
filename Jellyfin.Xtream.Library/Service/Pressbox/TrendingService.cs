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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// What is worth watching on now: live games with the channels showing them, programmes on channels the network
/// weights favour, and anything the search trends mention. The ranking is <see cref="TrendingRanker"/>; this class
/// fetches its inputs (Google Trends' public feeds, today's slate, the default guide) and caches the trend terms.
/// </summary>
public sealed class TrendingService : IDisposable
{
    private static readonly TimeSpan TermsTtl = TimeSpan.FromMinutes(10);
    private static readonly string[] Regions = ["US", "GB"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SportsService _sports;
    private readonly DefaultGuideService _defaultGuide;
    private readonly ILogger<TrendingService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<TrendTerm> _terms = Array.Empty<TrendTerm>();
    private DateTimeOffset _termsAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrendingService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="sports">The sports service.</param>
    /// <param name="defaultGuide">The default guide service.</param>
    /// <param name="logger">The logger.</param>
    public TrendingService(IHttpClientFactory httpClientFactory, SportsService sports, DefaultGuideService defaultGuide, ILogger<TrendingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sports = sports;
        _defaultGuide = defaultGuide;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.Dispose();
    }

    /// <summary>
    /// Gets the current trend terms (cached for ten minutes; the last good set is kept when a feed cannot be read).
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The terms.</returns>
    public async Task<IReadOnlyList<TrendTerm>> GetTermsAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _termsAt < TermsTtl)
        {
            return _terms;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow - _termsAt < TermsTtl)
            {
                return _terms;
            }

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var all = new List<TrendTerm>();
            foreach (var geo in Regions)
            {
                try
                {
                    var xml = await client.GetStringAsync(new Uri("https://trends.google.com/trending/rss?geo=" + geo), cancellationToken).ConfigureAwait(false);
                    all.AddRange(TrendingRanker.ParseTrendsRss(xml, geo));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.LogWarning("Google Trends {Geo} could not be read: {Reason}", geo, ex.Message);
                }
            }

            if (all.Count > 0)
            {
                _terms = all;
            }

            // Failed or not, wait out the TTL: a feed that is down is not retried on every request.
            _termsAt = DateTimeOffset.UtcNow;
            return _terms;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Ranks what is on now.
    /// </summary>
    /// <param name="channels">The lineup.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The things on now, hottest first.</returns>
    public async Task<IReadOnlyList<TrendingItem>> GetAsync(IReadOnlyList<LiveStreamInfo> channels, CancellationToken cancellationToken)
    {
        var terms = await GetTermsAsync(cancellationToken).ConfigureAwait(false);
        var fixtures = Plugin.Instance.Configuration.Pressbox.EnableSports
            ? await _sports.GetSlateAsync(cancellationToken).ConfigureAwait(false)
            : Array.Empty<Fixture>();
        var lineup = channels
            .Select(c =>
            {
                var id = XtreamTunerHost.BuildChannelId(c.ProviderIndex, c.StreamId);
                return new LineupChannel(id, c.Name, string.Empty, id);
            })
            .ToList();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var around = _defaultGuide.ListingsAround(nowMs - (6 * 3_600_000L), nowMs + (18 * 3_600_000L));
        var onNow = around.Where(l => l.StartMs <= nowMs && l.StopMs > nowMs).ToList();
        return TrendingRanker.Rank(nowMs, lineup, fixtures, onNow, around, terms, new HashSet<string>(StringComparer.Ordinal), Array.Empty<string>());
    }
}

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
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// Today's games from ESPN's public scoreboards, with the lineup's channels that show each one. The slate is cached
/// briefly (scores move), refreshed in the background, and never fetched while a guide is being generated.
/// </summary>
public sealed class SportsService : IDisposable
{
    private static readonly TimeSpan SlateTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GameLength = TimeSpan.FromHours(3);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DefaultGuideService _defaultGuide;
    private readonly ILogger<SportsService> _logger;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private volatile SlateSnapshot _slate = new(DateTimeOffset.MinValue, Array.Empty<Fixture>());
    private int _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="SportsService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="defaultGuide">The default guide service, whose listings help find a game's channels.</param>
    /// <param name="logger">The logger.</param>
    public SportsService(IHttpClientFactory httpClientFactory, DefaultGuideService defaultGuide, ILogger<SportsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _defaultGuide = defaultGuide;
        _logger = logger;
    }

    /// <summary>
    /// Gets the leagues the viewer follows.
    /// </summary>
    /// <returns>The followed leagues, in the order chosen.</returns>
    public static IReadOnlyList<SportsLeague> FollowedLeagues()
    {
        var ids = Plugin.Instance.Configuration.Pressbox.SportsLeagues
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ids
            .Select(id => EspnScoreboard.Leagues.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();
    }

    /// <summary>
    /// Gets today's games.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The fixtures, earliest first.</returns>
    public async Task<IReadOnlyList<Fixture>> GetSlateAsync(CancellationToken cancellationToken)
    {
        var slate = _slate;
        if (DateTimeOffset.UtcNow - slate.FetchedAt < SlateTtl)
        {
            return slate.Fixtures;
        }

        await _fetchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            slate = _slate;
            if (DateTimeOffset.UtcNow - slate.FetchedAt < SlateTtl)
            {
                return slate.Fixtures;
            }

            var fixtures = await FetchAsync(cancellationToken).ConfigureAwait(false);
            _slate = new SlateSnapshot(DateTimeOffset.UtcNow, fixtures);
            return fixtures;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// Gets the channels showing a game.
    /// </summary>
    /// <param name="fixtureId">The game's id.</param>
    /// <param name="channels">The lineup.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The channels, best first; empty when the game is not on today's slate.</returns>
    public async Task<IReadOnlyList<GameChannelMatch>> StreamsForAsync(string fixtureId, IReadOnlyList<LiveStreamInfo> channels, CancellationToken cancellationToken)
    {
        var slate = await GetSlateAsync(cancellationToken).ConfigureAwait(false);
        var fixture = slate.FirstOrDefault(f => string.Equals(f.Id, fixtureId, StringComparison.Ordinal));
        if (fixture is null)
        {
            return Array.Empty<GameChannelMatch>();
        }

        return Find(fixture, channels);
    }

    /// <summary>
    /// Writes guide entries for game channels (channels named for a game, such as a numbered pay-per-view feed)
    /// that the provider gives no guide for. Uses the slate already held; never fetches.
    /// </summary>
    /// <param name="sb">The XMLTV being built.</param>
    /// <param name="programmesStart">Where its programmes begin.</param>
    /// <param name="channels">The lineup.</param>
    /// <returns>How many programmes were added.</returns>
    public int AppendGameProgrammes(StringBuilder sb, int programmesStart, IReadOnlyList<LiveStreamInfo> channels)
    {
        var settings = Plugin.Instance.Configuration.Pressbox;
        if (!settings.EnableSports)
        {
            return 0;
        }

        StartBackgroundRefresh();
        var slate = _slate;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var games = slate.Fixtures
            .Where(f => f.State != "post" && f.StartMs > nowMs - GameLength.TotalMilliseconds && f.StartMs < nowMs + TimeSpan.FromHours(24).TotalMilliseconds)
            .ToList();
        if (games.Count == 0)
        {
            return 0;
        }

        var have = DefaultGuideService.ChannelsWithProgrammes(sb, programmesStart);
        var bare = channels
            .Where(c => !have.Contains(XtreamTunerHost.BuildChannelId(c.ProviderIndex, c.StreamId)))
            .ToList();
        if (bare.Count == 0)
        {
            return 0;
        }

        var written = 0;
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var game in games.OrderBy(g => g.StartMs))
        {
            foreach (var match in Find(game, bare).Where(m => m.Score >= 0.9 && taken.Add(m.Channel.Key)))
            {
                var start = DateTimeOffset.FromUnixTimeMilliseconds(game.StartMs);
                var stop = start + GameLength;
                var title = System.Security.SecurityElement.Escape(game.Name);
                sb.Append(CultureInfo.InvariantCulture, $"  <programme start=\"{Format(start)}\" stop=\"{Format(stop)}\" channel=\"{System.Security.SecurityElement.Escape(match.Channel.Key)}\">\n");
                sb.Append(CultureInfo.InvariantCulture, $"    <title>{title}</title>\n");
                sb.Append(CultureInfo.InvariantCulture, $"    <desc>{System.Security.SecurityElement.Escape(game.LeagueLabel)}</desc>\n");
                sb.Append("    <category>Sports</category>\n");
                sb.Append("  </programme>\n");
                written++;
            }
        }

        return written;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fetchLock.Dispose();
    }

    private static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

    private static string EasternDay(DateTimeOffset now, int daysBack)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.CreateCustomTimeZone("ET", TimeSpan.FromHours(-5), "ET", "ET");
        }
        catch (InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.CreateCustomTimeZone("ET", TimeSpan.FromHours(-5), "ET", "ET");
        }

        return TimeZoneInfo.ConvertTime(now, zone).AddDays(-daysBack).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<GameChannelMatch> Find(Fixture fixture, IReadOnlyList<LiveStreamInfo> channels)
    {
        var lineup = channels
            .Select(c =>
            {
                var id = XtreamTunerHost.BuildChannelId(c.ProviderIndex, c.StreamId);
                return new LineupChannel(id, c.Name, string.Empty, id);
            })
            .ToList();
        var listings = _defaultGuide.ListingsAround(fixture.StartMs - 3_600_000L, fixture.StartMs + (long)GameLength.TotalMilliseconds);
        return new GameStreamFinder(lineup).StreamsFor(fixture, listings);
    }

    private void StartBackgroundRefresh()
    {
        if (DateTimeOffset.UtcNow - _slate.FetchedAt < SlateTtl || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await GetSlateAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning("Could not refresh the sports slate: {Reason}", ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private async Task<IReadOnlyList<Fixture>> FetchAsync(CancellationToken cancellationToken)
    {
        var leagues = FollowedLeagues();
        var now = DateTimeOffset.UtcNow;
        var days = new List<string> { EasternDay(now, 0) };
        if (TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Utc).Hour is >= 4 and < 10)
        {
            // Before ESPN's day turns over, last night's late games are still on yesterday's board.
            days.Add(EasternDay(now, 1));
        }

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        return await FetchAllAsync(client, leagues, days, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Fixture>> FetchAllAsync(HttpClient client, IReadOnlyList<SportsLeague> leagues, IReadOnlyList<string> days, CancellationToken cancellationToken)
    {
        var tasks = new List<Task<List<Fixture>>>();
        foreach (var league in leagues)
        {
            foreach (var day in days)
            {
                tasks.Add(FetchLeagueAsync(client, league, day, cancellationToken));
            }
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .SelectMany(r => r)
            .GroupBy(f => f.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(f => f.StartMs)
            .ToList();
    }

    private async Task<List<Fixture>> FetchLeagueAsync(HttpClient client, SportsLeague league, string day, CancellationToken cancellationToken)
    {
        try
        {
            var text = await client.GetStringAsync(new Uri(EspnScoreboard.ScoreboardUrl(league, day)), cancellationToken).ConfigureAwait(false);
            return EspnScoreboard.Parse(JsonNode.Parse(text), league);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("ESPN {League} scoreboard for {Day} could not be read: {Reason}", league.Id, day, ex.Message);
            return new List<Fixture>();
        }
    }

    private sealed record SlateSnapshot(DateTimeOffset FetchedAt, IReadOnlyList<Fixture> Fixtures);
}

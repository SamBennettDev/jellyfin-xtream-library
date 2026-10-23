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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// Fetches default guides (such as epgshare01's) and matches the lineup's channels to them where there is no
/// doubt, so channels the provider gives no guide for still show what is on. Matching is by id, by a local station's
/// call sign, or by an exact name (see <see cref="GuideMatcher"/>); never a fuzzy guess. Only the programmes of
/// matched channels are kept, so the half-gigabyte local-stations guide ends up as a few hundred stations' listings.
/// Fetching runs in the background, retries on a growing delay when a guide cannot be reached, and a lineup that gains
/// a station fetches again so the new station has listings today, not tomorrow.
/// </summary>
public sealed partial class DefaultGuideService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILogger<DefaultGuideService> _logger;
    private readonly ImportBackoff _backoff = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, SourceSnapshot> _sources = new(StringComparer.Ordinal);
    private IReadOnlyList<ProviderChannel> _providerChannels = Array.Empty<ProviderChannel>();
    private volatile Dictionary<string, GuideMatch> _matches = new Dictionary<string, GuideMatch>(StringComparer.Ordinal);
    private int _running;
    private bool _loaded;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultGuideService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="appPaths">The Jellyfin application paths (the matched guide is kept beside the other plugin data).</param>
    /// <param name="logger">The logger.</param>
    public DefaultGuideService(IHttpClientFactory httpClientFactory, IServerApplicationPaths appPaths, ILogger<DefaultGuideService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Raised when new guide data has been fetched, so cached guides built from it can be dropped.
    /// </summary>
    public event EventHandler? Updated;

    /// <summary>
    /// Gets the current matches, by tuner channel id.
    /// </summary>
    public IReadOnlyDictionary<string, GuideMatch> Matches => _matches;

    /// <summary>
    /// Gets the configured guide addresses (the built-in list when none are set).
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <returns>The addresses, in order.</returns>
    public static IReadOnlyList<string> UrlsOf(PressboxSettings settings)
    {
        var text = string.IsNullOrWhiteSpace(settings.DefaultGuideUrls) ? PressboxSettings.DefaultGuideList : settings.DefaultGuideUrls;
        return text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Tells the service what the lineup is now: matches it against the guides held, and starts a background fetch
    /// when a guide is due or the lineup has gained stations the held listings lack.
    /// </summary>
    /// <param name="channels">The lineup.</param>
    public void Update(IReadOnlyList<LiveStreamInfo> channels)
    {
        var settings = Plugin.Instance.Configuration.Pressbox;
        if (!settings.EnableDefaultGuide)
        {
            _matches = new Dictionary<string, GuideMatch>(StringComparer.Ordinal);
            return;
        }

        _providerChannels = channels
            .Select(c => new ProviderChannel(
                XtreamTunerHost.BuildChannelId(c.ProviderIndex, c.StreamId),
                c.ProviderIndex.ToString(CultureInfo.InvariantCulture),
                c.EpgChannelId ?? string.Empty,
                c.Name))
            .ToList();
        EnsureLoaded();
        Rematch();
        if (IsAnythingDue(settings))
        {
            StartBackgroundRefresh();
        }
    }

    /// <summary>
    /// Gets the programmes of a guide channel that were kept.
    /// </summary>
    /// <param name="guideChannelId">The guide channel.</param>
    /// <returns>Its programmes, or none.</returns>
    public IReadOnlyList<XmltvProgramme> ProgrammesFor(string guideChannelId)
    {
        lock (_gate)
        {
            foreach (var s in _sources.Values)
            {
                if (s.Programmes.TryGetValue(guideChannelId, out var list))
                {
                    return list;
                }
            }
        }

        return Array.Empty<XmltvProgramme>();
    }


    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.CultureInvariant | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TitleRegex();

    /// <summary>
    /// Finds which tuner channels already have programmes in an XMLTV document under construction.
    /// </summary>
    /// <param name="sb">The XMLTV.</param>
    /// <param name="programmesStart">Where its programmes begin.</param>
    /// <returns>The channel ids with at least one programme.</returns>
    internal static HashSet<string> ChannelsWithProgrammes(StringBuilder sb, int programmesStart)
    {
        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in ProgrammeChannelRegex().Matches(sb.ToString(programmesStart, sb.Length - programmesStart)))
        {
            have.Add(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
        }

        return have;
    }

    /// <summary>
    /// Lists what the default guides have on for matched channels in a time window, for finding which channel shows a game.
    /// </summary>
    /// <param name="fromMs">Window start, Unix milliseconds.</param>
    /// <param name="toMs">Window end, Unix milliseconds.</param>
    /// <returns>Listings keyed by tuner channel id.</returns>
    public IReadOnlyList<GuideListing> ListingsAround(long fromMs, long toMs)
    {
        var result = new List<GuideListing>();
        foreach (var (channelId, match) in _matches)
        {
            foreach (var p in ProgrammesFor(match.GuideId))
            {
                if (p.StopMs < fromMs || p.StartMs > toMs)
                {
                    continue;
                }

                var t = TitleRegex().Match(p.InnerXml);
                if (t.Success)
                {
                    result.Add(new GuideListing(channelId, System.Net.WebUtility.HtmlDecode(t.Groups[1].Value), p.StartMs, p.StopMs));
                }
            }
        }

        return result;
    }

    [GeneratedRegex("<programme\\s[^>]*?channel=\"([^\"]*)\"", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ProgrammeChannelRegex();

    /// <summary>
    /// Gets the name and logo a matched channel takes from its default guide, when that is switched on.
    /// </summary>
    /// <param name="channelId">The tuner channel id.</param>
    /// <returns>The guide's name and logo (each null when there is nothing to override with).</returns>
    public (string? Name, string? Logo) OverlayFor(string channelId)
    {
        if (!Plugin.Instance.Configuration.Pressbox.DefaultGuideOverridesNames
            || !_matches.TryGetValue(channelId, out var m))
        {
            return (null, null);
        }

        return (string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, string.IsNullOrWhiteSpace(m.Logo) ? null : m.Logo);
    }

    /// <summary>
    /// Appends the default guide's listings for every matched channel the guide so far has no programmes for.
    /// </summary>
    /// <param name="sb">The XMLTV being built; its programmes begin at <paramref name="programmesStart"/>.</param>
    /// <param name="programmesStart">Where the programmes begin in <paramref name="sb"/>.</param>
    /// <returns>How many programmes were added.</returns>
    public int AppendFallbackProgrammes(StringBuilder sb, int programmesStart)
    {
        var matches = _matches;
        if (matches.Count == 0)
        {
            return 0;
        }

        var have = ChannelsWithProgrammes(sb, programmesStart);
        var written = 0;
        foreach (var (channelId, match) in matches)
        {
            if (have.Contains(channelId))
            {
                continue;
            }

            var attr = System.Security.SecurityElement.Escape(channelId);
            foreach (var p in ProgrammesFor(match.GuideId).OrderBy(x => x.StartMs))
            {
                sb.Append(CultureInfo.InvariantCulture, $"  <programme start=\"{p.StartAttr}\" stop=\"{p.StopAttr}\" channel=\"{attr}\">{p.InnerXml}</programme>\n");
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// Describes each guide: when it was fetched, how many channels and listings it holds, and any failure.
    /// </summary>
    /// <returns>One entry per configured guide.</returns>
    public IReadOnlyList<DefaultGuideStatus> Status()
    {
        var settings = Plugin.Instance.Configuration.Pressbox;
        var matches = _matches;
        var result = new List<DefaultGuideStatus>();
        lock (_gate)
        {
            foreach (var url in UrlsOf(settings))
            {
                _sources.TryGetValue(url, out var s);
                var failure = _backoff.LastFailure(url);
                result.Add(new DefaultGuideStatus(
                    url,
                    s?.FetchedAt,
                    s?.Channels.Count ?? 0,
                    s?.Programmes.Values.Sum(p => p.Count) ?? 0,
                    matches.Values.Count(m => m.SourceId == url),
                    failure?.Error,
                    failure?.RetryAt));
            }
        }

        return result;
    }

    /// <summary>
    /// Fetches every guide now, ignoring how fresh it is and any retry delay.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when every guide has been tried.</returns>
    public async Task RefreshNowAsync(CancellationToken cancellationToken)
    {
        EnsureLoaded();
        await RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Matches a channel to a guide channel by hand (or removes the pick).
    /// </summary>
    /// <param name="channelId">The tuner channel id.</param>
    /// <param name="guideChannelId">The guide channel id, or empty to remove the pick.</param>
    public void SetManualMatch(string channelId, string guideChannelId)
    {
        var settings = Plugin.Instance.Configuration.Pressbox;
        var picks = ParseManual(settings.GuideManualMatches);
        if (string.IsNullOrWhiteSpace(guideChannelId))
        {
            picks.Remove(channelId);
        }
        else
        {
            picks[channelId] = guideChannelId.Trim();
        }

        settings.GuideManualMatches = string.Join('\n', picks.Select(kv => kv.Key + "=" + kv.Value));
        Plugin.Instance.SaveConfiguration();
        Rematch();
        StartBackgroundRefresh();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Searches the guides' channels by name or id, for picking a match by hand.
    /// </summary>
    /// <param name="query">What to look for.</param>
    /// <param name="limit">The most results.</param>
    /// <returns>Matching guide channels.</returns>
    public IReadOnlyList<GuideChannel> Search(string query, int limit = 50)
    {
        var q = query.Trim();
        if (q.Length == 0)
        {
            return Array.Empty<GuideChannel>();
        }

        lock (_gate)
        {
            return _sources
                .SelectMany(kv => kv.Value.Channels.Select(c => new GuideChannel(c.Id, c.Names, c.Icon, kv.Key)))
                .Where(c => c.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Names.Any(n => n.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .Take(limit)
                .ToList();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fileLock.Dispose();
    }

    private static Dictionary<string, string> ParseManual(string text)
    {
        var picks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && eq < line.Length - 1)
            {
                picks[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return picks;
    }

    private string CachePath => Path.Combine(_appPaths.DataPath, "xtream-library", "default-guide.json");

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            try
            {
                if (File.Exists(CachePath))
                {
                    var saved = JsonSerializer.Deserialize<List<SourceSnapshot>>(File.ReadAllText(CachePath, Encoding.UTF8), JsonOptions);
                    foreach (var s in saved ?? new List<SourceSnapshot>())
                    {
                        _sources[s.Url] = s;
                    }

                    _logger.LogInformation("Loaded {Count} default guide(s) from the cache", _sources.Count);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The default guide cache could not be read; it will be fetched again");
            }
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string json;
            lock (_gate)
            {
                json = JsonSerializer.Serialize(_sources.Values.ToList(), JsonOptions);
            }

            var path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The default guide cache could not be written");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private List<GuideChannel> AllGuideChannels(string? exceptUrl = null)
    {
        lock (_gate)
        {
            return _sources
                .Where(kv => kv.Key != exceptUrl)
                .SelectMany(kv => kv.Value.Channels.Select(c => new GuideChannel(c.Id, c.Names, c.Icon, kv.Key)))
                .ToList();
        }
    }

    private void Rematch()
    {
        var settings = Plugin.Instance.Configuration.Pressbox;
        var guide = AllGuideChannels();
        var matches = new Dictionary<string, GuideMatch>(StringComparer.Ordinal);
        if (guide.Count > 0)
        {
            foreach (var m in GuideMatcher.ComputeGuideMatches(_providerChannels, guide))
            {
                matches[m.ChannelKey] = m;
            }

            // A hand pick always wins; the guide's name is used only for a default guide's entry.
            foreach (var (channelId, guideId) in ParseManual(settings.GuideManualMatches))
            {
                var g = guide.FirstOrDefault(x => string.Equals(x.Id, guideId, StringComparison.Ordinal));
                if (g is not null)
                {
                    matches[channelId] = new GuideMatch(channelId, g.Id, g.SourceId, GuideMatchRule.Manual, g.Names.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim(), string.IsNullOrEmpty(g.Icon) ? null : g.Icon);
                }
            }
        }

        _matches = matches;
    }

    private bool IsAnythingDue(PressboxSettings settings)
    {
        var matches = _matches;
        var refresh = TimeSpan.FromHours(settings.DefaultGuideRefreshHours);
        foreach (var url in UrlsOf(settings))
        {
            if (!_backoff.IsDue(url, url))
            {
                continue;
            }

            SourceSnapshot? s;
            lock (_gate)
            {
                _sources.TryGetValue(url, out s);
            }

            if (s is null || DateTimeOffset.UtcNow - s.FetchedAt > refresh)
            {
                return true;
            }

            // A lineup that gained a station the held listings lack fetches again.
            if (matches.Values.Any(m => m.SourceId == url && !s.Wanted.Contains(m.GuideId)))
            {
                return true;
            }
        }

        return false;
    }

    private void StartBackgroundRefresh()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(force: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Default guide refresh failed");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance.Configuration.Pressbox;
        var refresh = TimeSpan.FromHours(settings.DefaultGuideRefreshHours);
        var changed = false;
        foreach (var url in UrlsOf(settings))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceSnapshot? existing;
            lock (_gate)
            {
                _sources.TryGetValue(url, out existing);
            }

            var matches = _matches;
            var behind = existing is not null && matches.Values.Any(m => m.SourceId == url && !existing.Wanted.Contains(m.GuideId));
            var due = existing is null || DateTimeOffset.UtcNow - existing.FetchedAt > refresh || behind;
            if (!force && (!due || !_backoff.IsDue(url, url)))
            {
                continue;
            }

            try
            {
                var snapshot = await FetchAsync(url, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _sources[url] = snapshot;
                }

                _backoff.RecordSuccess(url);
                changed = true;
                Rematch();
                _logger.LogInformation(
                    "Default guide {Url}: {Channels} channels, {Programmes} listings kept for {Wanted} matched channels",
                    url,
                    snapshot.Channels.Count,
                    snapshot.Programmes.Values.Sum(p => p.Count),
                    snapshot.Wanted.Count);
            }
            catch (Exception ex) when (ex is HttpRequestException or XmltvGuideException or TaskCanceledException or IOException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _backoff.RecordFailure(url, url, ex.Message);
                var failure = _backoff.LastFailure(url);
                _logger.LogWarning("Default guide {Url} could not be fetched ({Reason}); trying again after {RetryAt:u}", url, ex.Message, failure?.RetryAt);
            }
        }

        if (changed)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<SourceSnapshot> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var manualIds = new HashSet<string>(ParseManual(config.Pressbox.GuideManualMatches).Values, StringComparer.Ordinal);
        var others = AllGuideChannels(url);
        var providerChannels = _providerChannels;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var earliest = nowMs - (3_600_000L);
        var latest = nowMs + (config.EpgDaysToFetch * 86_400_000L);

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(TimeSpan.FromMinutes(30));

        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Jellyfin-Xtream-Library");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, overall.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(overall.Token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var programmes = new Dictionary<string, List<XmltvProgramme>>(StringComparer.Ordinal);
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            var channels = await XmltvGuideReader.ReadAsync(
                stream,
                found =>
                {
                    var guide = new List<GuideChannel>(others);
                    guide.AddRange(found.Select(c => new GuideChannel(c.Id, c.Names, c.Icon, url)));
                    foreach (var m in GuideMatcher.ComputeGuideMatches(providerChannels, guide))
                    {
                        if (m.SourceId == url)
                        {
                            wanted.Add(m.GuideId);
                        }
                    }

                    foreach (var id in manualIds)
                    {
                        if (found.Any(c => c.Id == id))
                        {
                            wanted.Add(id);
                        }
                    }

                    return id => wanted.Contains(id);
                },
                p =>
                {
                    if (p.StopMs < earliest || p.StartMs > latest)
                    {
                        return;
                    }

                    if (!programmes.TryGetValue(p.GuideChannelId, out var list))
                    {
                        list = [];
                        programmes[p.GuideChannelId] = list;
                    }

                    list.Add(p);
                },
                XmltvGuideReader.DefaultMaxBytes,
                overall.Token).ConfigureAwait(false);

            return new SourceSnapshot
            {
                Url = url,
                FetchedAt = DateTimeOffset.UtcNow,
                Channels = channels.ToList(),
                Programmes = programmes,
                Wanted = wanted,
            };
        }
    }

    /// <summary>
    /// What is kept of one fetched guide.
    /// </summary>
    internal sealed class SourceSnapshot
    {
        /// <summary>Gets or sets the guide address.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Gets or sets when it was fetched.</summary>
        public DateTimeOffset FetchedAt { get; set; }

        /// <summary>Gets or sets every channel entry of the guide (small, so any station can be picked by hand).</summary>
        public List<XmltvChannel> Channels { get; set; } = new();

        /// <summary>Gets or sets the kept programmes, by guide channel id.</summary>
        public Dictionary<string, List<XmltvProgramme>> Programmes { get; set; } = new();

        /// <summary>Gets or sets the guide channels that were wanted when it was fetched.</summary>
        public HashSet<string> Wanted { get; set; } = new();
    }
}

/// <summary>
/// The state of one default guide.
/// </summary>
/// <param name="Url">Where it is fetched from.</param>
/// <param name="FetchedAt">When it was last fetched.</param>
/// <param name="Channels">How many channel entries it has.</param>
/// <param name="Programmes">How many listings are kept.</param>
/// <param name="MatchedChannels">How many of the lineup's channels are matched to it.</param>
/// <param name="Error">Why it could not be fetched, if it could not.</param>
/// <param name="RetryAt">When it will be tried again.</param>
public sealed record DefaultGuideStatus(string Url, DateTimeOffset? FetchedAt, int Channels, int Programmes, int MatchedChannels, string? Error, DateTimeOffset? RetryAt);

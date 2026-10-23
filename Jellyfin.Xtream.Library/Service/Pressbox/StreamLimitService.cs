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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// Holds each provider account to the number of simultaneous live streams it allows, and reports what the
/// panels say about their accounts. Providers on the same Xtream server and username share their strictest limit.
/// A stream takes a lease when it opens and gives it back when it closes.
/// </summary>
public sealed class StreamLimitService
{
    private readonly IXtreamClient _client;
    private readonly ILogger<StreamLimitService> _logger;
    private readonly StreamBudget _budget;
    private readonly ConcurrentDictionary<int, DetectedAccount> _detected = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitService"/> class.
    /// </summary>
    /// <param name="client">The Xtream API client, used to read the panels' account information.</param>
    /// <param name="activity">Notes when something is streamed, so a scheduled sync can wait.</param>
    /// <param name="logger">The logger.</param>
    public StreamLimitService(IXtreamClient client, PlaybackActivity activity, ILogger<StreamLimitService> logger)
    {
        _client = client;
        Activity = activity;
        _logger = logger;
        _budget = new StreamBudget(CurrentProviders);
    }

    /// <summary>
    /// Gets the playback activity tracker.
    /// </summary>
    public PlaybackActivity Activity { get; }

    /// <summary>
    /// Opens a stream on a provider: notes the activity and, when limits are on, takes a lease on the account.
    /// </summary>
    /// <param name="providerIndex">The provider's index in the configuration.</param>
    /// <param name="channelKey">What is being watched.</param>
    /// <returns>The lease to give back on close, or null when no limit applies.</returns>
    /// <exception cref="StreamLimitException">When the account has no free stream.</exception>
    public StreamLease? Open(int providerIndex, string channelKey)
    {
        Activity.Note();
        var config = Plugin.Instance.Configuration;
        if (!config.Pressbox.EnableStreamLimits)
        {
            return null;
        }

        var providerId = providerIndex.ToString(CultureInfo.InvariantCulture);
        try
        {
            return _budget.Reserve([(channelKey, providerId)])[0];
        }
        catch (StreamLimitException ex)
        {
            _logger.LogWarning("Refusing live stream {Channel}: {Reason}", channelKey, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Keeps a lease alive while its stream is still being watched, and notes that someone is watching.
    /// </summary>
    /// <param name="lease">The lease, or null when no limit applies (the viewing is still noted).</param>
    public void Hold(StreamLease? lease)
    {
        Activity.Note();
        if (lease is not null)
        {
            _budget.Touch(lease.Id);
        }
    }

    /// <summary>
    /// Gives a lease back.
    /// </summary>
    /// <param name="lease">The lease from <see cref="Open"/>; null does nothing.</param>
    public void Close(StreamLease? lease)
    {
        if (lease is not null)
        {
            _budget.Release(lease.Id);
        }
    }

    /// <summary>
    /// Reserves streams for a multiview's tiles, all or none.
    /// </summary>
    /// <param name="tiles">The (channel key, provider index) of each tile.</param>
    /// <returns>The leases, or an empty list when limits are off.</returns>
    /// <exception cref="StreamLimitException">When an account has no free stream.</exception>
    public IReadOnlyList<StreamLease> OpenMany(IReadOnlyList<(string Key, int ProviderIndex)> tiles)
    {
        Activity.Note();
        if (!Plugin.Instance.Configuration.Pressbox.EnableStreamLimits)
        {
            return Array.Empty<StreamLease>();
        }

        return _budget.Reserve(tiles.Select(t => (t.Key, t.ProviderIndex.ToString(CultureInfo.InvariantCulture))).ToList());
    }

    /// <summary>
    /// Gives several leases back.
    /// </summary>
    /// <param name="leases">The leases.</param>
    public void CloseMany(IEnumerable<StreamLease> leases)
    {
        foreach (var l in leases)
        {
            _budget.Release(l.Id);
        }
    }

    /// <summary>
    /// Reads every enabled provider's account from its panel: the maximum connections it allows and how many are in use.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One entry per provider.</returns>
    public async Task<IReadOnlyList<ProviderStreamStatus>> RefreshAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        for (var i = 0; i < config.Providers.Count; i++)
        {
            var p = config.Providers[i];
            if (!p.IsEnabled || string.IsNullOrEmpty(p.BaseUrl) || string.IsNullOrEmpty(p.Username))
            {
                continue;
            }

            try
            {
                var info = await _client.GetUserAndServerInfoAsync(Plugin.Instance.GetCreds(i), cancellationToken).ConfigureAwait(false);
                var max = info.UserInfo.MaxConnections;
                _detected[i] = new DetectedAccount(max > 0 ? max : null, info.UserInfo.ActiveCons, DateTimeOffset.UtcNow, null);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or Newtonsoft.Json.JsonException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _detected[i] = new DetectedAccount(null, 0, DateTimeOffset.UtcNow, ex.Message);
            }
        }

        return Status();
    }

    /// <summary>
    /// Describes every enabled provider's limit and use.
    /// </summary>
    /// <returns>One entry per provider.</returns>
    public IReadOnlyList<ProviderStreamStatus> Status()
    {
        var config = Plugin.Instance.Configuration;
        var result = new List<ProviderStreamStatus>();
        for (var i = 0; i < config.Providers.Count; i++)
        {
            var p = config.Providers[i];
            var id = i.ToString(CultureInfo.InvariantCulture);
            _detected.TryGetValue(i, out var d);
            result.Add(new ProviderStreamStatus(
                i,
                p.Name,
                p.StreamLimitMode,
                p.MaxStreams,
                d?.Max,
                d?.ActiveConnections,
                _budget.MaximumFor(id),
                _budget.ActiveFor(id),
                d?.Error));
        }

        return result;
    }

    private static string AccountOf(ProviderConfig p)
    {
        var origin = Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).ToUpperInvariant()
            : p.BaseUrl.ToUpperInvariant();
        return origin + "|" + p.Username.ToUpperInvariant();
    }

    private List<ProviderLimit> CurrentProviders()
    {
        var providers = Plugin.Instance.Configuration.Providers;
        var list = new List<ProviderLimit>(providers.Count);
        for (var i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            var mode = p.StreamLimitMode.ToUpperInvariant() switch
            {
                "LIMITED" => StreamLimitMode.Limited,
                "UNLIMITED" => StreamLimitMode.Unlimited,
                _ => StreamLimitMode.Auto,
            };
            _detected.TryGetValue(i, out var d);
            list.Add(new ProviderLimit(i.ToString(CultureInfo.InvariantCulture), AccountOf(p), mode, p.MaxStreams, d?.Max, !p.IsEnabled));
        }

        return list;
    }

    private sealed record DetectedAccount(int? Max, int ActiveConnections, DateTimeOffset CheckedAt, string? Error);
}

/// <summary>
/// What is known about one provider's stream allowance.
/// </summary>
/// <param name="ProviderIndex">The provider's index.</param>
/// <param name="Name">The provider's name.</param>
/// <param name="Mode">Auto, Limited or Unlimited.</param>
/// <param name="ConfiguredMax">The number set for Limited.</param>
/// <param name="DetectedMax">What the panel reported, when it did.</param>
/// <param name="ReportedActive">How many connections the panel says are in use (other apps count too).</param>
/// <param name="EffectiveMax">The allowance being enforced, or null when none.</param>
/// <param name="Active">Streams open through this plugin on the account.</param>
/// <param name="Error">Why the panel could not be asked, if it could not.</param>
public sealed record ProviderStreamStatus(
    int ProviderIndex,
    string Name,
    string Mode,
    int ConfiguredMax,
    int? DetectedMax,
    int? ReportedActive,
    int? EffectiveMax,
    int Active,
    string? Error);

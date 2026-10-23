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

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// How a provider's simultaneous-stream allowance is decided.
/// </summary>
public enum StreamLimitMode
{
    /// <summary>Use the provider's own reported maximum (Xtream <c>max_connections</c>) when known.</summary>
    Auto,

    /// <summary>Enforce the allowance the user chose.</summary>
    Limited,

    /// <summary>No allowance is enforced.</summary>
    Unlimited,
}

/// <summary>
/// The stream-limit settings and facts known about one provider.
/// </summary>
/// <param name="Id">Provider id (its index, as text).</param>
/// <param name="Account">The account the provider logs into, "origin|username"; providers sharing an account share its limit.</param>
/// <param name="Mode">How the allowance is decided.</param>
/// <param name="MaxStreams">The user's chosen allowance when <see cref="StreamLimitMode.Limited"/>.</param>
/// <param name="DetectedMax">What the panel reported (<c>max_connections</c>), when it did; zero or missing means unknown.</param>
/// <param name="Disabled">Whether the provider is switched off.</param>
public sealed record ProviderLimit(string Id, string Account, StreamLimitMode Mode, int? MaxStreams, int? DetectedMax, bool Disabled);

/// <summary>
/// A claim on one of a provider account's simultaneous streams.
/// </summary>
/// <param name="Id">Lease id.</param>
/// <param name="Key">What is being watched (channel key).</param>
/// <param name="ProviderId">The provider the stream is on.</param>
/// <param name="Account">The account that stream counts against.</param>
/// <param name="ExpiresAt">When the lease lapses unless renewed.</param>
public sealed record StreamLease(string Id, string Key, string ProviderId, string Account, DateTimeOffset ExpiresAt);

/// <summary>
/// Thrown when a provider account has no free stream.
/// </summary>
public sealed class StreamLimitException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitException"/> class.
    /// </summary>
    /// <param name="message">What to tell the viewer.</param>
    public StreamLimitException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitException"/> class.
    /// </summary>
    public StreamLimitException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamLimitException"/> class.
    /// </summary>
    /// <param name="message">What to tell the viewer.</param>
    /// <param name="innerException">The cause.</param>
    public StreamLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Keeps the number of simultaneous streams within what each provider account allows (a port of PressBox
/// <c>sessions.ts</c> and <c>providers.ts</c>). Sources with the same Xtream server and username share
/// their strictest configured limit. A lease that is not renewed lapses, so an abandoned reservation
/// frees its place (PressBox uses 90 seconds). Reservations of several channels are atomic.
/// </summary>
public sealed class StreamBudget
{
    /// <summary>
    /// Gets how long a lease lasts without renewal.
    /// </summary>
    public static TimeSpan LeaseTtl { get; } = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private readonly Dictionary<string, StreamLease> _leases = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<IReadOnlyList<ProviderLimit>> _providers;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamBudget"/> class.
    /// </summary>
    /// <param name="providers">Supplies the current providers and their limits.</param>
    /// <param name="clock">The clock; defaults to the system's.</param>
    public StreamBudget(Func<IReadOnlyList<ProviderLimit>> providers, Func<DateTimeOffset>? clock = null)
    {
        _providers = providers;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Gets the allowance of a provider's account: the strictest limit among the enabled providers that share
    /// it, or null when none is known (no limit enforced).
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <returns>The allowance, or null.</returns>
    public int? MaximumFor(string providerId)
    {
        var all = _providers();
        var p = all.FirstOrDefault(x => x.Id == providerId);
        if (p is null)
        {
            return null;
        }

        return MaximumForAccount(all, p.Account);
    }

    /// <summary>
    /// Gets how many streams are active on a provider's account.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <returns>The number of live leases on the account.</returns>
    public int ActiveFor(string providerId)
    {
        var account = _providers().FirstOrDefault(x => x.Id == providerId)?.Account;
        if (account is null)
        {
            return 0;
        }

        lock (_gate)
        {
            Expire();
            return _leases.Values.Count(l => l.Account == account);
        }
    }

    /// <summary>
    /// Reserves one to four streams atomically: either every one is granted, or none is.
    /// </summary>
    /// <param name="requests">What is being watched, as (key, provider id) pairs; keys must differ.</param>
    /// <param name="replace">Lease ids being replaced (released as the new ones are granted, and not counted against the limit).</param>
    /// <returns>The granted leases.</returns>
    /// <exception cref="StreamLimitException">When an account has no free stream, or the request is invalid.</exception>
    public IReadOnlyList<StreamLease> Reserve(IReadOnlyList<(string Key, string ProviderId)> requests, IReadOnlyCollection<string>? replace = null)
    {
        if (requests.Count is < 1 or > 4 || requests.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count() != requests.Count)
        {
            throw new StreamLimitException("Choose one to four different channels.");
        }

        var ignored = new HashSet<string>(replace ?? Array.Empty<string>(), StringComparer.Ordinal);
        var providers = _providers();
        lock (_gate)
        {
            Expire();
            var pending = new List<StreamLease>();
            foreach (var (key, providerId) in requests)
            {
                var p = providers.FirstOrDefault(x => x.Id == providerId)
                    ?? throw new StreamLimitException("Provider not found.");
                if (p.Disabled)
                {
                    throw new StreamLimitException("This provider is disabled.");
                }

                var max = MaximumForAccount(providers, p.Account);
                var active = _leases.Values.Count(l => l.Account == p.Account && !ignored.Contains(l.Id))
                    + pending.Count(l => l.Account == p.Account);
                if (max is not null && active >= max)
                {
                    throw new StreamLimitException(
                        $"This provider allows {max} simultaneous streams. Stop another channel or change its limit in the plugin settings.");
                }

                pending.Add(new StreamLease(Guid.NewGuid().ToString("N"), key, providerId, p.Account, _clock() + LeaseTtl));
            }

            foreach (var id in ignored)
            {
                _leases.Remove(id);
            }

            foreach (var l in pending)
            {
                _leases[l.Id] = l;
            }

            return pending;
        }
    }

    /// <summary>
    /// Renews a lease.
    /// </summary>
    /// <param name="id">The lease id.</param>
    /// <returns>True when the lease was still live and has been renewed.</returns>
    public bool Touch(string id)
    {
        lock (_gate)
        {
            Expire();
            if (!_leases.TryGetValue(id, out var l))
            {
                return false;
            }

            _leases[id] = l with { ExpiresAt = _clock() + LeaseTtl };
            return true;
        }
    }

    /// <summary>
    /// Releases a lease.
    /// </summary>
    /// <param name="id">The lease id.</param>
    public void Release(string id)
    {
        lock (_gate)
        {
            _leases.Remove(id);
        }
    }

    /// <summary>
    /// Releases every lease on a provider.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    public void ReleaseProvider(string providerId)
    {
        lock (_gate)
        {
            foreach (var key in _leases.Where(kv => kv.Value.ProviderId == providerId).Select(kv => kv.Key).ToList())
            {
                _leases.Remove(key);
            }
        }
    }

    private static int? MaximumForAccount(IReadOnlyList<ProviderLimit> all, string account)
    {
        var limits = all
            .Where(p => p.Account == account && !p.Disabled)
            .Select(p => p.Mode switch
            {
                StreamLimitMode.Unlimited => null,
                StreamLimitMode.Limited => p.MaxStreams,
                _ => p.DetectedMax,
            })
            .Where(n => n is > 0)
            .Select(n => n!.Value)
            .ToList();
        return limits.Count > 0 ? limits.Min() : null;
    }

    private void Expire()
    {
        var now = _clock();
        foreach (var key in _leases.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList())
        {
            _leases.Remove(key);
        }
    }
}

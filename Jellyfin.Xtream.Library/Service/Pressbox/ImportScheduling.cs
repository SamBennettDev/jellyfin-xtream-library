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

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// Keeps a guide or provider that failed to import from being asked for again straight away (a port of the
/// PressBox guide retry rules): 5 minutes, then 10, 20... up to 6 hours between tries, so an unreachable
/// address never hammers anyone. Asking by hand goes at once; a success, or a change of address, starts afresh.
/// </summary>
public sealed class ImportBackoff
{
    private static readonly TimeSpan First = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Max = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, Failure> _failures = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportBackoff"/> class.
    /// </summary>
    /// <param name="clock">The clock; defaults to the system's.</param>
    public ImportBackoff(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The delay before the next try, after a number of failures in a row.
    /// </summary>
    /// <param name="failuresSoFar">Consecutive failures so far (1 for the first).</param>
    /// <returns>The delay.</returns>
    public static TimeSpan RetryDelay(int failuresSoFar)
    {
        var exponent = Math.Max(0, failuresSoFar - 1);
        if (exponent >= 16)
        {
            return Max;
        }

        var ticks = First.Ticks * (1L << exponent);
        return TimeSpan.FromTicks(Math.Min(Max.Ticks, ticks));
    }

    /// <summary>
    /// Records a failure of an import.
    /// </summary>
    /// <param name="id">The guide or provider.</param>
    /// <param name="url">The address that failed; a different address later starts afresh.</param>
    /// <param name="error">Why it failed.</param>
    public void RecordFailure(string id, string url, string error)
    {
        _failures.AddOrUpdate(
            id,
            _ => new Failure(url, 1, _clock() + RetryDelay(1), error),
            (_, prior) =>
            {
                var count = prior.Url == url ? prior.Count + 1 : 1;
                return new Failure(url, count, _clock() + RetryDelay(count), error);
            });
    }

    /// <summary>
    /// Records a success, forgetting earlier failures.
    /// </summary>
    /// <param name="id">The guide or provider.</param>
    public void RecordSuccess(string id)
    {
        _failures.TryRemove(id, out _);
    }

    /// <summary>
    /// Whether an import may go now.
    /// </summary>
    /// <param name="id">The guide or provider.</param>
    /// <param name="currentUrl">The address it would fetch now; a changed address is always due.</param>
    /// <returns>True when not waiting out a failure.</returns>
    public bool IsDue(string id, string currentUrl)
    {
        if (!_failures.TryGetValue(id, out var f) || f.Url != currentUrl)
        {
            return true;
        }

        return _clock() >= f.RetryAt;
    }

    /// <summary>
    /// Why an import last failed and when it is tried again, if it did.
    /// </summary>
    /// <param name="id">The guide or provider.</param>
    /// <returns>The failure, or null.</returns>
    public (string Error, DateTimeOffset RetryAt, int Failures)? LastFailure(string id)
    {
        return _failures.TryGetValue(id, out var f) ? (f.Error, f.RetryAt, f.Count) : null;
    }

    private sealed record Failure(string Url, int Count, DateTimeOffset RetryAt, string Error);
}

/// <summary>
/// Remembers when something was last streamed, so a scheduled re-import never fights a viewer for the
/// provider's connection (a port of PressBox <c>activity.ts</c> and the quiet-minute rule of <c>refresh.ts</c>).
/// </summary>
public sealed class PlaybackActivity
{
    /// <summary>
    /// Gets how long playback must have been quiet before a refresh may start.
    /// </summary>
    public static TimeSpan QuietFor { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets how many one-minute waits a refresh gives way for before going ahead anyway.
    /// </summary>
    public static int MaxWaitMinutes => 30;

    private readonly Func<DateTimeOffset> _clock;
    private long _lastTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackActivity"/> class.
    /// </summary>
    /// <param name="clock">The clock; defaults to the system's.</param>
    public PlaybackActivity(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Notes that something is being streamed right now.
    /// </summary>
    public void Note()
    {
        System.Threading.Interlocked.Exchange(ref _lastTicks, _clock().UtcTicks);
    }

    /// <summary>
    /// How long it is since anything was streamed; effectively forever when nothing ever was.
    /// </summary>
    /// <returns>The idle time.</returns>
    public TimeSpan IdleFor()
    {
        var last = System.Threading.Interlocked.Read(ref _lastTicks);
        return last == 0 ? TimeSpan.MaxValue : _clock() - new DateTimeOffset(last, TimeSpan.Zero);
    }

    /// <summary>
    /// Whether a refresh should give way for one more minute: someone watched recently, and it has not
    /// waited as many minutes as it may.
    /// </summary>
    /// <param name="waitedMinutes">Minutes already waited.</param>
    /// <returns>True to wait another minute.</returns>
    public bool ShouldWait(int waitedMinutes)
    {
        return IdleFor() < QuietFor && waitedMinutes < MaxWaitMinutes;
    }

    /// <summary>
    /// How long until the first refresh after a start: when a day has passed since the last import, or a few
    /// minutes after boot if that is already due (or nothing has been imported). A restart should not re-download
    /// the whole provider.
    /// </summary>
    /// <param name="lastImportedAt">When the last import finished, or null if never.</param>
    /// <param name="now">The current time.</param>
    /// <param name="interval">The refresh interval.</param>
    /// <param name="minDelay">The least delay after boot.</param>
    /// <returns>The delay.</returns>
    public static TimeSpan FirstRefreshDelay(DateTimeOffset? lastImportedAt, DateTimeOffset now, TimeSpan interval, TimeSpan minDelay)
    {
        if (lastImportedAt is null)
        {
            return minDelay;
        }

        var due = lastImportedAt.Value + interval - now;
        return due > minDelay ? due : minDelay;
    }
}

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
using FluentAssertions;
using Jellyfin.Xtream.Library.Service.Pressbox;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service.Pressbox;

public class ImportSchedulingTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    public void RetryDelay_DoublesFromFiveMinutes(int failures, int minutes)
    {
        ImportBackoff.RetryDelay(failures).Should().Be(TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(40)]
    public void RetryDelay_StopsAtSixHours(int failures)
    {
        ImportBackoff.RetryDelay(failures).Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public void ImportBackoff_WaitsAfterAFailureButNotForADifferentAddress()
    {
        var clock = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
        var backoff = new ImportBackoff(() => clock);
        backoff.RecordFailure("g", "u1", "boom");
        backoff.IsDue("g", "u1").Should().BeFalse();
        backoff.IsDue("g", "u2").Should().BeTrue("a changed address is a new guide");
        clock = clock.AddMinutes(6);
        backoff.IsDue("g", "u1").Should().BeTrue();
        backoff.RecordSuccess("g");
        backoff.LastFailure("g").Should().BeNull();
    }

    [Fact]
    public void PlaybackActivity_WaitsForAQuietMinuteButNotForever()
    {
        var clock = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
        var activity = new PlaybackActivity(() => clock);
        activity.ShouldWait(0).Should().BeFalse("nobody has watched");
        activity.Note();
        activity.ShouldWait(0).Should().BeTrue();
        activity.ShouldWait(30).Should().BeFalse("it has waited long enough");
        clock = clock.AddSeconds(61);
        activity.ShouldWait(0).Should().BeFalse();
    }

    [Fact]
    public void FirstRefreshDelay_IsADayAfterTheLastImportButNeverSoonerThanTheMinimum()
    {
        var now = DateTimeOffset.Parse("2026-10-23T12:00:00Z");
        var day = TimeSpan.FromHours(24);
        var min = TimeSpan.FromMinutes(5);
        PlaybackActivity.FirstRefreshDelay(null, now, day, min).Should().Be(min);
        PlaybackActivity.FirstRefreshDelay(now.AddHours(-1), now, day, min).Should().Be(TimeSpan.FromHours(23));
        PlaybackActivity.FirstRefreshDelay(now.AddDays(-3), now, day, min).Should().Be(min);
    }
}

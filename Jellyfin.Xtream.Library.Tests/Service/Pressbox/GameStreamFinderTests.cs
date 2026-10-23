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
using System.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service.Pressbox;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service.Pressbox;

public class GameStreamFinderTests
{
    private static LineupChannel L(string key, string name, string group = "US| SPORTS", string tvg = "") => new(key, name, group, tvg);

    private static FixtureSide Side(string name, string shortName) => new(name, shortName, shortName[..3].ToUpperInvariant(), null, null);

    private static Fixture Game() => new()
    {
        Id = "nhl:1",
        League = "nhl",
        LeagueLabel = "NHL",
        Name = "Bruins at Flyers",
        StartMs = new DateTimeOffset(2026, 9, 24, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        State = "in",
        Away = Side("Boston Bruins", "Bruins"),
        Home = Side("Philadelphia Flyers", "Flyers"),
        Broadcasts = new() { "ESPN" },
    };

    [Fact]
    public void StreamsFor_FindsGameChannelsAndTheBroadcastNetwork()
    {
        var finder = new GameStreamFinder(new[]
        {
            L("game", "NHL | 01 - 7pm Bruins at Flyers"),
            L("stale", "US (ESPN+ 311) | NHL: Bruins vs. Flyers (2026-09-20 19:00:05)"),
            L("show", "End | Bruins and Flyers preview"),
            L("foreign", "Live | Bruins vs. Flyers | NHL.TV | PT: DAZN PPV 15", "PT| DAZN PPV"),
            L("espn", "US| ESPN HD"),
            L("plus", "ESPN PLUS 07:"),
            L("news", "US| ESPNEWS"),
            L("other", "NHL | 02 - 7pm Devils at Rangers"),
        });

        var keys = finder.StreamsFor(Game(), Array.Empty<GuideListing>()).Select(s => s.Channel.Key).ToList();

        keys.First().Should().Be("game");
        keys.Should().Contain("espn");
        keys.IndexOf("espn").Should().BeLessThan(keys.IndexOf("foreign"), "another language's feed ranks below an English network");
        keys.Should().NotContain(new[] { "stale", "show", "plus", "news", "other" });
    }

    [Fact]
    public void StreamsFor_UsesGuideListingsToFindTheChannel()
    {
        var fixture = Game();
        var finder = new GameStreamFinder(new[] { L("g", "US| TNT HD", "US| SPORTS", "TNT.us") });
        var found = finder.StreamsFor(
            fixture,
            new[] { new GuideListing("TNT.us", "Bruins at Flyers", fixture.StartMs - 60000, fixture.StartMs + (3 * 3600000)) });

        found.FirstOrDefault()?.Why.Should().Be("Guide: Bruins at Flyers");
    }
}

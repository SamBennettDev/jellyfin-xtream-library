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
using FluentAssertions;
using Jellyfin.Xtream.Library.Service.Pressbox;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service.Pressbox;

public class TrendingRankerTests
{
    private const string Rss = "<rss><channel><item><title>Bruins Flyers</title><ht:approx_traffic>200,000+</ht:approx_traffic><ht:news_item><ht:news_item_title><![CDATA[Bruins beat Flyers &amp; more]]></ht:news_item_title></ht:news_item></item><item><title>Other thing</title></item></channel></rss>";

    private static LineupChannel L(string key, string name, string group = "US| SPORTS", string tvg = "") => new(key, name, group, tvg);

    [Fact]
    public void ParseTrendsRss_ReadsTermsAndTraffic()
    {
        var terms = TrendingRanker.ParseTrendsRss(Rss, "US");
        terms.Should().HaveCount(2);
        terms[0].Traffic.Should().Be(200000);
        terms[1].Traffic.Should().Be(1000, "a term with no traffic figure gets a default");
    }

    [Fact]
    public void TrendScore_RewardsATitleThatMatchesATrend()
    {
        var terms = TrendingRanker.ParseTrendsRss(Rss, "US");
        var score = TrendingRanker.TrendScore("Bruins at Flyers", terms);
        score.Score.Should().BeInRange(25, 27);
        score.Geo.Should().Be("US");
        TrendingRanker.TrendScore("Cooking Hour", terms).Score.Should().Be(0);
    }

    [Theory]
    [InlineData("US| NBC HD", 30)]
    [InlineData("Shopping Channel", 6)]
    [InlineData("CBS Sports Network", 6)]
    [InlineData("US| TNT", 24)]
    public void NetworkWeight_FavoursTheBigNetworks(string name, int weight)
    {
        TrendingRanker.NetworkWeight(name).Should().Be(weight);
    }

    [Fact]
    public void Rank_PutsTheTrendingLiveGameFirstAndDropsFiller()
    {
        var terms = TrendingRanker.ParseTrendsRss(Rss, "US");
        var game = new Fixture
        {
            Id = "nhl:1",
            League = "nhl",
            LeagueLabel = "NHL",
            Name = "Bruins at Flyers",
            StartMs = new DateTimeOffset(2026, 9, 24, 23, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            State = "in",
            Away = new FixtureSide("Boston Bruins", "Bruins", "BRU", null, null),
            Home = new FixtureSide("Philadelphia Flyers", "Flyers", "FLY", null, null),
            Broadcasts = new() { "ESPN" },
        };
        var lineup = new[]
        {
            L("game", "NHL | 01 - 7pm Bruins at Flyers"),
            L("nbc", "US| NBC HD", "US| NEWS", "NBC.us"),
            L("shop", "Shopping", "US| SHOP", "SHOP.us"),
            L("tbs", "US| TBS", "US| ENT", "TBS.us"),
        };
        var nowMs = game.StartMs + 60000;
        var onNow = new[]
        {
            new GuideListing("NBC.us", "Evening News", nowMs - 1000, nowMs + 1000),
            new GuideListing("SHOP.us", "Paid Programming", nowMs - 1000, nowMs + 1000),
            new GuideListing("TBS.us", "Bruins at Flyers Pregame", nowMs - 1000, nowMs + 1000),
        };

        var ranked = TrendingRanker.Rank(nowMs, lineup, new[] { game }, onNow, Array.Empty<GuideListing>(), terms, new HashSet<string>(), Array.Empty<string>());

        ranked[0].Id.Should().Be("game:nhl:1");
        ranked[0].Reasons.Should().Contain("Live NHL");
        ranked[0].Reasons.Should().Contain("Trending");
        ranked.Any(r => r.Title == "Paid Programming").Should().BeFalse("filler is dropped");
        ranked.Any(r => r.Title == "Evening News").Should().BeTrue();
    }
}

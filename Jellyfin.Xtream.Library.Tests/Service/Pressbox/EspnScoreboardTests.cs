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

using System.Linq;
using System.Text.Json.Nodes;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service.Pressbox;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service.Pressbox;

public class EspnScoreboardTests
{
    private const string Body = """
    {"events":[{"id":"401","name":"Boston Bruins at Philadelphia Flyers","date":"2026-09-24T23:00Z","status":{"type":{"state":"in","shortDetail":"3rd 5:12"}},
     "competitions":[{"id":"401","date":"2026-09-24T23:00Z","status":{"type":{"state":"in","shortDetail":"3rd 5:12"}},
       "broadcasts":[{"names":["ESPN","ESPN+"]}],"geoBroadcasts":[{"media":{"shortName":"NHL Net"}}],
       "competitors":[{"homeAway":"home","score":"3","team":{"displayName":"Philadelphia Flyers","shortDisplayName":"Flyers","abbreviation":"PHI","logo":"flyers.png"}},
                      {"homeAway":"away","score":"2","team":{"displayName":"Boston Bruins","shortDisplayName":"Bruins","abbreviation":"BOS"}}]}]},
     {"id":"402","name":"Later game","date":"2026-09-25T23:00Z","status":{"type":{"state":"pre","shortDetail":"9/25 - 7:00 PM EDT"}},
     "competitions":[{"id":"402","competitors":[{"homeAway":"home","score":"0","team":{"displayName":"A City Aces","shortDisplayName":"Aces"}},{"homeAway":"away","score":"0","team":{"displayName":"B City Bees","shortDisplayName":"Bees"}}]}]}]}
    """;

    private static SportsLeague Nhl => EspnScoreboard.Leagues.First(l => l.Id == "nhl");

    [Fact]
    public void Parse_ReadsLiveAndUpcomingGames()
    {
        var fixtures = EspnScoreboard.Parse(JsonNode.Parse(Body), Nhl);

        fixtures.Should().HaveCount(2);
        fixtures[0].Id.Should().Be("nhl:401");
        fixtures[0].State.Should().Be("in");
        fixtures[0].Status.Should().Be("3rd 5:12");
        fixtures[0].Home!.Score.Should().Be(3);
        fixtures[0].Away!.Short.Should().Be("Bruins");
        fixtures[0].Home!.Logo.Should().Be("flyers.png");
        fixtures[0].Broadcasts.Should().Equal("ESPN", "ESPN+", "NHL Net");
        fixtures[0].StartMs.Should().Be(new System.DateTimeOffset(2026, 9, 24, 23, 0, 0, System.TimeSpan.Zero).ToUnixTimeMilliseconds());
        fixtures[1].State.Should().Be("pre");
        fixtures[1].Home!.Score.Should().BeNull("a game that has not started has no score to show");
    }

    [Fact]
    public void ScoreboardUrl_UsesTheLeaguePathAndDay()
    {
        EspnScoreboard.ScoreboardUrl(Nhl, "2026-09-24")
            .Should().Be("https://site.api.espn.com/apis/site/v2/sports/hockey/nhl/scoreboard?dates=20260924&limit=300");
    }

    [Fact]
    public void Parse_ReturnsNothingForAnEmptyBody()
    {
        EspnScoreboard.Parse(null, Nhl).Should().BeEmpty();
    }
}

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
using System.Text.Json.Nodes;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// A sports league ESPN's public site API serves.
/// </summary>
/// <param name="Id">Short id.</param>
/// <param name="Label">Display label.</param>
/// <param name="Sport">Grouping, e.g. "Football".</param>
/// <param name="Path">Path under the ESPN API, e.g. "football/nfl".</param>
/// <param name="Extra">Extra query ESPN needs to list the whole division, not a featured slice.</param>
public sealed record SportsLeague(string Id, string Label, string Sport, string Path, string? Extra = null);

/// <summary>
/// One side of a fixture: a team or an athlete.
/// </summary>
/// <param name="Name">Full name.</param>
/// <param name="Short">Short name.</param>
/// <param name="Abbr">Abbreviation.</param>
/// <param name="Logo">Team logo, or an athlete's headshot.</param>
/// <param name="Score">Current score, or null before the game.</param>
public sealed record FixtureSide(string Name, string Short, string Abbr, string? Logo, int? Score);

/// <summary>
/// A game, match, fight card or event as listed by ESPN.
/// </summary>
public sealed class Fixture
{
    /// <summary>Gets or sets the unique id, "league:eventId".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind: "match" (two sides), "field" (a leaderboard) or "card" (a fight card).</summary>
    public string Kind { get; set; } = "match";

    /// <summary>Gets or sets the league id.</summary>
    public string League { get; set; } = string.Empty;

    /// <summary>Gets or sets the league label.</summary>
    public string LeagueLabel { get; set; } = string.Empty;

    /// <summary>Gets or sets the event name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the start time in Unix milliseconds.</summary>
    public long StartMs { get; set; }

    /// <summary>Gets or sets the state: "pre" (scheduled), "in" (live) or "post" (final).</summary>
    public string State { get; set; } = "pre";

    /// <summary>Gets or sets ESPN's short status line.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the home side, when there are two sides.</summary>
    public FixtureSide? Home { get; set; }

    /// <summary>Gets or sets the away side, when there are two sides.</summary>
    public FixtureSide? Away { get; set; }

    /// <summary>Gets or sets the networks showing it.</summary>
    public List<string> Broadcasts { get; set; } = new();
}

/// <summary>
/// ESPN league catalogue and scoreboard parsing (a port of PressBox <c>sports.ts</c>). The scoreboard
/// JSON is loosely shaped, so it is read defensively.
/// </summary>
public static class EspnScoreboard
{
    /// <summary>
    /// Gets the base address of ESPN's public site API.
    /// </summary>
    public const string BaseUrl = "https://site.api.espn.com/apis/site/v2/sports";

    /// <summary>
    /// Gets the leagues this integration knows.
    /// </summary>
    public static IReadOnlyList<SportsLeague> Leagues { get; } =
    [
        new("nfl", "NFL", "Football", "football/nfl"),
        new("ncaaf", "College Football", "Football", "football/college-football", "groups=80"),
        new("ufl", "UFL", "Football", "football/ufl"),
        new("nba", "NBA", "Basketball", "basketball/nba"),
        new("wnba", "WNBA", "Basketball", "basketball/wnba"),
        new("ncaam", "Men's College Basketball", "Basketball", "basketball/mens-college-basketball", "groups=50"),
        new("ncaaw", "Women's College Basketball", "Basketball", "basketball/womens-college-basketball", "groups=50"),
        new("mlb", "MLB", "Baseball", "baseball/mlb"),
        new("cbase", "College Baseball", "Baseball", "baseball/college-baseball"),
        new("nhl", "NHL", "Hockey", "hockey/nhl"),
        new("ncaah", "College Hockey", "Hockey", "hockey/mens-college-hockey"),
        new("pga", "PGA Tour", "Golf", "golf/pga"),
        new("lpga", "LPGA", "Golf", "golf/lpga"),
        new("liv", "LIV Golf", "Golf", "golf/liv"),
        new("nascar", "NASCAR Cup", "Motorsport", "racing/nascar-premier"),
        new("indycar", "IndyCar", "Motorsport", "racing/irl"),
        new("f1", "Formula 1", "Motorsport", "racing/f1"),
        new("ufc", "UFC", "Combat", "mma/ufc"),
        new("atp", "ATP Tennis", "Tennis", "tennis/atp"),
        new("wta", "WTA Tennis", "Tennis", "tennis/wta"),
        new("mls", "MLS", "Soccer", "soccer/usa.1"),
        new("nwsl", "NWSL", "Soccer", "soccer/usa.nwsl"),
        new("ligamx", "Liga MX", "Soccer", "soccer/mex.1"),
        new("epl", "Premier League", "Soccer", "soccer/eng.1"),
        new("ucl", "Champions League", "Soccer", "soccer/uefa.champions"),
        new("wvb", "College Volleyball", "Other", "volleyball/womens-college-volleyball"),
        new("pll", "Premier Lacrosse", "Other", "lacrosse/pll"),
    ];

    /// <summary>
    /// Gets the leagues followed by default.
    /// </summary>
    public static IReadOnlyList<string> DefaultLeagues { get; } =
        ["nfl", "ncaaf", "nba", "wnba", "ncaam", "mlb", "nhl", "pga", "nascar", "f1", "ufc", "mls"];

    /// <summary>
    /// Builds the scoreboard URL for one league and one US Eastern day (ESPN's own days).
    /// </summary>
    /// <param name="league">The league.</param>
    /// <param name="day">The day as yyyy-MM-dd.</param>
    /// <returns>The URL.</returns>
    public static string ScoreboardUrl(SportsLeague league, string day)
    {
        var url = BaseUrl + "/" + league.Path + "/scoreboard?dates=" + day.Replace("-", string.Empty, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(league.Extra))
        {
            url += "&" + league.Extra;
        }

        return url + "&limit=300";
    }

    /// <summary>
    /// Parses one league's scoreboard into fixtures: two-sided matches, fight cards (headlined by the main event),
    /// tennis singles and fields (golf, racing).
    /// </summary>
    /// <param name="body">The scoreboard JSON.</param>
    /// <param name="league">The league it belongs to.</param>
    /// <returns>The fixtures found.</returns>
    public static List<Fixture> Parse(JsonNode? body, SportsLeague league)
    {
        var fixtures = new List<Fixture>();
        var events = body?["events"] as JsonArray;
        if (events is null)
        {
            return fixtures;
        }

        foreach (var ev in events)
        {
            if (ev is null)
            {
                continue;
            }

            if (ev["groupings"] is JsonArray groupings)
            {
                foreach (var group in groupings)
                {
                    var draw = Str(group?["grouping"]?["displayName"]);
                    if (!draw.Contains("singles", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var comp in Arr(group?["competitions"]))
                    {
                        var competitors = Arr(comp?["competitors"]).ToList();
                        if (competitors.Count < 2 || competitors[0]?["athlete"] is null || competitors[1]?["athlete"] is null)
                        {
                            continue;
                        }

                        var f = Base(ev, comp, league);
                        if (f is null)
                        {
                            continue;
                        }

                        f.Name = Str(competitors[0]?["athlete"]?["displayName"]) + " v " + Str(competitors[1]?["athlete"]?["displayName"]);
                        f.Away = AthleteSide(competitors[0]);
                        f.Home = AthleteSide(competitors[1]);
                        fixtures.Add(f);
                    }
                }

                continue;
            }

            var comps = Arr(ev["competitions"]).ToList();

            // A fight card: many two-fighter bouts; the last is the main event.
            if (comps.Count > 1 && comps.All(c => Arr(c?["competitors"]).Count() == 2 && Str(Arr(c?["competitors"]).First()?["type"]) == "athlete"))
            {
                var main = comps[^1];
                var fighters = Arr(main?["competitors"]).ToList();
                var card = Base(ev, comps[0], league);
                if (card is not null)
                {
                    card.Kind = "card";
                    card.Away = AthleteSide(fighters[0]);
                    card.Home = AthleteSide(fighters[1]);
                    var evState = Str(ev["status"]?["type"]?["state"]);
                    if (evState.Length > 0)
                    {
                        card.State = evState is "in" or "post" ? evState : "pre";
                    }

                    fixtures.Add(card);
                }

                continue;
            }

            // Multi-session events: what's on now, else the latest with results, else the next to come.
            var comp2 = comps.FirstOrDefault(c => StateOf(c) == "in")
                ?? comps.AsEnumerable().Reverse().FirstOrDefault(c => StateOf(c) == "post" && Arr(c?["competitors"]).Any())
                ?? comps.FirstOrDefault(c => StateOf(c) == "pre")
                ?? comps.FirstOrDefault();
            var sides = Arr(comp2?["competitors"]).ToList();
            var home = sides.FirstOrDefault(c => Str(c?["homeAway"]) == "home");
            var away = sides.FirstOrDefault(c => Str(c?["homeAway"]) == "away");
            var f2 = Base(ev, comp2, league);
            if (f2 is null)
            {
                continue;
            }

            if (home is not null && away is not null)
            {
                f2.Home = TeamSide(home, f2.State);
                f2.Away = TeamSide(away, f2.State);
                fixtures.Add(f2);
                continue;
            }

            // A field: golf, racing.
            f2.Kind = "field";
            var shortDetail = Str(ev["status"]?["type"]?["shortDetail"]);
            if (comps.Count <= 1 && shortDetail.Length > 0)
            {
                f2.Status = shortDetail;
            }

            fixtures.Add(f2);
        }

        return fixtures;
    }

    private static string StateOf(JsonNode? comp) => Str(comp?["status"]?["type"]?["state"]);

    private static Fixture? Base(JsonNode ev, JsonNode? comp, SportsLeague league)
    {
        var dateText = Str(comp?["date"]);
        if (dateText.Length == 0)
        {
            dateText = Str(ev["date"]);
        }

        if (!DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start))
        {
            return null;
        }

        var type = comp?["status"]?["type"] ?? ev["status"]?["type"];
        var rawState = Str(type?["state"]);
        var state = rawState is "in" or "post" ? rawState : "pre";
        var status = Str(type?["shortDetail"]);
        if (status.Length == 0)
        {
            status = Str(type?["detail"]);
        }

        if (status.Length == 0 && state == "in")
        {
            status = "In progress";
        }

        var evId = Str(ev["id"]);
        var compId = Str(comp?["id"]);
        return new Fixture
        {
            Id = league.Id + ":" + evId + (compId.Length > 0 && compId != evId ? ":" + compId : string.Empty),
            Kind = "match",
            League = league.Id,
            LeagueLabel = league.Label,
            Name = Str(ev["name"]).Length > 0 ? Str(ev["name"]) : Str(ev["shortName"]),
            StartMs = start.ToUnixTimeMilliseconds(),
            State = state,
            Status = status,
            Broadcasts = Networks(comp),
        };
    }

    private static List<string> Networks(JsonNode? comp)
    {
        var found = new List<string>();
        foreach (var b in Arr(comp?["broadcasts"]))
        {
            foreach (var n in Arr(b?["names"]))
            {
                var s = Str(n);
                if (s.Length > 0 && !found.Contains(s))
                {
                    found.Add(s);
                }
            }
        }

        foreach (var g in Arr(comp?["geoBroadcasts"]))
        {
            var s = Str(g?["media"]?["shortName"]);
            if (s.Length > 0 && !found.Contains(s))
            {
                found.Add(s);
            }
        }

        return found;
    }

    private static FixtureSide AthleteSide(JsonNode? c)
    {
        var a = c?["athlete"];
        var name = Str(a?["displayName"]);
        if (name.Length == 0)
        {
            name = Str(a?["fullName"]);
        }

        var shortName = Str(a?["shortName"]);
        if (shortName.Length == 0)
        {
            shortName = name;
        }

        var last = shortName.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        var abbr = last.Length > 3 ? last[..3].ToUpperInvariant() : last.ToUpperInvariant();
        return new FixtureSide(name, shortName, abbr, null, null);
    }

    private static FixtureSide TeamSide(JsonNode c, string state)
    {
        var team = c["team"];
        var name = Str(team?["displayName"]);
        if (name.Length == 0)
        {
            name = Str(team?["name"]);
        }

        var shortName = Str(team?["shortDisplayName"]);
        if (shortName.Length == 0)
        {
            shortName = Str(team?["name"]);
        }

        if (shortName.Length == 0)
        {
            shortName = name;
        }

        var logo = Str(team?["logo"]);
        if (logo.Length == 0)
        {
            logo = Str(team?["logos"]?[0]?["href"]);
        }

        int? score = null;
        var scoreText = Str(c["score"]);
        if (state != "pre" && scoreText.Length > 0 && int.TryParse(scoreText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
        {
            score = s;
        }

        return new FixtureSide(name, shortName, Str(team?["abbreviation"]), logo.Length > 0 ? logo : null, score);
    }

    private static IEnumerable<JsonNode?> Arr(JsonNode? node) => node is JsonArray a ? a : Enumerable.Empty<JsonNode?>();

    private static string Str(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s))
            {
                return s ?? string.Empty;
            }

            return v.ToString();
        }

        return string.Empty;
    }
}

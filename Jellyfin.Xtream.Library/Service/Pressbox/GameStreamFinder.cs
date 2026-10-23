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
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// A channel of the lineup, as the game finder sees it.
/// </summary>
/// <param name="Key">Unique channel key.</param>
/// <param name="Name">The channel name.</param>
/// <param name="Group">The channel's category.</param>
/// <param name="TvgId">The guide id the channel reads, or empty.</param>
public sealed record LineupChannel(string Key, string Name, string Group, string TvgId);

/// <summary>
/// One programme listing near a game.
/// </summary>
/// <param name="TvgId">Guide id of the channel the programme is on.</param>
/// <param name="Title">Programme title.</param>
/// <param name="StartMs">Start, Unix milliseconds.</param>
/// <param name="StopMs">Stop, Unix milliseconds.</param>
public sealed record GuideListing(string TvgId, string Title, long StartMs, long StopMs);

/// <summary>
/// A channel found showing a game.
/// </summary>
/// <param name="Channel">The channel.</param>
/// <param name="Why">Why it matched.</param>
/// <param name="Score">0 to 1: how sure the match is.</param>
public sealed record GameChannelMatch(LineupChannel Channel, string Why, double Score);

/// <summary>
/// Finds which channels are showing a game (a port of PressBox <c>streams.ts</c>). Providers carry games
/// three ways, and this checks all of them across the whole lineup:
/// per-game channels with the game written into the name ("NHL | 01 - 7pm Bruins at Flyers"; both teams must
/// appear, and a date in the name far from the game means a leftover feed), channels whose guide lists both
/// teams at game time, and the networks ESPN says are showing it (FS1, USA Network, a regional).
/// English-language feeds and the viewer's chosen categories come first.
/// </summary>
public sealed partial class GameStreamFinder
{
    private static readonly HashSet<string> NetworkTail = new(StringComparer.Ordinal)
    {
        "hd", "fhd", "uhd", "sd", "4k", "8k", "raw", "east", "west", "us", "usa", "backup", "60fps", "1080p", "720p", "feed", "tv",
    };

    // Networks named differently in lineups than on ESPN.
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["fs1"] = ["fs1", "fox sports 1"],
        ["fs2"] = ["fs2", "fox sports 2"],
        ["prime video"] = ["prime video", "amazon prime"],
        ["nfl net"] = ["nfl network"],
        ["nhl net"] = ["nhl network"],
        ["mlb net"] = ["mlb network"],
        ["usa net"] = ["usa network"],
        ["golf chnl"] = ["golf channel"],
        ["tennis chnl"] = ["tennis channel"],
        ["cbs sports network"] = ["cbs sports network", "cbssn"],
        ["big ten network"] = ["big ten network", "btn"],
        ["sec network"] = ["sec network"],
        ["acc network"] = ["acc network", "accn"],
        ["tru"] = ["trutv"],
    };

    private readonly IReadOnlyList<LineupChannel> _channels;
    private readonly string[] _folded;
    private readonly string[][] _bare;
    private readonly Dictionary<string, List<LineupChannel>> _byTvg;

    /// <summary>
    /// Initializes a new instance of the <see cref="GameStreamFinder"/> class, folding the lineup once.
    /// </summary>
    /// <param name="channels">The whole lineup.</param>
    public GameStreamFinder(IReadOnlyList<LineupChannel> channels)
    {
        _channels = channels;
        _folded = channels.Select(c => Fold(c.Name)).ToArray();
        _bare = channels.Select(c => BareName(c.Name)).ToArray();
        _byTvg = new Dictionary<string, List<LineupChannel>>(StringComparer.Ordinal);
        foreach (var c in channels)
        {
            if (c.TvgId.Length == 0)
            {
                continue;
            }

            if (!_byTvg.TryGetValue(c.TvgId, out var list))
            {
                list = [];
                _byTvg[c.TvgId] = list;
            }

            list.Add(c);
        }
    }

    /// <summary>
    /// Folds text for comparison: lower case, accents removed, "&amp;" as "and", runs of other characters as one space,
    /// padded with a space either side.
    /// </summary>
    /// <param name="s">The text.</param>
    /// <returns>The folded text.</returns>
    public static string Fold(string s)
    {
        var decomposed = (s ?? string.Empty).ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (ch >= '̀' && ch <= 'ͯ')
            {
                continue;
            }

            sb.Append(ch);
        }

        var t = sb.ToString().Replace("&", " and ", StringComparison.Ordinal);
        t = NotFolded().Replace(t, " ").Trim();
        return " " + t + " ";
    }

    /// <summary>
    /// Finds the channels showing a fixture, best first.
    /// </summary>
    /// <param name="fixture">The game.</param>
    /// <param name="listings">Guide listings around the game's day.</param>
    /// <param name="chosenCategories">The viewer's chosen categories, which rank slightly higher.</param>
    /// <returns>The streams found, best first.</returns>
    public IReadOnlyList<GameChannelMatch> StreamsFor(Fixture fixture, IReadOnlyList<GuideListing> listings, IReadOnlyList<string>? chosenCategories = null)
    {
        var found = new Dictionary<string, GameChannelMatch>(StringComparer.Ordinal);
        var chosenFolded = (chosenCategories ?? Array.Empty<string>()).Select(Fold).ToList();

        void Add(LineupChannel c, string why, double score)
        {
            var s = score;

            // Another language's feed ranks below an English network showing it.
            if (Foreign().IsMatch(c.Name) || ForeignGroup().IsMatch(c.Group))
            {
                s -= 0.4;
            }
            else if (English().IsMatch(c.Group) || English().IsMatch(c.Name))
            {
                s += 0.04;
            }

            var group = Fold(c.Group);
            if (chosenFolded.Any(cat => group.Contains(cat, StringComparison.Ordinal)))
            {
                s += 0.03;
            }

            if (FourK().IsMatch(c.Name + " " + c.Group))
            {
                s += 0.01;
            }

            if (!found.TryGetValue(c.Key, out var prev) || prev.Score < s)
            {
                found[c.Key] = new GameChannelMatch(c, why, Math.Min(1, s));
            }
        }

        var home = fixture.Home;
        var away = fixture.Away;
        var homeTerms = home is null ? new List<string>() : TermsFor(home);
        var awayTerms = away is null ? new List<string>() : TermsFor(away);

        // 1. Per-game channels named for the game.
        if (home is not null && away is not null)
        {
            for (var i = 0; i < _channels.Count; i++)
            {
                var n = _folded[i];
                if (!Mentions(n, awayTerms) || !Mentions(n, homeTerms))
                {
                    continue;
                }

                if (About().IsMatch(_channels[i].Name))
                {
                    continue;
                }

                var when = DateInName(_channels[i].Name);
                if (when is not null && Math.Abs(when.Value - fixture.StartMs) > 18 * 3_600_000L)
                {
                    continue;
                }

                Add(_channels[i], "Game channel", 0.95);
            }
        }
        else
        {
            // A tournament, race or card: the event's distinctive words, plus the league's, both in the name.
            var words = Fold(fixture.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4 && !EventStopWords().IsMatch(w))
                .ToList();
            var leagueWord = Fold(fixture.LeagueLabel).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (words.Count > 0)
            {
                for (var i = 0; i < _channels.Count; i++)
                {
                    var n = _folded[i];
                    if (!n.Contains(" " + leagueWord, StringComparison.Ordinal) && !words.Any(w => n.Contains(" " + w + " ", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var hits = words.Count(w => n.Contains(" " + w + " ", StringComparison.Ordinal));
                    if (hits >= Math.Min(2, words.Count))
                    {
                        Add(_channels[i], "Event channel", 0.85);
                    }
                }
            }
        }

        // 2. The guide lists the game at that time.
        var from = fixture.StartMs - (30 * 60_000L);
        var to = fixture.StartMs + (3 * 3_600_000L);
        if (home is not null && away is not null)
        {
            foreach (var r in listings)
            {
                if (r.StopMs <= from || r.StartMs >= to || About().IsMatch(r.Title))
                {
                    continue;
                }

                var folded = Fold(r.Title);
                if (!Mentions(folded, awayTerms) || !Mentions(folded, homeTerms))
                {
                    continue;
                }

                if (_byTvg.TryGetValue(r.TvgId, out var onGuide))
                {
                    foreach (var c in onGuide)
                    {
                        Add(c, "Guide: " + r.Title, 0.9);
                    }
                }
            }
        }

        // 3. The networks showing it: channels that are the network ("US| ESPN HD"), not ones that merely mention it.
        foreach (var network in fixture.Broadcasts)
        {
            if (StreamOnly().IsMatch(network.Trim()))
            {
                continue;
            }

            var key = Fold(network).Trim();
            var names = Aliases.TryGetValue(key, out var al) ? al : [network];
            var aliases = names.Select(a => Fold(a).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
            var taken = 0;
            for (var i = 0; i < _channels.Count && taken < 6; i++)
            {
                var words = _bare[i];
                if (words.Length == 0 || !aliases.Any(a => a.Length > 0 && words[0] == a[0]))
                {
                    continue;
                }

                var hit = aliases.Any(a => a.Length > 0
                    && a.Length <= words.Length
                    && a.Select((w, k) => words[k] == w).All(x => x)
                    && words.Skip(a.Length).All(NetworkTail.Contains));
                if (!hit)
                {
                    continue;
                }

                Add(_channels[i], "On " + network, 0.7);
                taken++;
            }
        }

        return found.Values
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Channel.Name.Length)
            .ToList();
    }

    // Names a side goes by in a channel name or guide title: "Flyers", "Philadelphia Flyers".
    // Abbreviations are too ambiguous ("SEA", "NY") to count.
    private static List<string> TermsFor(FixtureSide side)
    {
        var terms = new List<string>();
        foreach (var t in new[] { side.Short, side.Name })
        {
            if (!string.IsNullOrWhiteSpace(t) && t.Trim().Length >= 3)
            {
                var f = Fold(t);
                if (!terms.Contains(f))
                {
                    terms.Add(f);
                }
            }
        }

        return terms;
    }

    private static bool Mentions(string text, List<string> terms) => terms.Any(t => text.Contains(t, StringComparison.Ordinal));

    // A channel's name without its country prefix and decorations, as words: "US| ESPN HD RAW" -> espn, hd.
    private static string[] BareName(string name)
    {
        return Fold(CountryPrefix().Replace(name ?? string.Empty, string.Empty, 1))
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    // A date/time written into a channel's name, if any (Unix milliseconds).
    private static long? DateInName(string name)
    {
        var iso = IsoDate().Match(name);
        if (iso.Success)
        {
            var dt = new DateTimeOffset(
                int.Parse(iso.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(iso.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(iso.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(iso.Groups[4].Value, CultureInfo.InvariantCulture),
                int.Parse(iso.Groups[5].Value, CultureInfo.InvariantCulture),
                0,
                TimeSpan.Zero);
            return dt.ToUnixTimeMilliseconds();
        }

        var dm = DayMonth().Match(name);
        if (dm.Success)
        {
            const string months = "janfebmaraprmayjunjulaugsepoctnovdec";
            var month = months.IndexOf(dm.Groups[2].Value.ToLowerInvariant()[..3], StringComparison.Ordinal) / 3;
            var day = int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture);
            var now = DateTimeOffset.UtcNow;
            try
            {
                return new DateTimeOffset(now.Year, month + 1, day, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    [GeneratedRegex(@"[^a-z0-9+]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotFolded();

    [GeneratedRegex(@"^[^|:]{1,12}[|:]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex CountryPrefix();

    [GeneratedRegex(@"(20\d\d)-(\d\d)-(\d\d)[ T](\d\d):(\d\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b(\d{1,2})\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayMonth();

    // Feeds in another language: a Spanish commentary tag, or a non-English country prefix on the feed.
    [GeneratedRegex(@"\((?:esp|es|spa|fr|pt|de|it)\)|\b(?:pt|es|se|fr|de|it|br|nl|pl|gr|no|dk|fi|ar|tr|exyu|ru)\s*:|\bespanol\b|\bdeportes\b|\s(?:se|no|dk|fi|de|nl|pl|es|pt|it|fr)\s+\d*\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Foreign();

    // Channel groups for another country or language ("IT| SPORT", "SWEDEN ...").
    [GeneratedRegex(@"^\s*\|?\s*(?:pt|es|se|fr|de|it|br|nl|pl|gr|no|dk|fi|ar|tr|exyu|ru|mx|latam|sweden|norway|denmark|finland|spain|france|germany|italy|portugal|brazil|poland|greece|turkey|arabic)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForeignGroup();

    [GeneratedRegex(@"(^|\W)(us|usa|uk|ca|en|eng)(\W|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex English();

    // Shows about a game rather than the game itself.
    [GeneratedRegex(@"^\s*end\s*\||\b(?:preview|previews|highlights|replay|recap|review|press conference|post ?game|pre ?game|analysis|breakdown|reaction|podcast|condensed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex About();

    [GeneratedRegex(@"\b(4k|uhd|8k)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FourK();

    [GeneratedRegex(@"^(the|grand|prix|championship|open|night|fight|cup|series|round|stroke|play)$", RegexOptions.CultureInvariant)]
    private static partial Regex EventStopWords();

    // Streaming-only outlets: no channel is called this, but per-game channels often carry the name.
    [GeneratedRegex(@"^(espn\+|mlb\.tv|nba league pass|nhl\.tv|nfl\+|peacock|paramount\+|apple tv|dazn|max|hbo max|youtube)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StreamOnly();
}

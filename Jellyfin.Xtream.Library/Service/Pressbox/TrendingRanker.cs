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
using System.Net;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// A search term that is trending (Google Trends).
/// </summary>
/// <param name="Folded">The folded title of the search.</param>
/// <param name="Words">Its significant words.</param>
/// <param name="Traffic">Approximate searches.</param>
/// <param name="Geo">The country it trends in.</param>
/// <param name="News">Headlines it is in the news for (folded): what a programme might be called.</param>
public sealed record TrendTerm(string Folded, IReadOnlyList<string> Words, int Traffic, string Geo, IReadOnlyList<string> News);

/// <summary>
/// A feed carrying a trending thing.
/// </summary>
/// <param name="Key">The channel key.</param>
/// <param name="Name">The channel name.</param>
public sealed record TrendingFeed(string Key, string Name);

/// <summary>
/// One thing that is on now and probably being watched.
/// </summary>
public sealed class TrendingItem
{
    /// <summary>Gets or sets the id, "game:..." or "programme:...".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind, "game" or "programme".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the league, or the network.</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Gets or sets ESPN's line for a game ("Q3 5:12").</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the start, Unix milliseconds.</summary>
    public long StartMs { get; set; }

    /// <summary>Gets or sets the stop, Unix milliseconds.</summary>
    public long? StopMs { get; set; }

    /// <summary>Gets or sets the score; bigger is hotter.</summary>
    public double Score { get; set; }

    /// <summary>Gets or sets why it is up there ("Live NFL", "Trending in the US").</summary>
    public List<string> Reasons { get; set; } = new();

    /// <summary>Gets or sets the channels carrying it, best first.</summary>
    public List<TrendingFeed> Feeds { get; set; } = new();
}

/// <summary>
/// What is on now, biggest first: an estimate of what people are watching, since no one publishes that live
/// (a port of PressBox <c>trending.ts</c>). Each thing on is scored from a live game's league and how close it is late
/// on, what the US and UK are searching for (Google Trends) matched to what is airing, the network it is on and how
/// many channels carry it, whether it is live, and what this household watched lately. The same thing on several
/// channels is one entry, with its feeds under it, best first.
/// </summary>
public static partial class TrendingRanker
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "vs", "v", "at", "of", "in", "on", "for", "a", "to", "live", "news", "today", "tonight", "new",
        "show", "game", "match", "day", "time", "world", "how", "what", "who", "is", "with",
    };

    // What a game is worth to an audience, roughly: an NFL game draws more than any other live thing on American television.
    private static readonly Dictionary<string, int> LeagueWeight = new(StringComparer.Ordinal)
    {
        ["nfl"] = 100, ["ncaaf"] = 75, ["nba"] = 80, ["wnba"] = 45, ["ncaam"] = 55, ["ncaaw"] = 35, ["mlb"] = 60, ["nhl"] = 60,
        ["ncaah"] = 25, ["epl"] = 70, ["ucl"] = 75, ["mls"] = 45, ["nwsl"] = 30, ["ligamx"] = 40, ["f1"] = 65, ["nascar"] = 50,
        ["indycar"] = 35, ["ufc"] = 70, ["pga"] = 45, ["lpga"] = 25, ["liv"] = 30, ["atp"] = 40, ["wta"] = 35, ["ufl"] = 35,
        ["cbase"] = 25, ["wvb"] = 25, ["pll"] = 20,
    };

    /// <summary>
    /// Parses a Google Trends RSS document into trend terms.
    /// </summary>
    /// <param name="xml">The RSS text.</param>
    /// <param name="geo">The country the feed is for.</param>
    /// <returns>The terms.</returns>
    public static IReadOnlyList<TrendTerm> ParseTrendsRss(string xml, string geo)
    {
        var output = new List<TrendTerm>();
        foreach (Match item in RssItem().Matches(xml))
        {
            var body = item.Groups[1].Value;
            var title = TagTexts(body, "title").FirstOrDefault();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            var trafficText = new string((TagTexts(body, "ht:approx_traffic").FirstOrDefault() ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
            var traffic = int.TryParse(trafficText, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 1000;
            output.Add(new TrendTerm(
                GameStreamFinder.Fold(title),
                Significant(title),
                traffic,
                geo,
                TagTexts(body, "ht:news_item_title").Select(GameStreamFinder.Fold).ToList()));
        }

        return output;
    }

    /// <summary>
    /// How strongly what is searched for points at this title: every significant word of a search in the title
    /// (or the title in a headline the search is in the news for).
    /// </summary>
    /// <param name="title">The title.</param>
    /// <param name="terms">The current trend terms.</param>
    /// <returns>The score (0 when nothing points at it) and the country the best match trends in.</returns>
    public static (double Score, string? Geo) TrendScore(string title, IReadOnlyList<TrendTerm> terms)
    {
        var t = GameStreamFinder.Fold(title);
        var words = Significant(title);
        var best = (Score: 0.0, Geo: (string?)null);
        foreach (var trend in terms)
        {
            var inTitle = trend.Words.Count > 0 && trend.Words.All(w => t.Contains(" " + w + " ", StringComparison.Ordinal));
            var inNews = words.Count >= 2 && trend.News.Any(n => words.All(w => n.Contains(" " + w + " ", StringComparison.Ordinal)));
            if (!inTitle && !inNews)
            {
                continue;
            }

            // 1,000 searches -> 15, 100,000 -> 25, a million -> 30.
            var score = 5 * Math.Log10(Math.Max(10, trend.Traffic)) * (inTitle ? 1 : 0.7);
            if (score > best.Score)
            {
                best = (score, trend.Geo);
            }
        }

        return best;
    }

    /// <summary>
    /// What a programme on a channel of this name is worth before anything else is known about it: the networks the
    /// most people have on, US and UK, count most.
    /// </summary>
    /// <param name="channelName">The channel name.</param>
    /// <returns>The weight.</returns>
    public static int NetworkWeight(string channelName)
    {
        var n = GameStreamFinder.Fold(channelName);
        if (BigFour().IsMatch(n))
        {
            return 30;
        }

        if (EspnMain().IsMatch(n) || BbcItv().IsMatch(n))
        {
            return 30;
        }

        if (SecondTier().IsMatch(n))
        {
            return 24;
        }

        if (NewsTier().IsMatch(n))
        {
            return 16;
        }

        return CableTier().IsMatch(n) ? 14 : 6;
    }

    /// <summary>
    /// Ranks what is on now.
    /// </summary>
    /// <param name="nowMs">The current time, Unix milliseconds.</param>
    /// <param name="lineup">The channels that count (the viewer's chosen categories).</param>
    /// <param name="fixtures">Today's games, from ESPN.</param>
    /// <param name="onNow">What the guide lists on now.</param>
    /// <param name="listingsAroundToday">Guide listings around today's games, for finding which channels show a game.</param>
    /// <param name="terms">The current trend terms.</param>
    /// <param name="recentKeys">Channels watched lately.</param>
    /// <param name="chosenCategories">The viewer's chosen categories.</param>
    /// <returns>The things on now, hottest first.</returns>
    public static IReadOnlyList<TrendingItem> Rank(
        long nowMs,
        IReadOnlyList<LineupChannel> lineup,
        IReadOnlyList<Fixture> fixtures,
        IReadOnlyList<GuideListing> onNow,
        IReadOnlyList<GuideListing> listingsAroundToday,
        IReadOnlyList<TrendTerm> terms,
        IReadOnlySet<string> recentKeys,
        IReadOnlyList<string> chosenCategories)
    {
        var items = new List<TrendingItem>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var allowed = new HashSet<string>(lineup.Select(c => c.Key), StringComparer.Ordinal);
        var finder = new GameStreamFinder(lineup);

        // Live games (and ones about to start), with the channels showing them.
        foreach (var f in fixtures)
        {
            var soon = f.State == "pre" && f.StartMs - nowMs < 15 * 60_000L && f.StartMs - nowMs > -60 * 60_000L;
            if (f.State != "in" && !soon)
            {
                continue;
            }

            var feeds = finder.StreamsFor(f, listingsAroundToday, chosenCategories)
                .Where(s => s.Score >= 0.6 && allowed.Contains(s.Channel.Key))
                .Select(s => s.Channel)
                .ToList();
            if (feeds.Count == 0)
            {
                continue;
            }

            var reasons = new List<string> { f.State == "in" ? "Live " + f.LeagueLabel : f.LeagueLabel + " starting soon" };
            var score = (LeagueWeight.TryGetValue(f.League, out var w) ? w : 30) * (f.State == "in" ? 1.0 : 0.7);

            // Close, late on: the end of a tight game is what everyone turns to.
            if (f.State == "in" && f.Home?.Score is int hs && f.Away?.Score is int aws && Math.Abs(hs - aws) <= 8 && LateInGame().IsMatch(f.Status))
            {
                score += 15;
                reasons.Add("Close finish");
            }

            var searched = new[]
            {
                TrendScore(f.Home?.Name ?? string.Empty, terms).Score,
                TrendScore(f.Away?.Name ?? string.Empty, terms).Score,
                TrendScore(f.Name, terms).Score,
            }.Max();
            if (searched > 0)
            {
                score += searched;
                reasons.Add("Trending");
            }

            if (feeds.Any(c => recentKeys.Contains(c.Key)))
            {
                score += 8;
            }

            foreach (var c in feeds)
            {
                claimed.Add(c.Key);
            }

            items.Add(new TrendingItem
            {
                Id = "game:" + f.Id,
                Kind = "game",
                Title = f.Home is not null && f.Away is not null ? f.Away.Short + " at " + f.Home.Short : f.Name,
                Subtitle = f.LeagueLabel,
                Status = f.Status,
                StartMs = f.StartMs,
                Score = score,
                Reasons = reasons,
                Feeds = feeds.Select(c => new TrendingFeed(c.Key, c.Name)).ToList(),
            });
        }

        // Everything else on now, by programme: the same title on several channels is one thing.
        var byTvg = new Dictionary<string, List<LineupChannel>>(StringComparer.Ordinal);
        foreach (var c in lineup)
        {
            if (claimed.Contains(c.Key) || c.TvgId.Length == 0)
            {
                continue;
            }

            if (!byTvg.TryGetValue(c.TvgId, out var list))
            {
                list = [];
                byTvg[c.TvgId] = list;
            }

            list.Add(c);
        }

        var groups = new Dictionary<string, (string Title, long Start, long Stop, List<LineupChannel> Feeds)>(StringComparer.Ordinal);
        foreach (var p in onNow)
        {
            if (!byTvg.TryGetValue(p.TvgId, out var on) || Filler().IsMatch(p.Title))
            {
                continue;
            }

            var key = GameStreamFinder.Fold(p.Title).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (!groups.TryGetValue(key, out var g))
            {
                g = (p.Title, p.StartMs, p.StopMs, []);
                groups[key] = g;
            }

            foreach (var c in on)
            {
                if (!g.Feeds.Contains(c))
                {
                    g.Feeds.Add(c);
                }
            }
        }

        foreach (var (key, g) in groups)
        {
            var feeds = g.Feeds.OrderByDescending(c => FeedRank(c, chosenCategories)).ToList();
            var network = feeds.Max(c => NetworkWeight(c.Name));

            // Distinct networks carrying it, not copies of the same one.
            var spread = feeds.Select(c => SpreadNoise().Replace(GameStreamFinder.Fold(c.Name), string.Empty).Trim()).Distinct(StringComparer.Ordinal).Count();
            var reasons = new List<string>();
            double score = network + Math.Min(15, 3 * (spread - 1));
            if (LiveTitle().IsMatch(g.Title))
            {
                score += 8;
            }

            var searched = TrendScore(g.Title, terms);
            if (searched.Score > 0)
            {
                score += searched.Score;
                reasons.Add(searched.Geo == "GB" ? "Trending in the UK" : "Trending in the US");
            }

            if (feeds.Any(c => recentKeys.Contains(c.Key)))
            {
                score += 8;
                reasons.Add("You watch this");
            }

            items.Add(new TrendingItem
            {
                Id = "programme:" + key,
                Kind = "programme",
                Title = g.Title,
                Subtitle = feeds[0].Name,
                StartMs = g.Start,
                StopMs = g.Stop,
                Score = score,
                Reasons = reasons,
                Feeds = feeds.Take(12).Select(c => new TrendingFeed(c.Key, c.Name)).ToList(),
            });
        }

        return items.OrderByDescending(i => i.Score).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Feeds best first: the user's categories, English, a better picture.
    private static int FeedRank(LineupChannel c, IReadOnlyList<string> chosen)
    {
        var text = c.Name + " " + c.Group;
        var s = 0;
        var group = GameStreamFinder.Fold(c.Group);
        if (chosen.Any(cat => group.Contains(GameStreamFinder.Fold(cat), StringComparison.Ordinal)))
        {
            s += 4;
        }

        if (EnglishTag().IsMatch(text))
        {
            s += 2;
        }

        if (GoodPicture().IsMatch(text))
        {
            s += 1;
        }

        return s;
    }

    private static List<string> Significant(string s)
    {
        return GameStreamFinder.Fold(s).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !Stop.Contains(w))
            .ToList();
    }

    private static List<string> TagTexts(string item, string tag)
    {
        var pattern = "<" + Regex.Escape(tag) + @">(?:<!\[CDATA\[)?([\s\S]*?)(?:\]\]>)?</" + Regex.Escape(tag) + ">";
        return Regex.Matches(item, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value.Trim()))
            .ToList();
    }

    [GeneratedRegex(@"<item>([\s\S]*?)</item>", RegexOptions.CultureInvariant)]
    private static partial Regex RssItem();

    [GeneratedRegex(@"\b(nbc|cbs|abc|fox)\b(?! (news|business|sports|soul|life))", RegexOptions.CultureInvariant)]
    private static partial Regex BigFour();

    [GeneratedRegex(@"\bespn\b(?!\s*(\d|plus|\+|news|deportes|classic))|\bespn ?2\b", RegexOptions.CultureInvariant)]
    private static partial Regex EspnMain();

    [GeneratedRegex(@"\bbbc (one|1)\b|\bitv ?1?\b|\bsky sports (main event|premier league|f1|football)\b", RegexOptions.CultureInvariant)]
    private static partial Regex BbcItv();

    [GeneratedRegex(@"\b(tnt|tbs|fs1|nfl network|nfl redzone|redzone|nba tv|mlb network|nhl network|sky sports|tnt sports|dazn|channel 4|bbc two|bbc 2)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SecondTier();

    [GeneratedRegex(@"\b(fox news|cnn|msnbc|bbc news|sky news|cnbc)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NewsTier();

    [GeneratedRegex(@"\b(hbo|showtime|amc|fx|usa network|bravo|hgtv|discovery|food network|tlc|history|cartoon network|nickelodeon|disney)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CableTier();

    // Not worth anyone's evening.
    [GeneratedRegex(@"^\s*(to be announced|tba|tbd|paid programming|off air|sign off|no information|programming|close|station close|test card|infomercial)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Filler();

    [GeneratedRegex(@"(^|\W)(live|en vivo)(\W|$)|\b(vs?\.?|at)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiveTitle();

    [GeneratedRegex(@"\b(4th|q4|ot|9th|8th|3rd|2nd half|final)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LateInGame();

    [GeneratedRegex(@"\b(hd|fhd|uhd|4k|sd|raw|east|west|us|usa|uk|backup|\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SpreadNoise();

    [GeneratedRegex(@"(^|\W)(us|usa|uk|en|eng|ca)(\W|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnglishTag();

    [GeneratedRegex(@"\b(4k|uhd|fhd|1080)\b|ᵁᴰᴰ|⁴ᴷ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoodPicture();
}

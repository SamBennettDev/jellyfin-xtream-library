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
/// A provider channel as the guide matcher sees it.
/// </summary>
/// <param name="Key">Stable key of the channel (unique across providers).</param>
/// <param name="PlaylistId">Which provider the channel belongs to; channels are grouped per provider.</param>
/// <param name="TvgId">The provider's own guide id for the channel (may be empty).</param>
/// <param name="Name">The provider's channel name.</param>
public sealed record ProviderChannel(string Key, string PlaylistId, string TvgId, string Name);

/// <summary>
/// A channel of a default guide (XMLTV).
/// </summary>
/// <param name="Id">The XMLTV channel id.</param>
/// <param name="Names">Display names.</param>
/// <param name="Icon">Icon URL, or empty.</param>
/// <param name="SourceId">Which guide source it came from.</param>
public sealed record GuideChannel(string Id, IReadOnlyList<string> Names, string Icon, string SourceId);

/// <summary>
/// The rule that matched a channel to a guide channel.
/// </summary>
public enum GuideMatchRule
{
    /// <summary>The channel's tvg-id is the guide channel's id.</summary>
    Id,

    /// <summary>The channel names a local station's call sign.</summary>
    CallSign,

    /// <summary>The channel's cleaned name is the guide channel's cleaned name.</summary>
    Name,

    /// <summary>The viewer pinned the channel to a guide channel by hand; this always wins.</summary>
    Manual,
}

/// <summary>
/// One certain match of a provider channel to a guide channel.
/// </summary>
/// <param name="ChannelKey">The provider channel's key.</param>
/// <param name="GuideId">The guide channel's XMLTV id.</param>
/// <param name="SourceId">The guide source the guide channel came from.</param>
/// <param name="Rule">Which rule matched.</param>
/// <param name="Name">The guide's name to show instead of the provider's, when it is better.</param>
/// <param name="Logo">The guide's logo to show, when it has one.</param>
public sealed record GuideMatch(string ChannelKey, string GuideId, string SourceId, GuideMatchRule Rule, string? Name, string? Logo);

/// <summary>
/// The call signs a channel names.
/// </summary>
/// <param name="Signs">The call signs read from the channel.</param>
/// <param name="Sub">Whether the channel names a subchannel or low-power station (not the main programme).</param>
/// <param name="From">"name" or "feed" when signs were found, else null.</param>
public sealed record ChannelCallSigns(IReadOnlyList<string> Signs, bool Sub, string? From);

/// <summary>
/// A guide channel's station read from its id.
/// </summary>
/// <param name="Sign">The call sign.</param>
/// <param name="Rank">0 for -DT, 1 for -TV, 2 for the bare sign; null for subchannels, translators and the like.</param>
public sealed record GuideStationInfo(string Sign, int? Rank);

/// <summary>
/// Matches a provider's channels to a default guide (such as epgshare01) only where there is no doubt.
/// Never a fuzzy guess: a wrong guide on a channel is worse than none, since the viewer then cannot
/// tell what is on. Ported from PressBox <c>guide-match.ts</c>.
/// <list type="number">
/// <item>By id: the channel's tvg-id is a guide channel's id (trimmed, any case), and exactly one guide channel has that id.</item>
/// <item>By call sign (local stations): the station's call sign, read from a US channel's name or an iptv-org feed id, is exactly one main station in the guide.</item>
/// <item>By name: the channel's name, stripped of provider decorations, is exactly one guide channel's display name stripped the same way, and the provider's channels that strip to that name do not disagree about which channel they are.</item>
/// </list>
/// </summary>
public static partial class GuideMatcher
{
    private const string Call = "[KW][A-Z]{2,3}";
    private const string Suffixes = "DT|TV|LD|CD|CA|LP";
    private const string Networks = "ABC|NBC|CBS|FOX|CW|PBS|MNT|MY|MYTV|IND|ION|TELEMUNDO|UNIVISION|UNIMAS|ESTRELLA|AZTECA|METV|TBD";

    // Words in a channel name that look like call signs and aren't (after a network word: "FOX WEST", "CBS KIDS").
    private static readonly HashSet<string> NotCallSigns = new(StringComparer.Ordinal)
    {
        "WEST", "WWE", "KIDS", "WILD", "WOW", "WAVE", "WORK", "KISS", "WEB", "WIN", "WAR", "WAY", "WHY", "WHO",
        "KICK", "WINE", "WIRE", "WEEK", "WISE", "WITH", "WAYS", "KIND", "KNOW", "KEYS", "WIFE",
    };

    /// <summary>
    /// The comparable form of a channel or guide name, or null when there is nothing certain to compare
    /// (empty, a heading, or a provider name behind a prefix that isn't the US one).
    /// </summary>
    /// <param name="raw">The raw name.</param>
    /// <param name="providerSide">True for a provider's channel name, false for a guide's display name.</param>
    /// <returns>The key, or null.</returns>
    public static string? GuideNameKey(string raw, bool providerSide = true)
    {
        var t = DecorationChars().Replace(raw ?? string.Empty, " ").Normalize(NormalizationForm.FormKC).Trim();
        if (providerSide)
        {
            // "#### MOVIES ####", "===== SPORTS =====": a heading in the list, not a channel.
            if (Separator().IsMatch(t))
            {
                return null;
            }

            t = UsPrefix().Replace(t, string.Empty, 1);
            if (OtherPrefix().IsMatch(t))
            {
                return null;
            }
        }

        t = t.ToLowerInvariant();
        t = QualityParens().Replace(t, " ");
        t = t.Replace("&", " and ", StringComparison.Ordinal).Replace("+", " plus ", StringComparison.Ordinal);
        t = NonAlphanumeric().Replace(t, " ");

        // A guide's "Fox Sports 4K" is its own channel (the 4K simulcast), not the plain one;
        // a provider's "CNN 4K" is just its best feed of CNN.
        t = (providerSide ? QualityWords() : GuideQualityWords()).Replace(t, " ");
        t = MultiSpace().Replace(t, " ").Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>
    /// The provider says the channel is another country's ("Pop.uk", "FoxSports.ar@SD") and its name
    /// doesn't say US: the US guide's channel of the same name is someone else.
    /// </summary>
    /// <param name="tvgId">The channel's tvg-id.</param>
    /// <param name="name">The channel's name.</param>
    /// <returns>True when the channel is plainly not a US one.</returns>
    public static bool ForeignChannel(string tvgId, string name)
    {
        var m = CountrySuffix().Match((tvgId ?? string.Empty).Trim());
        if (!m.Success)
        {
            return false;
        }

        var cc = m.Groups[1].Value.ToLowerInvariant();
        if (cc == "us")
        {
            return false;
        }

        var cleaned = DecorationChars().Replace(name ?? string.Empty, " ").Normalize(NormalizationForm.FormKC);
        return !UsPrefix().IsMatch(cleaned);
    }

    /// <summary>
    /// A guide channel's station, from its id ("KDKA-DT.us_locals1").
    /// </summary>
    /// <param name="id">The guide channel id.</param>
    /// <returns>The call sign and rank, or null when the id isn't a US station.</returns>
    public static GuideStationInfo? GuideStation(string id)
    {
        var baseId = LastExtension().Replace((id ?? string.Empty).Trim(), string.Empty).ToUpperInvariant();
        var lead = StationLead().Match(baseId);
        if (!lead.Success)
        {
            return null;
        }

        var main = StationMain().Match(baseId);
        int? rank = null;
        if (main.Success)
        {
            rank = main.Groups[2].Value switch
            {
                "DT" => 0,
                "TV" => 1,
                _ => 2,
            };
        }

        return new GuideStationInfo(lead.Groups[1].Value, rank);
    }

    /// <summary>
    /// The call signs a provider channel names, as read (not yet checked against a guide).
    /// Only US channels: a "US:" name, or an iptv-org US feed id.
    /// </summary>
    /// <param name="tvgId">The channel's tvg-id.</param>
    /// <param name="name">The channel's name.</param>
    /// <returns>The call signs read.</returns>
    public static ChannelCallSigns ChannelCallSignsOf(string tvgId, string name)
    {
        var signs = new List<string>();
        var sub = false;
        string? from = null;
        var cleaned = DecorationChars().Replace(name ?? string.Empty, " ").Normalize(NormalizationForm.FormKC);
        if (UsPrefix().IsMatch(cleaned))
        {
            var rest = UsPrefix().Replace(cleaned, string.Empty, 1);
            foreach (var re in new[] { InParens(), AfterNetwork() })
            {
                foreach (Match m in re.Matches(rest))
                {
                    // "(WEST)" is a feed, not a station.
                    if (NotCallSigns.Contains(m.Groups[1].Value))
                    {
                        continue;
                    }

                    if (!signs.Contains(m.Groups[1].Value))
                    {
                        signs.Add(m.Groups[1].Value);
                    }

                    // "-DT2" is a subchannel; "-LD", "-CD" a low-power station of its own: neither is the main programme.
                    var digits = m.Groups[3].Value;
                    var suffix = m.Groups[2].Value;
                    if ((digits.Length > 0 && digits != "1") || (suffix.Length > 0 && suffix != "DT" && suffix != "TV"))
                    {
                        sub = true;
                    }
                }
            }

            if (signs.Count > 0)
            {
                from = "name";
            }
        }

        // iptv-org: "ABC.us@KGTV", "ABC.us@WDIODT", for the big networks' affiliates only.
        // Only when the name gave none: the name is what the viewer reads.
        if (signs.Count == 0)
        {
            var m = FeedId().Match((tvgId ?? string.Empty).Trim());
            if (m.Success && FeedNetworks().IsMatch(m.Groups[1].Value))
            {
                foreach (var s in FeedSigns(m.Groups[2].Value.ToUpperInvariant()))
                {
                    if (!signs.Contains(s))
                    {
                        signs.Add(s);
                    }
                }
            }

            if (signs.Count > 0)
            {
                from = "feed";
            }
        }

        return new ChannelCallSigns(signs, sub, from);
    }

    /// <summary>
    /// Which local stations' listings are worth keeping from a big local guide: the call signs the lineup
    /// names, and the guide entries picked by hand.
    /// </summary>
    /// <param name="channels">The lineup, as (tvgId, name) pairs.</param>
    /// <param name="pickedGuideIds">Guide ids chosen by hand.</param>
    /// <returns>The wanted call signs and guide ids.</returns>
    public static (HashSet<string> Signs, HashSet<string> Ids) WantedStations(
        IEnumerable<(string TvgId, string Name)> channels,
        IEnumerable<string> pickedGuideIds)
    {
        var signs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tvgId, name) in channels)
        {
            foreach (var s in ChannelCallSignsOf(tvgId, name).Signs)
            {
                signs.Add(s);
            }
        }

        return (signs, new HashSet<string>(pickedGuideIds, StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether a listing of a (large, local-stations) guide is worth keeping.
    /// </summary>
    /// <param name="guideChannelId">The guide channel id of the listing.</param>
    /// <param name="signs">Wanted call signs.</param>
    /// <param name="ids">Guide ids picked by hand.</param>
    /// <returns>True to keep.</returns>
    public static bool KeepLocalListing(string guideChannelId, HashSet<string> signs, HashSet<string> ids)
    {
        if (ids.Contains(guideChannelId))
        {
            return true;
        }

        var s = GuideStation(guideChannelId);
        return s is not null && signs.Contains(s.Sign);
    }

    /// <summary>
    /// Matches every channel to the guide, only where certain.
    /// </summary>
    /// <param name="channels">Provider channels.</param>
    /// <param name="guide">Guide channels.</param>
    /// <returns>The certain matches.</returns>
    public static IReadOnlyList<GuideMatch> ComputeGuideMatches(IReadOnlyList<ProviderChannel> channels, IReadOnlyList<GuideChannel> guide)
    {
        // Guide side: ids, and names, each with every guide channel they belong to.
        var byId = new Dictionary<string, List<GuideChannel>>(StringComparer.Ordinal);
        var byName = new Dictionary<string, HashSet<GuideChannel>>(StringComparer.Ordinal);
        foreach (var g in guide)
        {
            var k = IdKey(g.Id);
            if (k.Length == 0)
            {
                continue;
            }

            Push(byId, k, g);
            foreach (var n in g.Names)
            {
                var nk = GuideNameKey(n, providerSide: false);
                if (nk is null)
                {
                    continue;
                }

                if (!byName.TryGetValue(nk, out var set))
                {
                    set = [];
                    byName[nk] = set;
                }

                set.Add(g);
            }
        }

        // Provider side, per playlist: who shares a tvg-id, who shares a name.
        var keyOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        var nameGroups = new Dictionary<string, List<ProviderChannel>>(StringComparer.Ordinal);
        var idGroups = new Dictionary<string, List<ProviderChannel>>(StringComparer.Ordinal);
        foreach (var c in channels)
        {
            var nk = GuideNameKey(c.Name);
            keyOf[c.Key] = nk;
            if (nk is not null)
            {
                Push(nameGroups, c.PlaylistId + "\u0000" + nk, c);
            }

            var ik = IdKey(c.TvgId);
            if (ik.Length > 0)
            {
                Push(idGroups, c.PlaylistId + "\u0000" + ik, c);
            }
        }

        var output = new List<GuideMatch>();
        var matchedTo = new Dictionary<string, string>(StringComparer.Ordinal);

        // 1. By id.
        foreach (var c in channels)
        {
            var ik = IdKey(c.TvgId);
            if (ik.Length == 0 || !byId.TryGetValue(ik, out var hits) || hits.Count != 1)
            {
                continue;
            }

            var g = hits[0];
            matchedTo[c.Key] = g.Id;

            // The id is the provider's own word for which channel this is. Its name and logo too,
            // unless the provider used the id for channels that are plainly different.
            var sharing = idGroups.TryGetValue(c.PlaylistId + "\u0000" + ik, out var list) ? list : [c];
            var oneChannel = sharing.Select(s => keyOf.TryGetValue(s.Key, out var v) && v is not null ? v : "\u0000" + s.Key).Distinct(StringComparer.Ordinal).Count() == 1;
            var (name, logo) = Display(g);
            output.Add(new GuideMatch(c.Key, g.Id, g.SourceId, GuideMatchRule.Id, oneChannel ? name : null, oneChannel ? logo : null));
        }

        // 2. Local stations, by call sign.
        var stations = MainStations(guide);
        if (stations.Count > 0)
        {
            foreach (var c in channels)
            {
                if (matchedTo.ContainsKey(c.Key))
                {
                    continue;
                }

                var read = ChannelCallSignsOf(c.TvgId, c.Name);

                // Two stations named in one channel's name: which is it?
                if (read.Sub || read.Signs.Count == 0 || (read.From == "name" && read.Signs.Count > 1))
                {
                    continue;
                }

                var found = new HashSet<GuideChannel>();
                foreach (var s in read.Signs)
                {
                    if (stations.TryGetValue(s, out var st))
                    {
                        found.Add(st);
                    }
                }

                if (found.Count != 1)
                {
                    continue;
                }

                var g = found.First();
                matchedTo[c.Key] = g.Id;
                var (name, logo) = Display(g);
                output.Add(new GuideMatch(c.Key, g.Id, g.SourceId, GuideMatchRule.CallSign, name is not null && !BareCallSign().IsMatch(name.Trim()) ? name : null, logo));
            }
        }

        // 3. By name.
        foreach (var c in channels)
        {
            if (matchedTo.ContainsKey(c.Key))
            {
                continue;
            }

            if (!keyOf.TryGetValue(c.Key, out var nk) || nk is null || ForeignChannel(c.TvgId, c.Name))
            {
                continue;
            }

            if (!byName.TryGetValue(nk, out var hits) || hits.Count != 1)
            {
                continue;
            }

            var g = hits.First();
            var group = nameGroups.TryGetValue(c.PlaylistId + "\u0000" + nk, out var list) ? list : [c];
            if (!SameChannel(group))
            {
                continue;
            }

            // A sibling already matched by id to a different guide channel: the two rules disagree, so neither name match is certain.
            if (group.Any(s => matchedTo.TryGetValue(s.Key, out var other) && other != g.Id))
            {
                continue;
            }

            var (name, logo) = Display(g);
            output.Add(new GuideMatch(c.Key, g.Id, g.SourceId, GuideMatchRule.Name, name, logo));
        }

        return output;
    }

    private static (string? Name, string? Logo) Display(GuideChannel g)
    {
        var name = g.Names.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim();
        return (name, string.IsNullOrEmpty(g.Icon) ? null : g.Icon);
    }

    private static string IdKey(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

    // iptv-org marks a feed on the id ("ESPNU.us@HD"); the channel is the same.
    private static string ChannelIdKey(string s) => FeedMarker().Replace(IdKey(s), string.Empty);

    private static void Push<TValue>(Dictionary<string, List<TValue>> map, string key, TValue value)
    {
        if (map.TryGetValue(key, out var list))
        {
            list.Add(value);
        }
        else
        {
            map[key] = [value];
        }
    }

    // Channels in one playlist that strip to the same name are taken to be one channel (an HD and a 4K
    // feed, a backup) only if nothing says otherwise: they don't carry different tvg-ids, and two with
    // exactly the same name share a tvg-id (two plain "US: FOX 29" could be two cities).
    private static bool SameChannel(List<ProviderChannel> group)
    {
        if (group.Count == 1)
        {
            return true;
        }

        var ids = new HashSet<string>(group.Select(c => ChannelIdKey(c.TvgId)).Where(i => i.Length > 0), StringComparer.Ordinal);
        if (ids.Count > 1)
        {
            return false;
        }

        var names = group.Select(c => c.Name.Trim().ToLowerInvariant()).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            return ids.Count == 1 && group.All(c => ChannelIdKey(c.TvgId).Length > 0);
        }

        return true;
    }

    // The ways an iptv-org feed name can be a call sign and a suffix ("WGNTV": WGN + TV; "KGTV": KGTV).
    private static IEnumerable<string> FeedSigns(string feed)
    {
        foreach (var n in new[] { 3, 4 })
        {
            if (feed.Length < n)
            {
                continue;
            }

            var sign = feed[..n];
            if (!ExactCall().IsMatch(sign) || NotCallSigns.Contains(sign))
            {
                continue;
            }

            // Low-power suffixes (LD, CD...) aren't the main station either.
            var m = FeedRest().Match(feed[n..]);
            if (m.Success && (m.Groups[2].Value.Length == 0 || m.Groups[2].Value == "1"))
            {
                yield return sign;
            }
        }
    }

    // The main station for each call sign in the guide; a sign with two equally good stations is left out.
    private static Dictionary<string, GuideChannel> MainStations(IReadOnlyList<GuideChannel> guide)
    {
        var best = new Dictionary<string, (GuideChannel G, int Rank, bool Tied)>(StringComparer.Ordinal);
        foreach (var g in guide)
        {
            var s = GuideStation(g.Id);
            if (s is null || s.Rank is null)
            {
                continue;
            }

            var rank = s.Rank.Value;
            if (!best.TryGetValue(s.Sign, out var prev) || rank < prev.Rank)
            {
                best[s.Sign] = (g, rank, false);
            }
            else if (rank == prev.Rank)
            {
                best[s.Sign] = (prev.G, prev.Rank, true);
            }
        }

        return best.Where(kv => !kv.Value.Tied).ToDictionary(kv => kv.Key, kv => kv.Value.G, StringComparer.Ordinal);
    }

    // Superscript and modifier letters and marks: always decoration. Removed outright, before NFKC
    // turns them into "HD" glued to the word before it.
    [GeneratedRegex(@"[ʰ-˿ᴬ-ᶿ⁰-₟ⱼⱽ²³¹◉●★☆•]+", RegexOptions.CultureInvariant)]
    private static partial Regex DecorationChars();

    // A country prefix this guide is for: "US| ", "US: ", "USA - ".
    [GeneratedRegex(@"^\s*(?:US|USA)\s*(?:[|:]|-(?=\s))[\s|:]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsPrefix();

    // Any other leading "WORD:" / "WORD|": a sub-provider or another country.
    [GeneratedRegex(@"^[^\s:|]{1,12}\s?[:|]", RegexOptions.CultureInvariant)]
    private static partial Regex OtherPrefix();

    [GeneratedRegex(@"#{2,}|={2,}|\*{2,}|~{2,}|-{3,}|_{3,}|[►▶◄◀]", RegexOptions.CultureInvariant)]
    private static partial Regex Separator();

    [GeneratedRegex(@"\((?:\d{3,4}\s*[pi]|[48]k|uhd|fhd|hd|sd|hevc|raw)\)", RegexOptions.CultureInvariant)]
    private static partial Regex QualityParens();

    [GeneratedRegex(@"\b(?:uhd|fhd|hd|sd|[48]k|hevc|h\.?26[45]|raw|\d{2,3}\s*fps|\d{3,4}[pi])\b", RegexOptions.CultureInvariant)]
    private static partial Regex QualityWords();

    [GeneratedRegex(@"\b(?:fhd|hd|sd)\b", RegexOptions.CultureInvariant)]
    private static partial Regex GuideQualityWords();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"\.([a-z]{2})(?:@[^@.]*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountrySuffix();

    [GeneratedRegex(@"@[^@]*$", RegexOptions.CultureInvariant)]
    private static partial Regex FeedMarker();

    [GeneratedRegex(@"\.[^.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex LastExtension();

    [GeneratedRegex("^(" + Call + ")(?=$|[-_.])", RegexOptions.CultureInvariant)]
    private static partial Regex StationLead();

    [GeneratedRegex("^(" + Call + ")(?:-(DT|TV))?$", RegexOptions.CultureInvariant)]
    private static partial Regex StationMain();

    [GeneratedRegex(@"\((" + Call + ")(?:-(" + Suffixes + @")(\d*))?\)", RegexOptions.CultureInvariant)]
    private static partial Regex InParens();

    [GeneratedRegex(@"\b(?:" + Networks + @")\s+(?:\d{1,2}\s+)?(" + Call + ")(?:-(" + Suffixes + @")(\d*))?\b", RegexOptions.CultureInvariant)]
    private static partial Regex AfterNetwork();

    [GeneratedRegex(@"^([A-Za-z]+)\.us@([A-Za-z0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeedId();

    [GeneratedRegex(@"^(?:ABC|CBS|NBC|Fox|PBS|CW|TheCW|Telemundo|Univision|UniMas|MNT|MyNetworkTV|EstrellaTV)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeedNetworks();

    [GeneratedRegex("^" + Call + "$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactCall();

    [GeneratedRegex(@"^(DT|TV)?(\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex FeedRest();

    // A guide name that is only a call sign ("KDKA-DT") says less than the provider's "CBS 2 (KDKA) PITTSBURGH".
    [GeneratedRegex("^" + Call + @"(?:-[A-Z]{1,3}\d*)?_?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareCallSign();
}

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

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service.Pressbox;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service.Pressbox;

public class GuideMatcherTests
{
    private static ProviderChannel Ch(string key, string name, string tvg = "", string playlist = "p") => new(key, playlist, tvg, name);

    private static GuideChannel G(string id, string name, string icon = "", string source = "g") => new(id, new[] { name }, icon, source);

    private static GuideMatch? Of(IEnumerable<GuideMatch> matches, string key) => matches.FirstOrDefault(m => m.ChannelKey == key);

    [Theory]
    [InlineData("US| ESPN ᴴᴰ ⁶⁰ᶠᵖˢ", "espn")]
    [InlineData("US: CNN 4K", "cnn")]
    [InlineData("SYFYᴴᴰ", "syfy")]
    [InlineData("US: : NATIONAL GEOGRAPHIC HD", "national geographic")]
    [InlineData("ESPNU (720p)", "espnu")]
    public void GuideNameKey_StripsPrefixAndQualityTags(string name, string expected)
    {
        GuideMatcher.GuideNameKey(name).Should().Be(expected);
    }

    [Fact]
    public void GuideNameKey_TreatsPlusAndAmpersandAsWords()
    {
        GuideMatcher.GuideNameKey("AMC+", false).Should().Be(GuideMatcher.GuideNameKey("US: AMC PLUS"));
        GuideMatcher.GuideNameKey("US: AMC HD").Should().NotBe(GuideMatcher.GuideNameKey("AMC+", false));
        GuideMatcher.GuideNameKey("Law and Crime", false).Should().Be(GuideMatcher.GuideNameKey("US: LAW & CRIME"));
    }

    [Theory]
    [InlineData("UK: VH1")]
    [InlineData("VIP: CNN HD")]
    [InlineData("TV: ONE ᴿᴬᵂ")]
    [InlineData("#### MOVIES ᴴᴰ/ᴿᴬᵂ ####")]
    [InlineData("ᴴᴰ")]
    public void GuideNameKey_RefusesForeignAndHeaderChannels(string name)
    {
        GuideMatcher.GuideNameKey(name).Should().BeNull();
    }

    [Fact]
    public void GuideNameKey_KeepsFourKWhenAskedNotToStripIt()
    {
        GuideMatcher.GuideNameKey("Fox Sports 4K", false).Should().Be("fox sports 4k");
        GuideMatcher.GuideNameKey("CNN HD", false).Should().Be("cnn");
    }

    [Fact]
    public void ComputeGuideMatches_MatchesByIdThenNameAndRefusesAmbiguity()
    {
        var guide = new[]
        {
            G("CNN.HD.us2", "CNN HD", "cnn.png"),
            G("TBS.HD.us2", "TBS HD"),
            G("TBS.us2", "TBS"),
            G("dup.us2", "Dup A"),
            G("DUP.us2", "Dup B", string.Empty, "g2"),
        };
        var matches = GuideMatcher.ComputeGuideMatches(
            new[]
            {
                Ch("a", "US: CNN HD"),
                Ch("b", "Anything", " cnn.hd.US2 "),
                Ch("c", "US: TBS HD"),
                Ch("d", "Dup", "dup.us2"),
                Ch("e", "CNN", string.Empty, "other"),
            },
            guide);

        Of(matches, "a").Should().Be(new GuideMatch("a", "CNN.HD.us2", "g", GuideMatchRule.Name, "CNN HD", "cnn.png"));
        Of(matches, "b")!.Rule.Should().Be(GuideMatchRule.Id);
        Of(matches, "b")!.GuideId.Should().Be("CNN.HD.us2");
        Of(matches, "c").Should().BeNull("HD and SD are two guide channels with the same key");
        Of(matches, "d").Should().BeNull("two guide ids differ only in case");
        Of(matches, "e")!.GuideId.Should().Be("CNN.HD.us2");
    }

    [Fact]
    public void GuideStation_ReadsCallSignsOfFullPowerStationsOnly()
    {
        void Expect(string id, string sign, int? rank)
        {
            var s = GuideMatcher.GuideStation(id);
            s.Should().NotBeNull(id);
            s!.Sign.Should().Be(sign);
            s.Rank.Should().Be(rank);
        }

        Expect("KDKA-DT.us_locals1", "KDKA", 0);
        Expect("WGN-TV.us_locals1", "WGN", 1);
        Expect("KDKA-DT2.us_locals1", "KDKA", null);
        Expect("WCBS-DT_.us_locals1", "WCBS", null);
        GuideMatcher.GuideStation("K45DH-D.us_locals1").Should().BeNull();
        GuideMatcher.GuideStation("FOX.(WCTI-TV2).New.Bern,.NC.us_locals1").Should().BeNull();
        GuideMatcher.GuideStation("CNN.HD.us2").Should().BeNull();
    }

    [Fact]
    public void ChannelCallSignsOf_ReadsTheSignFromNameOrFeedId()
    {
        var a = GuideMatcher.ChannelCallSignsOf(string.Empty, "US: CBS 2 (KDKA) PITTSBURGH HD");
        a.Signs.Should().Equal("KDKA");
        a.Sub.Should().BeFalse();
        a.From.Should().Be("name");

        GuideMatcher.ChannelCallSignsOf(string.Empty, "US: NBC KOMU (A) ᴿᴬᵂ").Signs.Should().Equal("KOMU");
        GuideMatcher.ChannelCallSignsOf(string.Empty, "US: TELEMUNDO (KHRR-DT) MARANA ᴴᴰ").Signs.Should().Equal("KHRR");
        GuideMatcher.ChannelCallSignsOf(string.Empty, "US: MY 9 WWOR NEW YORK").Signs.Should().Equal("WWOR");
        GuideMatcher.ChannelCallSignsOf(string.Empty, "US: CBS 7 (WBBJ-DT3) JACKSON HD").Sub.Should().BeTrue();
        GuideMatcher.ChannelCallSignsOf(string.Empty, "US: TELEMUNDO (KUAN-LD) CHULA VISTA").Sub.Should().BeTrue();
        GuideMatcher.ChannelCallSignsOf(string.Empty, "US: NBC E! (WEST) ᴿᴬᵂ").Signs.Should().BeEmpty();
        GuideMatcher.ChannelCallSignsOf(string.Empty, "US: ABC 5 WEST DES MOINES IA (WOI)").Signs.Should().Equal("WOI");
        GuideMatcher.ChannelCallSignsOf(string.Empty, "UK: ITV (KDKA)").Signs.Should().BeEmpty();
        GuideMatcher.ChannelCallSignsOf(string.Empty, "CBS 2 (KDKA) PITTSBURGH").Signs.Should().BeEmpty();

        var feed = GuideMatcher.ChannelCallSignsOf("ABC.us@KMGHTV", "ABC KMGH (720p)");
        feed.Signs.Should().Equal("KMGH");
        feed.From.Should().Be("feed");
        GuideMatcher.ChannelCallSignsOf("CW.us@WGNTV", "WGN").Signs.Should().Equal("WGN");
        GuideMatcher.ChannelCallSignsOf("Roar.us@KMGHTV", "Roar KMGH").Signs.Should().BeEmpty();
        GuideMatcher.ChannelCallSignsOf("Movies.us@KPVMLD", "Movies! KPVM-LD").Signs.Should().BeEmpty();
        GuideMatcher.ChannelCallSignsOf("ABC.us@WDIODT2", "ABC WDIO 2").Signs.Should().BeEmpty();
    }

    [Fact]
    public void ComputeGuideMatches_MatchesLocalStationsByCallSignOnlyWhenCertain()
    {
        var locals = new[]
        {
            G("KDKA-DT.us_locals1", "KDKA-DT", "https://img.example/cbs.png", "locals"),
            G("KOMU-DT.us_locals1", "KOMU-DT", string.Empty, "locals"),
            G("WGN-TV.us_locals1", "WGN-TV", string.Empty, "locals"),
            G("KMGH-DT.us_locals1", "KMGH-DT", string.Empty, "locals"),
            G("WSBK-DT.us_locals1", "WSBK-DT", string.Empty, "locals"),
            G("WBZ-DT.us_locals1", "WBZ-DT", string.Empty, "locals"),
            G("WCBS-DT.us_locals1", "WCBS-DT", string.Empty, "locals"),
            G("WWOR-DT.us_locals1", "WWOR-DT", string.Empty, "locals"),
            G("KDKA-DT2.us_locals1", "KDKA-DT2", string.Empty, "locals"),
            G("KUAN-LD.us_locals1", "KUAN-LD", string.Empty, "locals"),
            G("WEST-DT.us_locals1", "WEST-DT", string.Empty, "locals"),
        };
        var matches = GuideMatcher.ComputeGuideMatches(
            new[]
            {
                Ch("kdka", "US: CBS 2 (KDKA) PITTSBURGH HD"),
                Ch("kdka-raw", "US: CBS 2 (KDKA) PITTSBURGH ᴿᴬᵂ"),
                Ch("komu", "US: NBC KOMU (A) ᴿᴬᵂ"),
                Ch("wgn", "WGN", "CW.us@WGNTV"),
                Ch("kmgh", "ABC KMGH", "ABC.us@KMGHTV"),
                Ch("wsbk", "US: CW 38 (WSBK) BOSTON", "WBZ.us"),
                Ch("two", "US: CBS (WCBS) / MY (WWOR)"),
                Ch("sub", "US: CBS (KDKA-DT2) PITTSBURGH"),
                Ch("lowpower", "US: TELEMUNDO (KUAN-LD) CHULA VISTA"),
                Ch("west", "US: NBC E! (WEST)"),
                Ch("missing", "US: ABC (KXYZ) NOWHERE"),
                Ch("uk", "UK: ITV (KDKA)"),
            },
            locals);

        Of(matches, "kdka").Should().Be(new GuideMatch("kdka", "KDKA-DT.us_locals1", "locals", GuideMatchRule.CallSign, null, "https://img.example/cbs.png"));
        Of(matches, "kdka-raw")!.GuideId.Should().Be("KDKA-DT.us_locals1");
        Of(matches, "komu")!.GuideId.Should().Be("KOMU-DT.us_locals1");
        Of(matches, "wgn")!.GuideId.Should().Be("WGN-TV.us_locals1");
        Of(matches, "kmgh")!.GuideId.Should().Be("KMGH-DT.us_locals1");
        Of(matches, "wsbk")!.GuideId.Should().Be("WSBK-DT.us_locals1");
        foreach (var key in new[] { "two", "sub", "lowpower", "west", "missing", "uk" })
        {
            Of(matches, key).Should().BeNull(key);
        }
    }

    [Fact]
    public void ComputeGuideMatches_RefusesACallSignThatTwoGuidesClaim()
    {
        var locals = new[]
        {
            G("KDKA-DT.us_locals1", "KDKA-DT", string.Empty, "locals"),
            G("KDKA-DT.us_locals2", "KDKA-DT", string.Empty, "other"),
        };
        GuideMatcher.ComputeGuideMatches(new[] { Ch("kdka", "US: CBS 2 (KDKA)") }, locals).Should().BeEmpty();
    }
}

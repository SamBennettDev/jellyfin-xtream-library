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
using FluentAssertions;
using Jellyfin.Xtream.Library.Service.Pressbox;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service.Pressbox;

public class StreamBudgetTests
{
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-23T12:00:00Z");

    private (StreamBudget Budget, List<ProviderLimit> Providers) Make()
    {
        var providers = new List<ProviderLimit>
        {
            new("0", "http://a|u", StreamLimitMode.Auto, null, 2, false),
            new("1", "http://a|u", StreamLimitMode.Limited, 1, null, false),
            new("2", "http://b|v", StreamLimitMode.Unlimited, null, 5, false),
            new("3", "http://c|w", StreamLimitMode.Auto, null, null, false),
        };
        return (new StreamBudget(() => providers, () => _now), providers);
    }

    [Fact]
    public void MaximumFor_UsesTheStrictestLimitOfProvidersSharingAnAccount()
    {
        var (budget, _) = Make();
        budget.MaximumFor("0").Should().Be(1);
        budget.MaximumFor("2").Should().BeNull("unlimited");
        budget.MaximumFor("3").Should().BeNull("nothing is known, so nothing is enforced");
    }

    [Fact]
    public void Reserve_CountsStreamsAcrossProvidersSharingAnAccountAndRefusesTheOneOverTheLimit()
    {
        var (budget, _) = Make();
        var lease = budget.Reserve(new[] { ("ch1", "0") });
        budget.ActiveFor("1").Should().Be(1);

        var refused = () => budget.Reserve(new[] { ("ch2", "1") });
        refused.Should().Throw<StreamLimitException>();

        budget.Reserve(new[] { ("ch2", "1") }, new[] { lease[0].Id });
        budget.ActiveFor("0").Should().Be(1, "the new stream replaced the old one");
    }

    [Fact]
    public void Leases_LapseWhenNotHeld()
    {
        var (budget, _) = Make();
        budget.Reserve(new[] { ("ch1", "0") });
        _now = _now.AddSeconds(91);
        budget.ActiveFor("0").Should().Be(0);
    }

    [Fact]
    public void Reserve_RefusesTheSameChannelTwiceInOneRequest()
    {
        var (budget, _) = Make();
        var refused = () => budget.Reserve(new[] { ("a", "3"), ("a", "3") });
        refused.Should().Throw<StreamLimitException>();
    }

    [Fact]
    public void Reserve_GrantsEveryStreamOfAnUnlimitedProviderAndLetsThemBeHeld()
    {
        var (budget, _) = Make();
        var three = budget.Reserve(new[] { ("x", "2"), ("y", "2"), ("z", "2") });
        three.Should().HaveCount(3);
        budget.Touch(three[0].Id).Should().BeTrue();
    }

    [Fact]
    public void Reserve_RefusesADisabledProvider()
    {
        var (budget, providers) = Make();
        providers[0] = providers[0] with { Disabled = true };
        var refused = () => budget.Reserve(new[] { ("q", "0") });
        refused.Should().Throw<StreamLimitException>();
    }
}

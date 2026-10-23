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

public class MultiviewPlannerTests
{
    private static readonly MultiviewTile[] Tiles =
    {
        new("http://p/1.ts", "A"),
        new("http://p/2.ts", "B"),
    };

    [Fact]
    public void Cells_PlacesAGridOfFourInQuarters()
    {
        var cells = MultiviewPlanner.Cells(4, MultiviewLayout.Grid);
        cells.Should().HaveCount(4);
        cells[3].X.Should().Be(960);
        cells[3].Y.Should().Be(540);
    }

    [Fact]
    public void Cells_CentresTheLastRowOfAThreeTileGrid()
    {
        var cells = MultiviewPlanner.Cells(3, MultiviewLayout.Grid);
        cells[2].X.Should().Be(480);
        cells[2].Y.Should().Be(540);
    }

    [Fact]
    public void Cells_FocusMakesTheFirstTileLarge()
    {
        var cells = MultiviewPlanner.Cells(3, MultiviewLayout.Focus);
        cells[0].W.Should().Be(1280);
        cells[0].H.Should().Be(720);
        cells[1].X.Should().Be(1280);
        cells[2].Y.Should().Be(360);
    }

    [Fact]
    public void BuildFfmpegArgs_TakesTheSoundFromTheChosenTile()
    {
        var grid = MultiviewPlanner.BuildFfmpegArgs(Tiles, MultiviewLayout.Grid, 1);
        grid.Should().Contain("[v]");
        grid.Should().Contain("1:a:0?");

        var focus = MultiviewPlanner.BuildFfmpegArgs(Tiles, MultiviewLayout.Focus, 1);
        focus[focus.ToList().IndexOf("-i") + 1].Should().Be("http://p/2.ts", "the focus layout leads with the audio tile");
        focus.Should().Contain("0:a:0?");
    }

    [Fact]
    public void BuildFfmpegArgs_RefusesAnAudioTileThatIsNotATile()
    {
        var act = () => MultiviewPlanner.BuildFfmpegArgs(Tiles, MultiviewLayout.Grid, 5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildFfmpegArgs_RefusesNoTilesOrMoreThanFour()
    {
        var none = () => MultiviewPlanner.BuildFfmpegArgs(Array.Empty<MultiviewTile>(), MultiviewLayout.Grid, 0);
        none.Should().Throw<ArgumentException>();
        var many = () => MultiviewPlanner.BuildFfmpegArgs(Enumerable.Range(0, 5).Select(i => new MultiviewTile("http://p/" + i, "T")).ToList(), MultiviewLayout.Grid, 0);
        many.Should().Throw<ArgumentException>();
    }
}

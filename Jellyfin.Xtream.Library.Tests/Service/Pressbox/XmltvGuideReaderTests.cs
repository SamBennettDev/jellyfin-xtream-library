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
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service.Pressbox;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service.Pressbox;

public class XmltvGuideReaderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T12:00:00Z");

    private static string Stamp(DateTimeOffset t) => t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

    private static string Guide() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><tv>" +
        "<channel id=\"CNN.HD.us2\"><icon src=\"https://img/cnn.png?w=1&amp;h=2\" /><display-name>CNN HD</display-name></channel>" +
        "<channel id=\"KDKA-DT.us_locals1\"><display-name>KDKA-DT</display-name></channel>" +
        "<channel id=\"KXYZ-DT.us_locals1\"><display-name>KXYZ-DT</display-name></channel>" +
        $"<programme start=\"{Stamp(Now)}\" stop=\"{Stamp(Now.AddHours(1))}\" channel=\"CNN.HD.us2\"><title lang=\"en\">News &amp; Views</title><desc>About</desc></programme>" +
        $"<programme start=\"{Stamp(Now)}\" stop=\"{Stamp(Now.AddHours(1))}\" channel=\"KXYZ-DT.us_locals1\"><title>Skipped</title></programme>" +
        $"<programme start=\"{Stamp(Now)}\" stop=\"{Stamp(Now.AddHours(1))}\" channel=\"KDKA-DT.us_locals1\"><title>KDKA news</title></programme></tv>";

    private static async Task<(IReadOnlyList<XmltvChannel> Channels, List<XmltvProgramme> Programmes)> ReadAsync(Stream stream)
    {
        var programmes = new List<XmltvProgramme>();
        var channels = await XmltvGuideReader.ReadAsync(stream, _ => id => id != "KXYZ-DT.us_locals1", programmes.Add, XmltvGuideReader.DefaultMaxBytes, default);
        return (channels, programmes);
    }

    [Fact]
    public async Task ReadAsync_ReturnsEveryChannelAndOnlyTheWantedProgrammes()
    {
        var (channels, programmes) = await ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(Guide())));

        channels.Should().HaveCount(3);
        channels[0].Icon.Should().Be("https://img/cnn.png?w=1&h=2");
        programmes.Should().HaveCount(2);
        programmes[0].InnerXml.Should().Be("<title lang=\"en\">News &amp; Views</title><desc>About</desc>");
        programmes[0].StartMs.Should().Be(Now.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task ReadAsync_ReadsAGzippedGuide()
    {
        var bytes = Encoding.UTF8.GetBytes(Guide());
        var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        packed.Position = 0;
        var (_, programmes) = await ReadAsync(packed);
        programmes.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadAsync_RefusesATruncatedGuide()
    {
        var xml = Guide();
        var act = async () => await ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml[..(xml.Length - 30)])));
        await act.Should().ThrowAsync<XmltvGuideException>();
    }

    [Fact]
    public async Task ReadAsync_RefusesAGuideOverTheSizeCap()
    {
        var act = async () => await XmltvGuideReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(Guide())), c => _ => true, _ => { }, 100, default);
        await act.Should().ThrowAsync<XmltvGuideException>();
    }

    [Theory]
    [InlineData("20260923190000 +0000")]
    [InlineData("20260923150000 -0400")]
    public void ParseXmltvTime_HonoursTheOffset(string text)
    {
        XmltvGuideReader.ParseXmltvTime(text).Should().Be(new DateTimeOffset(2026, 9, 23, 19, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ParseXmltvTime_ReturnsNullForGarbage()
    {
        XmltvGuideReader.ParseXmltvTime("garbage").Should().BeNull();
    }
}

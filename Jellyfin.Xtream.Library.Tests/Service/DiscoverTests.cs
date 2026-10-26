// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// Discover: placeholder addresses, release ordering and picking the right file inside a torrent.

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service.Discover;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class DiscoverTests
{
    private static ReleaseOption Release(int resolution, int seeders, double sizeGb, string title = "x", bool rejected = false) => new()
    {
        Id = title + resolution + seeders,
        Title = title,
        Resolution = resolution,
        Seeders = seeders,
        SizeBytes = (long)(sizeGb * 1024 * 1024 * 1024),
        MagnetUrl = "magnet:?xt=urn:btih:abc",
        Rejected = rejected,
    };

    [Fact]
    public void AMovieAddressRoundTrips()
    {
        var path = new TitleRef(true, 872585, 0, 0).ToPath();

        TitleRef.TryParse("http://127.0.0.1:8096" + path, out var parsed).Should().BeTrue();
        parsed.Should().Be(new TitleRef(true, 872585, 0, 0));
    }

    [Fact]
    public void AnEpisodeAddressRoundTrips()
    {
        var path = new TitleRef(false, 1399, 3, 12).ToPath();

        TitleRef.TryParse(path, out var parsed).Should().BeTrue();
        parsed.Should().Be(new TitleRef(false, 1399, 3, 12));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://example.com/movie.mkv")]
    [InlineData("/XtreamLibrary/Discover/auto/movie/abc")]
    [InlineData("/XtreamLibrary/Discover/auto/movie/0")]
    [InlineData("/XtreamLibrary/Discover/auto/episode/5/1")]
    public void ForeignOrBrokenAddressesAreNotPlaceholders(string? text)
    {
        TitleRef.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void ASeededFullHdReleaseBeatsAHugeRemuxAndADeadTorrent()
    {
        var settings = new DiscoverSettings();
        var releases = new[]
        {
            Release(2160, 12, 78, "remux"),
            Release(1080, 300, 6, "web"),
            Release(1080, 0, 5, "dead"),
        };

        var chosen = ReleaseLabeler.Select(releases, settings);

        chosen.Select(r => r.Title).Should().Equal("web", "remux");
    }

    [Fact]
    public void ReleasesOverTheSizeCapAreLeftOut()
    {
        var settings = new DiscoverSettings { MaxSizeGb = 10 };

        var chosen = ReleaseLabeler.Select(new[] { Release(1080, 50, 6, "small"), Release(1080, 500, 25, "big") }, settings);

        chosen.Select(r => r.Title).Should().Equal("small");
    }

    [Fact]
    public void TheLabelShowsQualitySizeAndSeeders()
    {
        var release = Release(1080, 311, 6.2);
        release.Quality = "WEBDL-1080p";
        release.Indexer = "EZTV";

        ReleaseLabeler.Label(release).Should().Be("WEBDL-1080p · 6.2 GB · 311 seeders · EZTV");
    }

    [Theory]
    [InlineData("Movie.2023.2160p.UHD.BluRay.x265", "2160p BluRay")]
    [InlineData("Movie.2023.1080p.WEB-DL.DDP5.1", "1080p WEB-DL")]
    [InlineData("Some.Movie.4K.REMUX", "2160p REMUX")]
    [InlineData("nothing useful", "unknown")]
    public void QualityCanBeGuessedFromATitle(string title, string expected)
    {
        ReleaseLabeler.InferQuality(title).Should().Be(expected);
    }

    [Fact]
    public void AMoviePicksItsLargestVideoAndSkipsSamples()
    {
        var files = new List<(int Index, string Path, long Size)>
        {
            (1, "Movie/Movie.sample.mkv", 50_000_000),
            (2, "Movie/Movie.mkv", 6_000_000_000),
            (3, "Movie/Movie.nfo", 1_000),
            (4, "Movie/Extras/behind.mkv", 900_000_000),
        };

        ReleaseLabeler.PickFile(files, 0, 0).Should().Be(2);
    }

    [Theory]
    [InlineData("Show.S02E05.1080p.mkv", 2, 5, true)]
    [InlineData("Show - 2x05 - Title.mkv", 2, 5, true)]
    [InlineData("Show.S02E15.mkv", 2, 5, false)]
    [InlineData("Show.S12E05.mkv", 2, 5, false)]
    [InlineData("Season 2/Show.s02e05.mkv", 2, 5, true)]
    public void EpisodeNamesAreMatchedWithoutConfusingNeighbours(string path, int season, int episode, bool expected)
    {
        ReleaseLabeler.MatchesEpisode(path, season, episode).Should().Be(expected);
    }

    [Fact]
    public void ASeasonPackYieldsTheRequestedEpisode()
    {
        var files = new List<(int Index, string Path, long Size)>
        {
            (1, "Show.S01/Show.S01E01.mkv", 1_000_000_000),
            (2, "Show.S01/Show.S01E02.mkv", 1_100_000_000),
            (3, "Show.S01/Show.S01E03.mkv", 1_200_000_000),
        };

        ReleaseLabeler.PickFile(files, 1, 2).Should().Be(2);
        ReleaseLabeler.PickFile(files, 1, 9).Should().Be(-1);
    }

    [Fact]
    public void FolderNamesCarryTheTmdbIdAndAreFilesystemSafe()
    {
        var title = new CatalogTitle(603, true, "The Matrix: Reloaded?", 2003, string.Empty, 50);

        DiscoverCatalogService.FolderName(title).Should().Be("The Matrix Reloaded (2003) [tmdbid-603]");
    }

    [Fact]
    public void SettingsAreClampedAndTrimmed()
    {
        var settings = new DiscoverSettings { MaxMovies = -5, MaxSeries = 999999, TorrServerUrl = " http://torrserver:8090/ ", CatalogPath = "/x/" };

        settings.Validate();

        settings.MaxMovies.Should().Be(0);
        settings.MaxSeries.Should().Be(5000);
        settings.TorrServerUrl.Should().Be("http://torrserver:8090");
        settings.CatalogPath.Should().Be("/x");
    }
}

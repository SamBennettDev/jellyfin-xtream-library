// Copyright (C) 2022  Kevin Jilissen

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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Xtream.Library.Service.Discover;

/// <summary>
/// The handle Jellyfin keeps while a Discover release is playing. The bytes never pass through it: the media source's
/// path is the TorrServer address, which Jellyfin (and its ffmpeg) reads directly and can seek in.
/// </summary>
internal sealed class DiscoverLiveStream : ILiveStream
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverLiveStream"/> class.
    /// </summary>
    /// <param name="mediaSource">The opened media source.</param>
    /// <param name="originalStreamId">The open token it was opened from.</param>
    public DiscoverLiveStream(MediaSourceInfo mediaSource, string originalStreamId)
    {
        MediaSource = mediaSource;
        OriginalStreamId = originalStreamId;
        UniqueId = Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string TunerHostId => "discover";

    /// <inheritdoc />
    public bool EnableStreamSharing => false;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; }

    /// <inheritdoc />
    public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Close() => Task.CompletedTask;

    /// <inheritdoc />
    public Stream GetStream() => Stream.Null;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing is held: TorrServer manages the torrent.
    }
}

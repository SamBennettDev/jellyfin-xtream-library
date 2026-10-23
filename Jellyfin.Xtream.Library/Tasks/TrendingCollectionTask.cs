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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service.Pressbox;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Xtream.Library.Tasks;

/// <summary>
/// Refreshes the "Trending Now" collection. Does nothing unless trending collections are switched on.
/// </summary>
public class TrendingCollectionTask : IScheduledTask
{
    private readonly TrendingCollectionService _collections;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrendingCollectionTask"/> class.
    /// </summary>
    /// <param name="collections">The collection service.</param>
    public TrendingCollectionTask(TrendingCollectionService collections)
    {
        _collections = collections;
    }

    /// <inheritdoc />
    public string Name => "Refresh Xtream trending collection";

    /// <inheritdoc />
    public string Key => "XtreamLibraryTrending";

    /// <inheritdoc />
    public string Description => "Keeps a Trending Now collection of the synced movies and series that people are searching for.";

    /// <inheritdoc />
    public string Category => "Xtream Library";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _collections.RefreshAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks,
            },
        };
    }
}

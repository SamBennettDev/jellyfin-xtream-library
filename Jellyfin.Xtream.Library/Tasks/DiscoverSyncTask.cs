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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service.Discover;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Xtream.Library.Tasks;

/// <summary>
/// Keeps the Discover libraries in step with TMDB, Radarr and Sonarr. Does nothing unless Discover is switched on.
/// </summary>
public class DiscoverSyncTask : IScheduledTask
{
    private readonly DiscoverCatalogService _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverSyncTask"/> class.
    /// </summary>
    /// <param name="catalog">The catalog service.</param>
    public DiscoverSyncTask(DiscoverCatalogService catalog)
    {
        _catalog = catalog;
    }

    /// <inheritdoc />
    public string Name => "Sync Discover catalog";

    /// <inheritdoc />
    public string Key => "XtreamLibraryDiscover";

    /// <inheritdoc />
    public string Description => "Lists popular English titles from TMDB as placeholders, leaving out what Radarr and Sonarr already hold.";

    /// <inheritdoc />
    public string Category => "Xtream Library";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _catalog.SyncAsync(progress, cancellationToken).ConfigureAwait(false);
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
                IntervalTicks = TimeSpan.FromHours(12).Ticks,
            },
        };
    }
}

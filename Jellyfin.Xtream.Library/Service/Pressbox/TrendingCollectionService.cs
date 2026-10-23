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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// Keeps a "Trending Now" collection in the Jellyfin library: the synced movies and series whose titles the search
/// trends are talking about, hottest first. It is the one place the plugin writes to Jellyfin's own collections, so it
/// is kept in this file alone; if a Jellyfin release changes those interfaces, this is all that needs to change.
/// </summary>
public sealed class TrendingCollectionService
{
    /// <summary>
    /// The collection's name.
    /// </summary>
    public const string CollectionName = "Trending Now";

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly TrendingService _trending;
    private readonly ILogger<TrendingCollectionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrendingCollectionService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="collectionManager">Jellyfin's collections.</param>
    /// <param name="trending">The trending service.</param>
    /// <param name="logger">The logger.</param>
    public TrendingCollectionService(ILibraryManager libraryManager, ICollectionManager collectionManager, TrendingService trending, ILogger<TrendingCollectionService> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _trending = trending;
        _logger = logger;
    }

    /// <summary>
    /// Brings the collection up to date.
    /// </summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many titles the collection now holds.</returns>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken)
    {
        var settings = Plugin.Instance.Configuration.Pressbox;
        if (!settings.EnableTrending || !settings.EnableTrendingCollections)
        {
            return 0;
        }

        var terms = await _trending.GetTermsAsync(cancellationToken).ConfigureAwait(false);
        if (terms.Count == 0)
        {
            _logger.LogInformation("No trend terms to build the collection from; leaving it as it is");
            return 0;
        }

        var titles = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
        });
        var wanted = titles
            .Select(i => (Item: i, Score: TrendingRanker.TrendScore(i.Name, terms).Score))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(settings.TrendingCollectionSize)
            .Select(x => x.Item.Id)
            .ToList();

        var existing = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Name = CollectionName,
            Recursive = true,
        }).FirstOrDefault();

        if (existing is null)
        {
            if (wanted.Count == 0)
            {
                return 0;
            }

            await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = CollectionName,
                ItemIdList = wanted.Select(g => g.ToString("N")).ToArray(),
            }).ConfigureAwait(false);
            _logger.LogInformation("Created the {Name} collection with {Count} titles", CollectionName, wanted.Count);
            return wanted.Count;
        }

        var current = existing.GetLinkedChildren().Select(c => c.Id).ToList();
        var stale = current.Except(wanted).ToList();
        var fresh = wanted.Except(current).ToList();
        if (stale.Count > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(existing.Id, stale).ConfigureAwait(false);
        }

        if (fresh.Count > 0)
        {
            await _collectionManager.AddToCollectionAsync(existing.Id, fresh).ConfigureAwait(false);
        }

        _logger.LogInformation("Updated the {Name} collection: {Added} added, {Removed} removed, {Count} in all", CollectionName, fresh.Count, stale.Count, wanted.Count);
        return wanted.Count;
    }
}

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
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Library.Service.Discover;

/// <summary>
/// A torrent TorrServer has loaded: its info hash and files.
/// </summary>
/// <param name="Hash">The info hash.</param>
/// <param name="Files">The files as (index, path, size).</param>
public sealed record LoadedTorrent(string Hash, IReadOnlyList<(int Index, string Path, long Size)> Files);

/// <summary>
/// Talks to TorrServer, which turns a torrent into an HTTP stream that supports range requests, so a player can
/// start in the middle and TorrServer fetches the pieces around that point first.
/// </summary>
public sealed class TorrServerClient
{
    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="TorrServerClient"/> class.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    public TorrServerClient(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    /// <summary>
    /// Checks that TorrServer answers.
    /// </summary>
    /// <param name="baseUrl">TorrServer's address.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Its version text, or null when it does not answer.</returns>
    public async Task<string?> EchoAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(baseUrl + "/echo", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim() : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads a torrent (a magnet or a torrent file address) and waits for its file list.
    /// </summary>
    /// <param name="baseUrl">TorrServer's address.</param>
    /// <param name="link">The magnet or torrent file address.</param>
    /// <param name="title">A name to show in TorrServer.</param>
    /// <param name="timeout">How long to wait for the file list.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The loaded torrent, or null when its files did not arrive in time.</returns>
    public async Task<LoadedTorrent?> AddAsync(string baseUrl, string link, string title, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var added = await PostAsync(baseUrl, new JsonObject { ["action"] = "add", ["link"] = link, ["title"] = title, ["save_to_db"] = false }, cancellationToken).ConfigureAwait(false);
        var hash = added?["hash"]?.GetValue<string>();
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        var deadline = DateTime.UtcNow + timeout;
        var status = added;
        while (true)
        {
            var files = ReadFiles(status);
            if (files.Count > 0)
            {
                return new LoadedTorrent(hash, files);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            status = await PostAsync(baseUrl, new JsonObject { ["action"] = "get", ["hash"] = hash }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the address that streams one file of a loaded torrent.
    /// </summary>
    /// <param name="baseUrl">TorrServer's address.</param>
    /// <param name="hash">The info hash.</param>
    /// <param name="fileIndex">The file index.</param>
    /// <param name="fileName">The file's name (used only so players see an extension).</param>
    /// <returns>The stream address.</returns>
    public static string StreamUrl(string baseUrl, string hash, int fileIndex, string fileName)
    {
        var name = Uri.EscapeDataString(System.IO.Path.GetFileName(fileName));
        return string.Create(CultureInfo.InvariantCulture, $"{baseUrl}/stream/{name}?link={hash}&index={fileIndex}&play");
    }

    /// <summary>
    /// Removes a torrent from TorrServer, freeing its cache.
    /// </summary>
    /// <param name="baseUrl">TorrServer's address.</param>
    /// <param name="hash">The info hash.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the call returns.</returns>
    public async Task RemoveAsync(string baseUrl, string hash, CancellationToken cancellationToken)
    {
        await PostAsync(baseUrl, new JsonObject { ["action"] = "rem", ["hash"] = hash }, cancellationToken).ConfigureAwait(false);
    }

    private static List<(int Index, string Path, long Size)> ReadFiles(JsonNode? status)
    {
        var files = new List<(int, string, long)>();
        if (status?["file_stats"]?.AsArray() is not { } stats)
        {
            return files;
        }

        foreach (var file in stats)
        {
            var id = file?["id"]?.GetValue<int>() ?? 0;
            var path = file?["path"]?.GetValue<string>() ?? string.Empty;
            var length = file?["length"]?.GetValue<long>() ?? 0;
            if (id > 0 && path.Length > 0)
            {
                files.Add((id, path, length));
            }
        }

        return files;
    }

    private async Task<JsonNode?> PostAsync(string baseUrl, JsonNode body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(baseUrl + "/torrents", content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }
}

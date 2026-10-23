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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// Runs a multiview: up to four of the lineup's channels mixed into one picture by ffmpeg, served as one MPEG-TS
/// stream. The mosaic is a live channel in its own right (see <see cref="XtreamTunerHost"/>), so every Jellyfin client
/// can watch it. Each tile holds a stream on its provider for as long as the mosaic runs, all or none, so a mosaic that
/// would push an account past its limit is refused rather than starting three tiles and failing the fourth.
/// </summary>
public sealed class MultiviewService
{
    private readonly StreamLimitService _limits;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<MultiviewService> _logger;
    private readonly ConcurrentDictionary<string, int> _running = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiviewService"/> class.
    /// </summary>
    /// <param name="limits">The stream limit service.</param>
    /// <param name="mediaEncoder">Jellyfin's media encoder, which knows where its ffmpeg is.</param>
    /// <param name="logger">The logger.</param>
    public MultiviewService(StreamLimitService limits, IMediaEncoder mediaEncoder, ILogger<MultiviewService> logger)
    {
        _limits = limits;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets the multiview with a token, when multiview is on.
    /// </summary>
    /// <param name="token">The multiview's secret token.</param>
    /// <returns>The multiview, or null.</returns>
    public static MultiviewDefinition? ByToken(string token)
    {
        var settings = Plugin.Instance.Configuration.Pressbox;
        return settings.EnableMultiview
            ? settings.Multiviews.FirstOrDefault(m => string.Equals(m.Token, token, StringComparison.Ordinal))
            : null;
    }

    /// <summary>
    /// Gets the tuner channel id a multiview is shown as.
    /// </summary>
    /// <param name="definition">The multiview.</param>
    /// <returns>The channel id.</returns>
    public static string ChannelIdOf(MultiviewDefinition definition) => XtreamTunerHost.MultiviewChannelPrefix + definition.Id;

    /// <summary>
    /// Gets whether a mosaic is being served.
    /// </summary>
    /// <param name="id">The multiview id.</param>
    /// <returns>True when running.</returns>
    public bool IsRunning(string id) => _running.ContainsKey(id);

    /// <summary>
    /// Serves a multiview until the viewer leaves.
    /// </summary>
    /// <param name="definition">The multiview.</param>
    /// <param name="resolve">Turns a tuner channel id into its stream address and provider index (null when it is gone).</param>
    /// <param name="output">Where the MPEG-TS goes.</param>
    /// <param name="cancellationToken">Cancelled when the viewer leaves.</param>
    /// <returns>A task that completes when the mosaic ends.</returns>
    /// <exception cref="InvalidOperationException">When the multiview is already being served or a channel in it is gone.</exception>
    /// <exception cref="StreamLimitException">When a provider has no free stream for a tile.</exception>
    public async Task StreamAsync(
        MultiviewDefinition definition,
        Func<string, CancellationToken, Task<(string Url, int ProviderIndex)?>> resolve,
        Stream output,
        CancellationToken cancellationToken)
    {
        // Jellyfin probes a stream before it plays it, and the probe's connection may still be closing when
        // the player's arrives, so a second request waits a few seconds for the first to let go.
        var waitedMs = 0;
        while (!_running.TryAdd(definition.Id, 1))
        {
            if (waitedMs >= 6000)
            {
                throw new InvalidOperationException("This multiview is already being watched.");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            waitedMs += 250;
        }

        IReadOnlyList<StreamLease> leases = Array.Empty<StreamLease>();
        try
        {
            var settings = Plugin.Instance.Configuration.Pressbox;
            var tiles = new List<MultiviewTile>();
            var keys = new List<(string Key, int ProviderIndex)>();
            foreach (var channelId in definition.ChannelIds.Take(MultiviewPlanner.MaxTiles))
            {
                var found = await resolve(channelId, cancellationToken).ConfigureAwait(false);
                if (found is null)
                {
                    throw new InvalidOperationException("A channel in this multiview is no longer in the lineup.");
                }

                tiles.Add(new MultiviewTile(found.Value.Url, channelId));
                keys.Add((channelId, found.Value.ProviderIndex));
            }

            if (tiles.Count == 0)
            {
                throw new InvalidOperationException("This multiview has no channels.");
            }

            leases = _limits.OpenMany(keys);
            var layout = Enum.TryParse<MultiviewLayout>(definition.Layout, true, out var l) ? l : MultiviewLayout.Grid;
            var args = MultiviewPlanner.BuildFfmpegArgs(tiles, layout, Math.Clamp(definition.AudioIndex, 0, tiles.Count - 1));
            var ffmpeg = string.IsNullOrWhiteSpace(settings.FfmpegPath) ? _mediaEncoder.EncoderPath : settings.FfmpegPath;
            if (string.IsNullOrWhiteSpace(ffmpeg))
            {
                ffmpeg = "ffmpeg";
            }

            var info = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }

            using var process = new Process { StartInfo = info };
            _logger.LogInformation("Starting multiview {Name} with {Count} tiles", definition.Name, tiles.Count);
            process.Start();
            using var killOnLeave = cancellationToken.Register(() => Kill(process));

            // Keep the tiles' leases alive, and drain ffmpeg's log so it never blocks on a full pipe.
            using var heartbeat = new CancellationTokenSource();
            var hold = Task.Run(
                async () =>
                {
                    while (!heartbeat.IsCancellationRequested)
                    {
                        foreach (var lease in leases)
                        {
                            _limits.Hold(lease);
                        }

                        await Task.Delay(TimeSpan.FromSeconds(30), heartbeat.Token).ConfigureAwait(false);
                    }
                },
                CancellationToken.None);
            var drain = Task.Run(
                async () =>
                {
                    string? line;
                    string? last = null;
                    while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
                    {
                        last = line;
                    }

                    if (last is not null)
                    {
                        _logger.LogDebug("ffmpeg (multiview {Name}) ended: {Line}", definition.Name, last);
                    }
                },
                CancellationToken.None);

            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The viewer left.
            }
            catch (IOException)
            {
                // The viewer's connection dropped.
            }
            finally
            {
                Kill(process);
                await heartbeat.CancelAsync().ConfigureAwait(false);
                try
                {
                    await hold.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: the heartbeat was stopped.
                }

                await drain.ConfigureAwait(false);
            }
        }
        finally
        {
            _limits.CloseMany(leases);
            _running.TryRemove(definition.Id, out _);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}

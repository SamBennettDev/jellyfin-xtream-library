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
using System.Linq;
using System.Text;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// One channel of a multiview.
/// </summary>
/// <param name="Url">The stream address ffmpeg reads.</param>
/// <param name="Label">A name for logs and the guide; never the address (it carries credentials).</param>
public sealed record MultiviewTile(string Url, string Label);

/// <summary>
/// How the tiles of a multiview are laid out.
/// </summary>
public enum MultiviewLayout
{
    /// <summary>A balanced grid: one, two side by side, three (two over one), or two by two.</summary>
    Grid,

    /// <summary>The audio tile large on the left, the others stacked small on the right.</summary>
    Focus,
}

/// <summary>
/// A cell of the multiview canvas.
/// </summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="W">Width.</param>
/// <param name="H">Height.</param>
public readonly record struct MultiviewCell(int X, int Y, int W, int H);

/// <summary>
/// Plans a multiview: one to four channels, from any providers, composed by ffmpeg into one picture with the
/// sound of a single chosen tile, so every client can play it as an ordinary channel. This is the Jellyfin
/// counterpart of PressBox's Multiview screen (a balanced grid or an audio-focused layout, one active audio
/// source). Each tile still uses one of its provider's connections, which <see cref="StreamBudget"/> accounts for.
/// </summary>
public static class MultiviewPlanner
{
    /// <summary>
    /// Gets the most tiles a multiview may have.
    /// </summary>
    public const int MaxTiles = 4;

    /// <summary>
    /// Works out where each tile goes on a canvas.
    /// </summary>
    /// <param name="count">How many tiles, one to four.</param>
    /// <param name="layout">The layout.</param>
    /// <param name="width">Canvas width.</param>
    /// <param name="height">Canvas height.</param>
    /// <returns>One cell per tile; for <see cref="MultiviewLayout.Focus"/> the first is the large one.</returns>
    public static IReadOnlyList<MultiviewCell> Cells(int count, MultiviewLayout layout, int width = 1920, int height = 1080)
    {
        if (count is < 1 or > MaxTiles)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Choose one to four channels.");
        }

        var halfW = width / 2;
        var halfH = height / 2;
        if (count == 1)
        {
            return [new MultiviewCell(0, 0, width, height)];
        }

        if (layout == MultiviewLayout.Focus)
        {
            var bigW = width * 2 / 3;
            var bigH = bigW * 9 / 16;
            var smallW = width - bigW;
            var smallH = smallW * 9 / 16;
            var cells = new List<MultiviewCell> { new(0, 0, bigW, bigH) };
            for (var i = 1; i < count; i++)
            {
                cells.Add(new MultiviewCell(bigW, (i - 1) * smallH, smallW, smallH));
            }

            return cells;
        }

        return count switch
        {
            2 => [new MultiviewCell(0, halfH / 2, halfW, halfH), new MultiviewCell(halfW, halfH / 2, halfW, halfH)],
            3 => [new MultiviewCell(0, 0, halfW, halfH), new MultiviewCell(halfW, 0, halfW, halfH), new MultiviewCell(halfW / 2, halfH, halfW, halfH)],
            _ => [new MultiviewCell(0, 0, halfW, halfH), new MultiviewCell(halfW, 0, halfW, halfH), new MultiviewCell(0, halfH, halfW, halfH), new MultiviewCell(halfW, halfH, halfW, halfH)],
        };
    }

    /// <summary>
    /// Builds the ffmpeg filter graph that composes the tiles onto a black canvas.
    /// </summary>
    /// <param name="cells">The cells, one per tile, in input order.</param>
    /// <param name="width">Canvas width.</param>
    /// <param name="height">Canvas height.</param>
    /// <param name="fps">Output frame rate.</param>
    /// <returns>The <c>-filter_complex</c> value; its output pad is <c>[v]</c>.</returns>
    public static string BuildFilterGraph(IReadOnlyList<MultiviewCell> cells, int width = 1920, int height = 1080, int fps = 30)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"color=c=black:s={width}x{height}:r={fps}[base];");
        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            sb.Append(CultureInfo.InvariantCulture, $"[{i}:v:0]scale={c.W}:{c.H}:force_original_aspect_ratio=decrease,pad={c.W}:{c.H}:(ow-iw)/2:(oh-ih)/2:black,setsar=1,fps={fps}[t{i}];");
        }

        var previous = "base";
        for (var i = 0; i < cells.Count; i++)
        {
            var last = i == cells.Count - 1;
            var output = last ? "v" : "o" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append(CultureInfo.InvariantCulture, $"[{previous}][t{i}]overlay=x={cells[i].X}:y={cells[i].Y}:eof_action=pass:shortest=0[{output}]");
            if (!last)
            {
                sb.Append(';');
            }

            previous = output;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the ffmpeg arguments (no shell involved) that read the tiles and write one MPEG-TS stream to stdout.
    /// </summary>
    /// <param name="tiles">One to four tiles.</param>
    /// <param name="layout">The layout.</param>
    /// <param name="audioTile">Which tile's sound is heard (an index into <paramref name="tiles"/>).</param>
    /// <param name="videoEncoderArgs">The video encoder arguments; defaults to x264, fast and low-latency.</param>
    /// <param name="width">Canvas width.</param>
    /// <param name="height">Canvas height.</param>
    /// <param name="fps">Frame rate.</param>
    /// <returns>The argument list.</returns>
    public static IReadOnlyList<string> BuildFfmpegArgs(
        IReadOnlyList<MultiviewTile> tiles,
        MultiviewLayout layout,
        int audioTile,
        IReadOnlyList<string>? videoEncoderArgs = null,
        int width = 1920,
        int height = 1080,
        int fps = 30)
    {
        if (tiles.Count is < 1 or > MaxTiles)
        {
            throw new ArgumentException("Choose one to four channels.", nameof(tiles));
        }

        if (audioTile < 0 || audioTile >= tiles.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(audioTile), "The audio tile must be one of the tiles.");
        }

        // The audio tile leads: in the focus layout it is the large one.
        var order = Enumerable.Range(0, tiles.Count).OrderBy(i => i == audioTile ? 0 : 1).ThenBy(i => i).ToList();
        var ordered = order.Select(i => tiles[i]).ToList();
        var cells = Cells(ordered.Count, layout, width, height);
        if (layout == MultiviewLayout.Grid)
        {
            // A grid keeps the viewer's order; only the sound follows the audio tile.
            ordered = tiles.ToList();
        }

        var audioInput = layout == MultiviewLayout.Grid ? audioTile : 0;
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin" };
        foreach (var t in ordered)
        {
            args.AddRange(["-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5", "-fflags", "+genpts", "-i", t.Url]);
        }

        args.AddRange(["-filter_complex", BuildFilterGraph(cells, width, height, fps)]);
        args.AddRange(["-map", "[v]", "-map", audioInput.ToString(CultureInfo.InvariantCulture) + ":a:0?"]);
        args.AddRange(videoEncoderArgs ?? ["-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p", "-g", (fps * 2).ToString(CultureInfo.InvariantCulture), "-b:v", "6M", "-maxrate", "7M", "-bufsize", "12M"]);
        args.AddRange(["-c:a", "aac", "-b:a", "128k", "-ac", "2", "-f", "mpegts", "pipe:1"]);
        return args;
    }
}

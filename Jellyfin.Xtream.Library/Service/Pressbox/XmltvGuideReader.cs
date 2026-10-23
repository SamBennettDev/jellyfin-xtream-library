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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Jellyfin.Xtream.Library.Service.Pressbox;

/// <summary>
/// A channel entry of an XMLTV guide.
/// </summary>
/// <param name="Id">The channel id.</param>
/// <param name="Names">Display names.</param>
/// <param name="Icon">Icon URL, or empty.</param>
public sealed record XmltvChannel(string Id, IReadOnlyList<string> Names, string Icon);

/// <summary>
/// One programme of an XMLTV guide, kept as the parts needed to write it out again for another channel id.
/// </summary>
/// <param name="GuideChannelId">The guide channel it is on.</param>
/// <param name="StartAttr">The raw <c>start</c> attribute.</param>
/// <param name="StopAttr">The raw <c>stop</c> attribute.</param>
/// <param name="StartMs">Start, Unix milliseconds.</param>
/// <param name="StopMs">Stop, Unix milliseconds.</param>
/// <param name="InnerXml">Everything inside the element (title, desc, category, icon...), already escaped.</param>
public sealed record XmltvProgramme(string GuideChannelId, string StartAttr, string StopAttr, long StartMs, long StopMs, string InnerXml);

/// <summary>
/// The result of reading a guide: where it stopped, and why.
/// </summary>
public sealed class XmltvGuideException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="XmltvGuideException"/> class.
    /// </summary>
    public XmltvGuideException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XmltvGuideException"/> class.
    /// </summary>
    /// <param name="message">What is wrong with the guide.</param>
    public XmltvGuideException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XmltvGuideException"/> class.
    /// </summary>
    /// <param name="message">What is wrong with the guide.</param>
    /// <param name="innerException">The cause.</param>
    public XmltvGuideException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads an XMLTV guide as a stream, however large (the local-stations guide is over half a gigabyte of XML),
/// optionally gzipped. Only the programmes a caller wants are materialised; the rest are skipped.
/// </summary>
public static class XmltvGuideReader
{
    /// <summary>
    /// Gets the most uncompressed bytes a guide may unpack to before it is given up on.
    /// </summary>
    public static long DefaultMaxBytes => 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Reads a guide.
    /// </summary>
    /// <param name="source">The guide, gzipped or not (detected by its first bytes).</param>
    /// <param name="onChannelsComplete">Called once, when the channel entries are all read (at the first programme, or the end): given them, it returns which guide channels' programmes to keep.</param>
    /// <param name="onProgramme">Called for each kept programme.</param>
    /// <param name="maxBytes">The most uncompressed bytes to read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The channels of the guide.</returns>
    /// <exception cref="XmltvGuideException">When the document is not a complete XMLTV guide, or is too big.</exception>
    public static async Task<IReadOnlyList<XmltvChannel>> ReadAsync(
        Stream source,
        Func<IReadOnlyList<XmltvChannel>, Func<string, bool>> onChannelsComplete,
        Action<XmltvProgramme> onProgramme,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffered = new BufferedStream(source, 64 * 1024);
        var head = new byte[2];
        var read = 0;
        while (read < 2)
        {
            var n = await buffered.ReadAsync(head.AsMemory(read, 2 - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        Stream text = new PrefixedStream(head.AsSpan(0, read).ToArray(), buffered);
        if (read == 2 && head[0] == 0x1f && head[1] == 0x8b)
        {
            text = new GZipStream(text, CompressionMode.Decompress);
        }

        text = new LimitStream(text, maxBytes);

        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CheckCharacters = false,
        };

        var channels = new List<XmltvChannel>();
        Func<string, bool>? keep = null;
        var sawRoot = false;
        var closed = false;
        try
        {
            using var reader = XmlReader.Create(text, settings);
            while (!reader.EOF)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element)
                {
                    await reader.ReadAsync().ConfigureAwait(false);
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "tv":
                        sawRoot = true;
                        await reader.ReadAsync().ConfigureAwait(false);
                        break;
                    case "channel":
                        {
                            var el = (XElement)await XNode.ReadFromAsync(reader, cancellationToken).ConfigureAwait(false);
                            var id = (string?)el.Attribute("id") ?? string.Empty;
                            if (id.Length > 0)
                            {
                                var names = el.Elements("display-name").Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList();
                                var icon = (string?)el.Element("icon")?.Attribute("src") ?? string.Empty;
                                channels.Add(new XmltvChannel(id, names, icon));
                            }

                            break;
                        }

                    case "programme":
                        {
                            keep ??= onChannelsComplete(channels);
                            var channelId = reader.GetAttribute("channel") ?? string.Empty;
                            if (!keep(channelId))
                            {
                                await reader.SkipAsync().ConfigureAwait(false);
                                break;
                            }

                            var el = (XElement)await XNode.ReadFromAsync(reader, cancellationToken).ConfigureAwait(false);
                            var startAttr = (string?)el.Attribute("start") ?? string.Empty;
                            var stopAttr = (string?)el.Attribute("stop") ?? string.Empty;
                            var start = ParseXmltvTime(startAttr);
                            var stop = ParseXmltvTime(stopAttr);
                            if (start is null)
                            {
                                break;
                            }

                            onProgramme(new XmltvProgramme(
                                channelId,
                                startAttr,
                                stopAttr,
                                start.Value,
                                stop ?? start.Value,
                                string.Concat(el.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)))));
                            break;
                        }

                    default:
                        await reader.SkipAsync().ConfigureAwait(false);
                        break;
                }
            }

            closed = true;
        }
        catch (XmlException ex)
        {
            throw new XmltvGuideException("Not a complete XMLTV guide: " + ex.Message, ex);
        }
        catch (InvalidDataException ex)
        {
            throw new XmltvGuideException("The guide could not be unpacked: " + ex.Message, ex);
        }
        catch (GuideTooBigException ex)
        {
            throw new XmltvGuideException("The guide is too big.", ex);
        }

        if (!sawRoot || !closed)
        {
            throw new XmltvGuideException("Not a complete XMLTV guide.");
        }

        keep ??= onChannelsComplete(channels);
        return channels;
    }

    /// <summary>
    /// Parses an XMLTV time ("20260923190000 +0000", the zone optional and taken as UTC when missing).
    /// </summary>
    /// <param name="value">The attribute text.</param>
    /// <returns>Unix milliseconds, or null when unreadable.</returns>
    public static long? ParseXmltvTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var s = value.Trim();
        var space = s.IndexOf(' ', StringComparison.Ordinal);
        var stamp = space >= 0 ? s[..space] : s;
        var zone = space >= 0 ? s[(space + 1)..].Trim() : "+0000";
        if (stamp.Length < 8 || !stamp.All(char.IsAsciiDigit))
        {
            return null;
        }

        stamp = stamp.PadRight(14, '0');
        if (!DateTime.TryParseExact(stamp[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var offset = TimeSpan.Zero;
        if (zone.Length >= 5 && (zone[0] == '+' || zone[0] == '-')
            && int.TryParse(zone.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hh)
            && int.TryParse(zone.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mm))
        {
            offset = new TimeSpan(hh, mm, 0);
            if (zone[0] == '-')
            {
                offset = -offset;
            }
        }

        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUnixTimeMilliseconds();
    }

    private sealed class GuideTooBigException : IOException
    {
        public GuideTooBigException()
        {
        }
    }

    private sealed class LimitStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _max;
        private long _total;

        public LimitStream(Stream inner, long max)
        {
            _inner = inner;
            _max = max;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            Count(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Count(n);
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Count(n);
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Count(int n)
        {
            _total += n;
            if (_total > _max)
            {
                throw new GuideTooBigException();
            }
        }
    }

    private sealed class PrefixedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _rest;
        private int _at;

        public PrefixedStream(byte[] prefix, Stream rest)
        {
            _prefix = prefix;
            _rest = rest;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at < _prefix.Length)
            {
                var n = Math.Min(count, _prefix.Length - _at);
                Array.Copy(_prefix, _at, buffer, offset, n);
                _at += n;
                return n;
            }

            return _rest.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_at < _prefix.Length)
            {
                var n = Math.Min(buffer.Length, _prefix.Length - _at);
                _prefix.AsMemory(_at, n).CopyTo(buffer);
                _at += n;
                return n;
            }

            return await _rest.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rest.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

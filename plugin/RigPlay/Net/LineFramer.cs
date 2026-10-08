// SPDX-License-Identifier: GPL-3.0-only
// LineFramer.cs: splits the control channel's byte stream into lines (spec §5.1): each message ends with \n, one \r
// before it is stripped, and a line longer than 65536 bytes (terminator excluded) is an error.
// Pure: no SimHub or WPF types (compiled into RigPlay.Tests).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RigPlayPlugin.Net
{
    public sealed class LineTooLongException : Exception
    {
        public LineTooLongException(int limit) : base("line longer than " + limit + " bytes") { }
    }

    public sealed class LineFramer
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly int maxLineBytes;
        private readonly MemoryStream pending = new MemoryStream();

        public LineFramer(int maxLineBytes = ProtocolDefaults.MaxLineBytes)
        {
            this.maxLineBytes = maxLineBytes;
        }

        /// <summary>
        /// Adds received bytes and returns the lines they complete, without terminators (an empty line is returned as
        /// ""). Throws <see cref="LineTooLongException"/> as soon as the line being assembled exceeds the limit.
        /// </summary>
        public List<string> Feed(byte[] buffer, int offset, int count)
        {
            var lines = new List<string>();
            var start = offset;
            var end = offset + count;
            for (var i = offset; i < end; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                pending.Write(buffer, start, i - start);
                lines.Add(TakeLine());
                start = i + 1;
            }
            pending.Write(buffer, start, end - start);
            // A \r that ends up as the line's last byte is stripped, so allow one byte of slack for it.
            if (pending.Length > maxLineBytes + 1 || (pending.Length == maxLineBytes + 1 && LastByte() != (byte)'\r'))
                throw new LineTooLongException(maxLineBytes);
            return lines;
        }

        private byte LastByte()
        {
            return pending.GetBuffer()[pending.Length - 1];
        }

        private string TakeLine()
        {
            var bytes = pending.GetBuffer();
            var length = (int)pending.Length;
            if (length > 0 && bytes[length - 1] == (byte)'\r') length--;
            if (length > maxLineBytes)
            {
                pending.SetLength(0);
                throw new LineTooLongException(maxLineBytes);
            }
            var text = Utf8.GetString(bytes, 0, length);
            pending.SetLength(0);
            return text;
        }
    }
}

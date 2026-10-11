using System;
using System.Buffers.Binary;
using System.Text;

namespace MdxPreview.Core.Mdx
{
    /// <summary>
    /// Bounds-checked little-endian cursor used by <see cref="MdxLoader"/>.
    ///
    /// MDX chunks are nested blocks of the shape <c>[tag][size][payload]</c> and the
    /// engine reference (games/warcraft-3/renderer/mdx/r_mdx_load.c) reads them through
    /// <c>sizeBuf_t</c>, which never looks past the end of its own view. This type
    /// mirrors that shape with explicit bounds: every read either succeeds or throws
    /// <see cref="MdxParseException"/> carrying the chunk name and the absolute file
    /// offset, so a malformed model can never read outside the buffer or spin.
    ///
    /// The C loader treats a failed read as "dest, and produce zeroes"; that tolerance
    /// is modelled here by the <c>TryRead*</c> helpers plus the loader's explicit
    /// diagnostics, not by silently truncating reads.
    /// </summary>
    internal sealed class MdxChunkReader
    {
        readonly byte[] _data;
        readonly int _start;
        readonly int _end;
        int _pos;

        public MdxChunkReader(byte[] data, int start, int length, string context)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (start < 0 || length < 0 || (long)start + length > data.Length)
            {
                throw new MdxParseException(
                    $"view [{start}, {start + length}) does not fit the {data.Length}-byte buffer",
                    context ?? string.Empty, start);
            }
            _data = data;
            _start = start;
            _end = start + length;
            _pos = start;
            Context = context ?? string.Empty;
        }

        /// <summary>Diagnostic name of the record currently being read ("GEOS[3].VRTX").</summary>
        public string Context { get; set; }

        /// <summary>Bytes consumed inside this view (mirrors the C <c>readcount</c>).</summary>
        public int Position => _pos - _start;

        /// <summary>Size of this view (mirrors the C <c>cursize</c>).</summary>
        public int Length => _end - _start;

        /// <summary>Bytes left in this view (mirrors <c>cursize - readcount</c>).</summary>
        public int Remaining => _end - _pos;

        public bool AtEnd => _pos >= _end;

        /// <summary>Absolute offset of the cursor in the backing buffer.</summary>
        public int AbsolutePosition => _pos;

        /// <summary>Absolute offset one past the end of this view.</summary>
        public int AbsoluteEnd => _end;


        /// <summary>A nested view over this view's backing buffer (the C inner block).</summary>
        public MdxChunkReader Slice(int absoluteStart, int absoluteEnd, string context)
        {
            if (absoluteStart < _start || absoluteEnd > _end || absoluteEnd < absoluteStart)
            {
                throw new MdxParseException(
                    $"nested view [{absoluteStart}, {absoluteEnd}) escapes [{_start}, {_end})",
                    context ?? Context, absoluteStart);
            }
            return new MdxChunkReader(_data, absoluteStart, absoluteEnd - absoluteStart, context);
        }

        public void SeekToEnd() => _pos = _end;

        public void SkipToAbsolute(int absoluteOffset)
        {
            if (absoluteOffset < _start || absoluteOffset > _end)
            {
                throw new MdxParseException(
                    $"cursor move to {absoluteOffset} escapes [{_start}, {_end})",
                    Context, _pos);
            }
            _pos = absoluteOffset;
        }


        void Require(int bytes, string what)
        {
            if (bytes < 0 || _pos + bytes > _end)
            {
                throw new MdxParseException(
                    $"{Context}: truncated read of {what} ({bytes} byte(s) required at offset {_pos}, " +
                    $"only {_end - _pos} byte(s) left in the [{_start}, {_end}) view)",
                    Context, _pos);
            }
        }

        /// <summary>
        /// Validates a bulk payload length with 64-bit arithmetic, so a hostile element count
        /// (0xFFFFFFFF keys, gigabytes of vertices) fails as a parse error instead of overflowing
        /// an int and allocating.
        /// </summary>
        void RequireElements(long count, int strideBytes, string what)
        {
            long totalBytes = count * strideBytes;
            if (count < 0 || totalBytes < 0 || totalBytes > _end - _pos)
            {
                throw new MdxParseException(
                    $"{Context}: {what} needs {count} element(s) = {totalBytes} byte(s) but only " +
                    $"{_end - _pos} byte(s) are left at offset {_pos}",
                    Context, _pos);
            }
        }

        public uint ReadU32()
        {
            Require(4, "u32");
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos, 4));
            _pos += 4;
            return value;
        }

        public int ReadI32()
        {
            Require(4, "i32");
            int value = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos, 4));
            _pos += 4;
            return value;
        }

        public float ReadF32()
        {
            Require(4, "f32");
            float value = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_pos, 4));
            _pos += 4;
            return value;
        }

        public byte ReadU8()
        {
            Require(1, "u8");
            return _data[_pos++];
        }

        public Math3D.Vec3 ReadVec3()
            => new Math3D.Vec3(ReadF32(), ReadF32(), ReadF32());

        public MdxBounds ReadBounds()
        {
            MdxBounds bounds;
            bounds.Radius = ReadF32();
            bounds.Min = ReadVec3();
            bounds.Max = ReadVec3();
            return bounds;
        }

        public byte[] ReadBytes(int count)
        {
            Require(count, "byte[]");
            byte[] result = new byte[count];
            if (count > 0) Buffer.BlockCopy(_data, _pos, result, 0, count);
            _pos += count;
            return result;
        }

        /// <summary>
        /// Bulk float read (VRTX/NRMS/UVBS/MTGC/... payloads). A single bounds check plus a
        /// block copy keeps whole-model parsing in the millisecond range.
        /// </summary>
        public float[] ReadFloats(int count)
        {
            RequireElements(count, 4, "float[]");
            float[] result = new float[count];
            if (count > 0)
            {
                if (BitConverter.IsLittleEndian)
                {
                    Buffer.BlockCopy(_data, _pos, result, 0, count * 4);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        result[i] = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_pos + i * 4, 4));
                }
                _pos += count * 4;
            }
            return result;
        }

        public int[] ReadInt32s(int count)
        {
            RequireElements(count, 4, "i32[]");
            int[] result = new int[count];
            if (count > 0)
            {
                if (BitConverter.IsLittleEndian)
                {
                    Buffer.BlockCopy(_data, _pos, result, 0, count * 4);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        result[i] = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos + i * 4, 4));
                }
                _pos += count * 4;
            }
            return result;
        }

        public ushort[] ReadUInt16s(int count)
        {
            RequireElements(count, 2, "u16[]");
            ushort[] result = new ushort[count];
            if (count > 0)
            {
                if (BitConverter.IsLittleEndian)
                {
                    Buffer.BlockCopy(_data, _pos, result, 0, count * 2);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        result[i] = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos + i * 2, 2));
                }
                _pos += count * 2;
            }
            return result;
        }

        /// <summary>
        /// Fixed-size NUL-padded string field (mdxObjectName_t / mdxFileName_t). The C code
        /// relies on the C string terminator, so everything from the first NUL is dropped.
        /// Bytes are mapped 1:1 (Latin-1) so authored path bytes survive untouched.
        /// </summary>
        public string ReadFixedString(int length)
        {
            Require(length, "fixed string");
            int nul = -1;
            int scanEnd = _pos + length;
            for (int i = _pos; i < scanEnd; i++)
            {
                if (_data[i] == 0) { nul = i; break; }
            }
            int textLength = nul >= 0 ? nul - _pos : length;
            string value = textLength > 0 ? Encoding.Latin1.GetString(_data, _pos, textLength) : string.Empty;
            _pos = scanEnd;
            return value;
        }

        /// <summary>Reads a u32 at an absolute offset of the backing buffer (record resync scan).</summary>
        public static uint PeekU32(byte[] data, int absoluteOffset)
            => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(absoluteOffset, 4));

        public static int PeekI32(byte[] data, int absoluteOffset)
            => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(absoluteOffset, 4));

        /// <summary>FourCC bytes as a printable tag ("GEOS", "KP2V", "?123" for binary garbage).</summary>
        public static string TagName(uint tag)
        {
            Span<char> chars = stackalloc char[4];
            for (int i = 0; i < 4; i++)
            {
                int b = (int)((tag >> (8 * i)) & 0xFF);
                chars[i] = b >= 0x20 && b < 0x7F ? (char)b : '.';
            }
            return new string(chars);
        }
    }
}

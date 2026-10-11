using System;

namespace MdxPreview.Core.Blp
{
    public sealed class BlpException : Exception
    {
        public BlpException(string message) : base(message) { }
    }

    public enum BlpVersion
    {
        Blp1 = 1,
        Blp2 = 2,
    }

    /// <summary>
    /// How the decoded first mip is handed to the caller. BLP1 files may carry
    /// JPEG-compressed content; the core layer deliberately keeps that as a
    /// complete JPEG byte stream so the Godot layer can decode it with
    /// <c>Image.LoadJpgFromBuffer</c> instead of shipping a JPEG decoder here.
    /// </summary>
    public enum BlpPayloadKind
    {
        Rgba8,
        Jpeg,
    }

    /// <summary>
    /// Decoded BLP texture (Warcraft III BLP1 and World of Warcraft BLP2).
    /// Pure C# port of renderer/r_blp1.c and renderer/r_blp2.c.
    ///
    /// Every RGBA8 buffer (mip 0 in <see cref="Rgba"/> and the lower levels in
    /// <see cref="MipRgba"/>) is top-down, row 0 first, which is what
    /// <c>Image.CreateFromData(width, height, false, Image.Format.Rgba8, ...)</c>
    /// expects and what the C reference produces: the paletted paths fill the
    /// destination sequentially (renderer/r_blp1.c:213, renderer/r_blp2.c:169)
    /// and the DXT path writes <c>pixels[y * width + x]</c>
    /// (renderer/r_blp2.c:355), with stb_image also returning top-down rows.
    /// No vertical flip is applied.
    ///
    /// <para>
    /// Alpha semantics follow the C reference: 8-bit linear alpha, never
    /// premultiplied (renderer/r_blp1.c:216 assigns the raw byte,
    /// renderer/r_blp2.c:275 expands DXT3 nibbles with <c>(a &lt;&lt; 4) | a</c>),
    /// and the renderer uploads it untouched as GL_RGBA/GL_BGRA
    /// (renderer/r_texture.c:297).
    /// </para>
    ///
    /// <para>
    /// JPEG payloads: Warcraft III BLP1 JPEGs carry alpha as the <b>4th
    /// component of that same baseline JPEG</b> — one scan, SOF <c>Nf=4</c>,
    /// SOS <c>Ns=4</c> (verified on every sampled unit/building texture), with
    /// the bands stored as raw B,G,R,A (no YCbCr transform, which is why
    /// renderer/r_blp1.c compiles stb_image with
    /// <c>STBI_WARCRAFT3_BLP_JPEG_RGBA_BANDS</c>, renderer/stb/stb_image.h:3957).
    /// There is no separate alpha plane or alpha-only JPEG stream in the file,
    /// so <see cref="AlphaChannel"/> cannot be produced without decoding the
    /// JPEG; the composition is
    /// <c>R = band2, G = band1, B = band0, A = band3</c>
    /// (renderer/r_blp1.c:195-200 converts exactly that band order into its BGRA
    /// <c>color32_t</c>). Note that Godot's own
    /// <c>Image.LoadJpgFromBuffer</c> refuses 4-component JPEGs
    /// ("JPEG files with CMYK or YCCK colorspaces are not currently supported"),
    /// so a Warcraft III aware JPEG decode is required on the Godot side.
    /// </para>
    /// </summary>
    public sealed class BlpImage
    {
        public BlpVersion Version;
        public BlpPayloadKind Kind;

        public int Width;
        public int Height;
        public int MipCount;

        /// <summary>Mip 0 as RGBA8 (Width*Height*4) when <see cref="Kind"/> is Rgba8.</summary>
        public byte[] Rgba;

        /// <summary>
        /// Complete JPEG stream for mip 0 when <see cref="Kind"/> is Jpeg: the
        /// shared header (renderer/r_blp1.c:143-150, ending with the SOF marker)
        /// followed by mip 0's entropy payload (renderer/r_blp1.c:170-176).
        /// </summary>
        public byte[] JpegData;

        /// <summary>
        /// Decoded lower mips as RGBA8, indexed by mip level, so
        /// <c>MipRgba[0] == Rgba</c>. Entries are null for JPEG payloads (the
        /// core layer does not run a JPEG decoder) and, defensively, for any
        /// level that could not be produced.
        /// </summary>
        public byte[][] MipRgba;

        byte[] _alphaChannel;
        byte[][] _mipAlpha;

        /// <summary>
        /// Mip 0 8-bit alpha plane (Width*Height), or null when the payload has
        /// no alpha or when it cannot be separated from the colour data.
        ///
        /// For <see cref="BlpPayloadKind.Rgba8"/> payloads the plane is derived
        /// lazily from the alpha bytes of <see cref="Rgba"/> (they are the same
        /// values the C decoders produce). For <see cref="BlpPayloadKind.Jpeg"/>
        /// payloads it stays null — see the class remarks: alpha is the 4th
        /// component of <see cref="JpegData"/> itself, so a WC3-aware JPEG
        /// decoder is needed. A caller that runs such a decoder may assign the
        /// extracted plane here; <see cref="HasAlpha"/> and the rest of the
        /// object are unaffected.
        /// </summary>
        public byte[] AlphaChannel
        {
            get
            {
                if (_alphaChannel == null && Kind == BlpPayloadKind.Rgba8 && Rgba != null)
                    _alphaChannel = BlpIo.ExtractAlphaPlane(Rgba, Rgba.Length / 4);
                return _alphaChannel;
            }
            set => _alphaChannel = value;
        }

        /// <summary>
        /// Per-level alpha planes mirroring <see cref="MipRgba"/>; entries are
        /// null wherever <see cref="MipRgba"/> is null (notably every level of a
        /// JPEG payload). Derived lazily, same rules as
        /// <see cref="AlphaChannel"/>, and settable for externally decoded
        /// planes.
        /// </summary>
        public byte[][] MipAlpha
        {
            get
            {
                if (_mipAlpha == null && MipRgba != null)
                {
                    var mips = new byte[MipRgba.Length][];
                    for (int level = 0; level < MipRgba.Length; level++)
                        if (MipRgba[level] != null)
                            mips[level] = BlpIo.ExtractAlphaPlane(MipRgba[level], MipRgba[level].Length / 4);
                    _mipAlpha = mips;
                }
                return _mipAlpha;
            }
            set => _mipAlpha = value;
        }

        /// <summary>
        /// Diagnostics: raw header fields. BLP2 stores encoding (1 paletted,
        /// 2 DXT, 3 raw BGRA), alphaDepth and alphaEncoding directly; BLP1 has
        /// no encoding/alphaEncoding fields, so <see cref="RawEncoding"/> is the
        /// header's <c>type</c> (0 = shared-header JPEG, non-zero = paletted) and
        /// <see cref="RawAlphaEncoding"/> is always 0.
        /// </summary>
        public uint RawEncoding;
        public uint RawAlphaDepth;
        public uint RawAlphaEncoding;

        /// <summary>
        /// Component count declared by the SOF marker inside
        /// <see cref="JpegData"/>, or 0 when the payload is not JPEG or no SOF
        /// marker was found. Every Warcraft III BLP1 JPEG sampled declares 4
        /// components, which is what makes Godot's
        /// <c>Image.LoadJpgFromBuffer</c> reject the stream as CMYK/YCCK.
        /// </summary>
        public int JpegBandCount;

        /// <summary>
        /// True when <see cref="JpegData"/> carries a 4th component, i.e. the
        /// Warcraft III "RGB + alpha in one JPEG" layout that needs the
        /// <c>{band0, band1, band2, band3} = {B, G, R, A}</c> interpretation.
        /// Independent of <see cref="Kind"/>: such a stream is decoded by
        /// <see cref="JpegWarcraft3Decoder"/> into <see cref="Rgba"/>, and
        /// <see cref="JpegData"/> is still kept for diagnostics.
        /// </summary>
        public bool JpegHasAlphaBand => JpegData != null && JpegBandCount >= 4;

        /// <summary>
        /// Existing contract, unchanged: for JPEG payloads the header's
        /// alphaDepth decides (the 4th JPEG band exists even in files that
        /// declare <c>alphaBits = 0</c>, which is why
        /// <see cref="JpegHasAlphaBand"/> is reported separately); for decoded
        /// payloads it reports whether any pixel is not fully opaque.
        /// </summary>
        public bool HasAlpha => Kind == BlpPayloadKind.Jpeg
            ? RawAlphaDepth != 0
            : RgbaHasAlpha();

        bool RgbaHasAlpha()
        {
            if (Rgba == null) return false;
            for (int i = 3; i < Rgba.Length; i += 4)
                if (Rgba[i] != 255) return true;
            return false;
        }

        /// <summary>
        /// Decodes a BLP1/BLP2 file. Throws <see cref="BlpException"/> for
        /// malformed or unsupported input.
        /// </summary>
        public static BlpImage Decode(byte[] data)
        {
            if (data == null)
                throw new BlpException("BLP: no data supplied.");
            if (data.Length < 4)
                throw new BlpException($"BLP: file too small ({data.Length} bytes) to hold a magic value.");

            uint magic = BlpIo.U32(data, 0);
            switch (magic)
            {
                case Blp1Decoder.Magic:
                    return Blp1Decoder.Decode(data);
                case Blp2Decoder.Magic:
                    return Blp2Decoder.Decode(data);
                default:
                    throw new BlpException(
                        $"BLP: bad magic 0x{magic:X8} (expected \"BLP1\" or \"BLP2\").");
            }
        }

        public override string ToString()
            => $"BLP{(int)Version} {Width}x{Height} mips={MipCount} kind={Kind} " +
               $"encoding={RawEncoding} alphaDepth={RawAlphaDepth} alphaEncoding={RawAlphaEncoding}";
    }
}

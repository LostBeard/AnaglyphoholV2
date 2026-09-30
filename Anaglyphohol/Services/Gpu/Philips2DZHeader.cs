namespace Anaglyphohol.Services.Gpu
{
    public enum HeaderDataFormats
    {
        HEADER_DATA_FORMAT_BBBA,
        HEADER_DATA_FORMAT_RGBA,
        HEADER_DATA_FORMAT_BGRA,
        HEADER_DATA_FORMAT_BBBB,
    }

    /// <summary>
    /// The Philips / Dimenco 2D+Z header: a 512 x 1 pixel row drawn at the screen's top-left that tells a Dimenco
    /// display the frame is 2D+Z and carries its depth factor/offset. Ported from SpawnDev.BlazorJS.MultiView.Dimenco
    /// (pure C#, no JS). 32 header bytes (10 basic incl. CRC32 + 22 extended incl. CRC32) = 256 bits, one bit on every
    /// EVEN pixel.
    /// </summary>
    public class Philips2DZHeader
    {
        public const int HeaderWidth = 512;
        public const int HeaderHeight = 1;
        public const int BytesPerPixel = 4;

        public int Width => HeaderWidth;
        public int Height => HeaderHeight;
        public bool Dirty { get; private set; } = true;

        byte _Factor = 16;
        /// <summary>Depth factor (3D strength). Anaglyphohol maps Level3D 0..1 to 0..255.</summary>
        public byte Factor { get => _Factor; set => Set(ref _Factor, value); }
        byte _Offset = 128;
        /// <summary>Depth offset (convergence). Anaglyphohol maps Focus3D 0..1 to 0..255.</summary>
        public byte Offset { get => _Offset; set => Set(ref _Offset, value); }
        byte _ContentType = 3;
        /// <summary>0 no depth, 1 signage, 2 movie, 3 game, 4 CGI, 5 still.</summary>
        public byte ContentType { get => _ContentType; set => Set(ref _ContentType, value); }
        byte _DataType = 0;
        /// <summary>0 default (2D plus depth), 1 declipse (redundant data removed), 2 declipse (full background data).</summary>
        public byte DataType { get => _DataType; set => Set(ref _DataType, value); }
        bool _HeaderFactorEnabled = true;
        public bool HeaderFactorEnabled { get => _HeaderFactorEnabled; set => Set(ref _HeaderFactorEnabled, value); }
        bool _HeaderOffsetEnabled = true;
        public bool HeaderOffsetEnabled { get => _HeaderOffsetEnabled; set => Set(ref _HeaderOffsetEnabled, value); }
        HeaderDataFormats _Format = HeaderDataFormats.HEADER_DATA_FORMAT_BBBA;
        public HeaderDataFormats Format { get => _Format; set => Set(ref _Format, value); }
        bool _TransparentUnusedPixels = true;
        public bool TransparentUnusedPixels { get => _TransparentUnusedPixels; set => Set(ref _TransparentUnusedPixels, value); }

        readonly byte[] _HeaderData = new byte[HeaderWidth * BytesPerPixel];
        /// <summary>RGBA pixels of the header row (regenerated when dirty).</summary>
        public byte[] HeaderData
        {
            get
            {
                if (Dirty) UpdateHeaderData();
                return _HeaderData;
            }
        }

        /// <summary>Raised when a field changes and the header pixels must be redrawn.</summary>
        public event Action? OnHeaderDirty;

        void Set<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            if (Dirty) return;
            Dirty = true;
            OnHeaderDirty?.Invoke();
        }

        /// <summary>The 32 header bytes (basic 6 + CRC32, extended 18 + CRC32), before expansion to pixels.</summary>
        public byte[] GetHeaderBytes()
        {
            var h = new List<byte> { 241, ContentType, Factor, Offset, 0, 0 };
            if (HeaderFactorEnabled) h[4] |= 128;
            if (HeaderOffsetEnabled) h[4] |= 64;
            AppendCrc(h);
            var h2 = new List<byte> { 242, 20, DataType switch { 1 => (byte)154, 2 => (byte)239, _ => (byte)0 } };
            while (h2.Count < 18) h2.Add(0);
            AppendCrc(h2);
            h.AddRange(h2);
            return h.ToArray();
        }

        static void AppendCrc(List<byte> bytes)
        {
            uint crc = CalcCRC32(bytes);
            bytes.Add((byte)(crc >> 24));
            bytes.Add((byte)(crc >> 16));
            bytes.Add((byte)(crc >> 8));
            bytes.Add((byte)crc);
        }

        void UpdateHeaderData()
        {
            var h = GetHeaderBytes();
            int hbit = 0, hbyte = 0;
            for (var i = 0; i < _HeaderData.Length; i += 4)
            {
                _HeaderData[i] = 0;
                _HeaderData[i + 1] = 0;
                _HeaderData[i + 2] = 0;
                _HeaderData[i + 3] = (byte)(TransparentUnusedPixels ? 0 : 255);
                if (i % 8 != 0) continue;   // one bit on every even pixel
                var b = (byte)(255 * ((h[hbyte] >> (7 - hbit)) & 1));
                switch (Format)
                {
                    case HeaderDataFormats.HEADER_DATA_FORMAT_BBBA:
                        _HeaderData[i] = b; _HeaderData[i + 1] = b; _HeaderData[i + 2] = b;
                        break;
                    case HeaderDataFormats.HEADER_DATA_FORMAT_RGBA:
                        _HeaderData[i + 2] = b;   // RGBA: blue is byte 2
                        break;
                    case HeaderDataFormats.HEADER_DATA_FORMAT_BGRA:
                        _HeaderData[i] = b;       // BGRA: blue is byte 0
                        break;
                    case HeaderDataFormats.HEADER_DATA_FORMAT_BBBB:
                        _HeaderData[i] = b; _HeaderData[i + 1] = b; _HeaderData[i + 2] = b; _HeaderData[i + 3] = b;
                        break;
                }
                if (Format != HeaderDataFormats.HEADER_DATA_FORMAT_BBBB) _HeaderData[i + 3] = 255;
                if (++hbit == 8)
                {
                    hbit = 0;
                    hbyte++;
                }
            }
            Dirty = false;
        }

        static readonly uint[] CrcTable = BuildCrcTable();

        /// <summary>MSB-first CRC-32 (polynomial 0x04C11DB7, init 0, no reflection, no final xor) - the Philips header CRC.</summary>
        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
                table[n] = c;
            }
            return table;
        }

        public static uint CalcCRC32(IReadOnlyList<byte> p)
        {
            uint crc = 0;
            for (int i = 0; i < p.Count; i++) crc = CrcTable[(crc >> 24) ^ p[i]] ^ (crc << 8);
            return crc;
        }
    }
}

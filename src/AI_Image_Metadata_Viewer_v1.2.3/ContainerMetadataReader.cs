using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AIImageMetadataViewer;

internal static class ContainerMetadataReader
{
    public static ParsedContainer Read(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.RandomAccess);
        if (stream.Length == 0) throw new InvalidDataException("0 byteのファイルです。");
        var header = new byte[(int)Math.Min(64, stream.Length)];
        stream.ReadExactly(header);
        stream.Position = 0;
        var format = BinaryHelpers.DetectFormat(header);
        var result = new ParsedContainer { Format = format };
        switch (format)
        {
            case ImageContainerFormat.Png: ReadPng(stream, result, cancellationToken); break;
            case ImageContainerFormat.Jpeg: ReadJpeg(stream, result, cancellationToken); break;
            case ImageContainerFormat.WebP: ReadWebP(stream, result, cancellationToken); break;
            case ImageContainerFormat.Tiff: ReadTiff(stream, result); break;
            case ImageContainerFormat.Bmp: ReadBmp(stream, result); break;
            case ImageContainerFormat.Gif: ReadGif(stream, result, cancellationToken); break;
            case ImageContainerFormat.Avif:
            case ImageContainerFormat.Heif: ReadIsoBmff(stream, result, cancellationToken); break;
            case ImageContainerFormat.Jxl:
                result.Raw.Add(new RawMetadataItem("JXL", "signature", "JPEG XL", "Container", header.Length, BinaryHelpers.Preview(header)));
                break;
            default: throw new InvalidDataException("対応する画像シグネチャではありません（拡張子偽装の可能性があります）。");
        }
        if (result.DpiY == 0 && result.DpiX > 0) result.DpiY = result.DpiX;
        if (result.DpiX == 0 && result.DpiY > 0) result.DpiX = result.DpiY;
        return result;
    }

    private static void ReadPng(Stream stream, ParsedContainer result, CancellationToken ct)
    {
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        var metadataLoaded = 0L;
        var seenIhdr = false;
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (stream.Length - stream.Position < 12) throw new InvalidDataException("PNGチャンクが途中で切れています。");
            var chunkHeader = new byte[8];
            stream.ReadExactly(chunkHeader);
            var length = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[..4]);
            var type = Encoding.ASCII.GetString(chunkHeader[4..8]);
            if (length > int.MaxValue || length > stream.Length - stream.Position - 4)
                throw new InvalidDataException($"PNG {type} チャンク長が不正です。");
            var payload = type is "IDAT" or "fdAT";
            var shouldRead = !payload && length <= AppLimits.MaxMetadataEntryBytes &&
                             metadataLoaded + length <= AppLimits.MaxMetadataTotalBytes;
            byte[]? data = null;
            if (shouldRead)
            {
                data = new byte[(int)length];
                stream.ReadExactly(data);
                metadataLoaded += length;
            }
            else BinaryHelpers.Skip(stream, length);
            var crc = new byte[4];
            stream.ReadExactly(crc);

            var value = payload ? "画像データ（内容は非表示）" : data is null ? "サイズ上限のため内容は非表示" : PngValue(type, data);
            result.Raw.Add(new RawMetadataItem("PNG", type, PngChunkName(type), char.IsUpper(type[0]) ? "Critical chunk" : "Ancillary chunk",
                length, value, payload));
            if (data is null) { if (type == "IEND") break; continue; }
            switch (type)
            {
                case "IHDR":
                    if (seenIhdr || data.Length != 13) throw new InvalidDataException("PNG IHDRが不正です。");
                    seenIhdr = true;
                    result.Width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)));
                    result.Height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)));
                    result.BitDepth = data[8] * PngChannels(data[9]);
                    result.HasAlpha = data[9] is 4 or 6;
                    break;
                case "tRNS": result.HasAlpha = true; break;
                case "tEXt": ParsePngText(data, result, false, false); break;
                case "zTXt": ParsePngText(data, result, true, false); break;
                case "iTXt": ParsePngText(data, result, false, true); break;
                case "eXIf": ExifReader.AddTo(result, data, "PNG/eXIf"); break;
                case "pHYs":
                    if (data.Length == 9 && data[8] == 1)
                    {
                        result.DpiX = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)) * 0.0254;
                        result.DpiY = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)) * 0.0254;
                    }
                    break;
                case "sRGB": result.ColorSpace = "sRGB"; break;
                case "iCCP": result.ColorSpace = "ICC Profile"; break;
                case "acTL":
                    if (data.Length >= 8) result.FrameCount = Math.Max(1, checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4))));
                    break;
            }
            if (type == "IEND") break;
        }
        if (!seenIhdr || result.Width <= 0 || result.Height <= 0) throw new InvalidDataException("PNG IHDRがありません。");
    }

    private static void ParsePngText(byte[] data, ParsedContainer result, bool compressed, bool international)
    {
        try
        {
            var p = Array.IndexOf(data, (byte)0);
            if (p is < 1 or > 79) throw new InvalidDataException("PNGテキストのキーワードが不正です。");
            var key = Encoding.Latin1.GetString(data, 0, p);
            string value;
            if (international)
            {
                if (p + 3 > data.Length) throw new InvalidDataException("iTXtヘッダーが短すぎます。");
                var compressionFlag = data[p + 1];
                var cursor = p + 3;
                cursor = FindNullAfter(data, cursor) + 1; // language tag
                cursor = FindNullAfter(data, cursor) + 1; // translated keyword
                var bytes = data.AsSpan(cursor).ToArray();
                if (compressionFlag == 1) bytes = DecompressLimited(bytes);
                else if (compressionFlag != 0) throw new InvalidDataException("iTXt圧縮フラグが不正です。");
                value = Encoding.UTF8.GetString(bytes);
            }
            else if (compressed)
            {
                if (p + 2 > data.Length || data[p + 1] != 0) throw new InvalidDataException("zTXt圧縮方式が不正です。");
                value = Encoding.Latin1.GetString(DecompressLimited(data.AsSpan(p + 2).ToArray()));
            }
            else value = Encoding.Latin1.GetString(data, p + 1, data.Length - p - 1);
            result.AddText(key, value);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            result.Raw.Add(new RawMetadataItem("PNG/Text", "error", "破損テキスト", "Error", data.Length, ex.Message));
        }
    }

    private static int FindNullAfter(byte[] data, int start)
    {
        var p = Array.IndexOf(data, (byte)0, start);
        if (p < 0) throw new InvalidDataException("iTXt文字列終端がありません。");
        return p;
    }

    private static byte[] DecompressLimited(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = zlib.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > AppLimits.MaxMetadataEntryBytes) throw new InvalidDataException("展開後テキストが上限を超えました。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void ReadJpeg(Stream stream, ParsedContainer result, CancellationToken ct)
    {
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8) throw new InvalidDataException("JPEG SOIがありません。");
        result.Raw.Add(new RawMetadataItem("JPEG", "FFD8", "SOI", "Marker", 0, "Start of Image"));
        var appCounts = new Dictionary<int, int>();
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            int b;
            do { b = stream.ReadByte(); } while (b != 0xFF && b >= 0);
            if (b < 0) break;
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xFF);
            if (marker < 0) throw new InvalidDataException("JPEGマーカーが途中で切れています。");
            if (marker == 0xD9) { result.Raw.Add(new RawMetadataItem("JPEG", "FFD9", "EOI", "Marker", 0, "End of Image")); break; }
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue;
            var lenBytes = new byte[2];
            stream.ReadExactly(lenBytes);
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(lenBytes);
            if (segmentLength < 2 || segmentLength - 2 > stream.Length - stream.Position)
                throw new InvalidDataException($"JPEG FF{marker:X2}セグメント長が不正です。");
            var dataLength = segmentLength - 2;
            if (marker == 0xDA)
            {
                BinaryHelpers.Skip(stream, dataLength);
                result.Raw.Add(new RawMetadataItem("JPEG", "FFDA", "SOS / entropy-coded data", "Image payload",
                    stream.Length - stream.Position, "画像データ（内容は非表示）", true));
                break;
            }
            byte[]? data = dataLength <= AppLimits.MaxMetadataEntryBytes ? new byte[dataLength] : null;
            if (data is null) BinaryHelpers.Skip(stream, dataLength); else stream.ReadExactly(data);
            var name = JpegMarkerName(marker);
            if (marker is >= 0xE0 and <= 0xEF)
            {
                var index = appCounts.GetValueOrDefault(marker) + 1;
                appCounts[marker] = index;
                name += $" #{index}";
            }
            result.Raw.Add(new RawMetadataItem("JPEG", $"FF{marker:X2}", name, "Segment", dataLength,
                data is null ? "サイズ上限のため内容は非表示" : JpegValue(marker, data), false));
            if (data is null) continue;
            if (IsSof(marker) && data.Length >= 6)
            {
                result.BitDepth = data[0] * data[5];
                result.Height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(1, 2));
                result.Width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(3, 2));
            }
            else if (marker == 0xE1 && data.AsSpan().StartsWith("Exif\0\0"u8)) ExifReader.AddTo(result, data, "JPEG/APP1 EXIF");
            else if (marker == 0xE1 && data.AsSpan().StartsWith("http://ns.adobe.com/xap/1.0/\0"u8))
            {
                var value = Encoding.UTF8.GetString(data, 29, data.Length - 29);
                result.AddText("XMP", value);
            }
            else if (marker == 0xFE) result.AddText("Comment", Encoding.Latin1.GetString(data));
            else if (marker == 0xE2 && data.AsSpan().StartsWith("ICC_PROFILE\0"u8)) result.ColorSpace = "ICC Profile";
        }
        if (result.Width <= 0 || result.Height <= 0) throw new InvalidDataException("JPEG画像サイズを取得できません。");
    }

    private static void ReadWebP(Stream stream, ParsedContainer result, CancellationToken ct)
    {
        Span<byte> riff = stackalloc byte[12];
        stream.ReadExactly(riff);
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(riff.Slice(4, 4));
        if (declared + 8L > stream.Length) throw new InvalidDataException("WebP RIFFサイズがファイル範囲外です。");
        var frames = 0;
        while (stream.Position + 8 <= Math.Min(stream.Length, declared + 8L))
        {
            ct.ThrowIfCancellationRequested();
            var ch = new byte[8];
            stream.ReadExactly(ch);
            var type = Encoding.ASCII.GetString(ch[..4]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(ch[4..]);
            if (length > int.MaxValue || length > stream.Length - stream.Position)
                throw new InvalidDataException($"WebP {type}チャンク長が不正です。");
            var payload = type is "VP8 " or "VP8L" or "ALPH" or "ANMF";
            byte[]? data = length <= AppLimits.MaxMetadataEntryBytes ? new byte[(int)length] : null;
            if (data is null) BinaryHelpers.Skip(stream, length); else stream.ReadExactly(data);
            if ((length & 1) != 0)
            {
                if (stream.Position >= stream.Length) throw new InvalidDataException("WebPパディングがありません。");
                stream.ReadByte();
            }
            result.Raw.Add(new RawMetadataItem("WebP", type, WebPChunkName(type), "RIFF chunk", length,
                payload ? "画像/アニメーションデータ（内容は非表示）" : data is null ? "サイズ上限のため内容は非表示" : BinaryHelpers.Preview(data), payload));
            if (data is null) continue;
            switch (type)
            {
                case "VP8X" when data.Length >= 10:
                    result.HasAlpha = (data[0] & 0x10) != 0;
                    result.Width = 1 + ReadU24(data.AsSpan(4, 3));
                    result.Height = 1 + ReadU24(data.AsSpan(7, 3));
                    break;
                case "VP8 " when data.Length >= 10 && result.Width == 0:
                    result.Width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2)) & 0x3FFF;
                    result.Height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2)) & 0x3FFF;
                    break;
                case "VP8L" when data.Length >= 5 && result.Width == 0:
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1, 4));
                    result.Width = 1 + (int)(bits & 0x3FFF);
                    result.Height = 1 + (int)((bits >> 14) & 0x3FFF);
                    result.HasAlpha = true;
                    break;
                case "EXIF": ExifReader.AddTo(result, data, "WebP/EXIF"); break;
                case "XMP ": result.AddText("XMP", Encoding.UTF8.GetString(data)); break;
                case "ICCP": result.ColorSpace = "ICC Profile"; break;
                case "ANMF": frames++; break;
            }
        }
        result.FrameCount = Math.Max(1, frames);
        result.BitDepth = result.HasAlpha ? 32 : 24;
        if (result.Width <= 0 || result.Height <= 0) throw new InvalidDataException("WebP画像サイズを取得できません。");
    }

    private static void ReadTiff(Stream stream, ParsedContainer result)
    {
        if (stream.Length > AppLimits.MaxMetadataTotalBytes)
            throw new InvalidDataException("大容量TIFFのメタデータ走査は安全上の上限を超えています（64 MiB）。");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        ExifReader.AddTo(result, data, "TIFF");
        var doc = ExifReader.Parse(data);
        PopulateTiffDimensions(doc.Root, doc.LittleEndian, result);
        result.Raw.Insert(0, new RawMetadataItem("TIFF", "header", "TIFF container", "Header", 8, BinaryHelpers.Preview(data.AsSpan(0, Math.Min(8, data.Length)))));
    }

    private static void PopulateTiffDimensions(ExifDirectory directory, bool le, ParsedContainer result)
    {
        foreach (var e in directory.Entries)
        {
            if (e.Data.Length == 0) continue;
            uint value = e.Type == 3 && e.Data.Length >= 2 ? BinaryHelpers.U16(e.Data, le) :
                         e.Type == 4 && e.Data.Length >= 4 ? BinaryHelpers.U32(e.Data, le) : 0;
            if (e.Tag == 0x0100) result.Width = checked((int)value);
            if (e.Tag == 0x0101) result.Height = checked((int)value);
            if (e.Tag == 0x0102) result.BitDepth = checked((int)value);
            if (e.Tag == 0x0115 && result.BitDepth > 0) result.BitDepth *= checked((int)value);
        }
    }

    private static void ReadBmp(Stream stream, ParsedContainer result)
    {
        var length = (int)Math.Min(stream.Length, 256);
        var data = new byte[length];
        stream.ReadExactly(data);
        if (data.Length < 30) throw new InvalidDataException("BMPヘッダーが短すぎます。");
        var dib = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14, 4));
        if (dib >= 40 && data.Length >= 54)
        {
            result.Width = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18, 4)));
            result.Height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22, 4)));
            result.BitDepth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28, 2));
            result.HasAlpha = result.BitDepth == 32;
            var xppm = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(38, 4));
            var yppm = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(42, 4));
            if (xppm > 0) result.DpiX = xppm * 0.0254;
            if (yppm > 0) result.DpiY = yppm * 0.0254;
        }
        result.Raw.Add(new RawMetadataItem("BMP", "DIB", "Bitmap header", $"DIB {dib}", data.Length, BinaryHelpers.Preview(data)));
    }

    private static void ReadGif(Stream stream, ParsedContainer result, CancellationToken ct)
    {
        var data = new byte[(int)Math.Min(stream.Length, AppLimits.MaxMetadataTotalBytes)];
        stream.ReadExactly(data);
        if (data.Length < 13) throw new InvalidDataException("GIFヘッダーが短すぎます。");
        result.Width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6, 2));
        result.Height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2));
        result.BitDepth = ((data[10] & 7) + 1) * 3;
        result.Raw.Add(new RawMetadataItem("GIF", "LSD", "Logical Screen Descriptor", "Header", 13, BinaryHelpers.Preview(data.AsSpan(0, 13))));
        var frames = 0;
        for (var i = 13; i < data.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (data[i] == 0x2C) frames++;
            if (i + 2 < data.Length && data[i] == 0x21 && data[i + 1] == 0xFE)
            {
                var p = i + 2;
                using var text = new MemoryStream();
                while (p < data.Length && data[p] != 0)
                {
                    var n = data[p++];
                    if (p + n > data.Length) break;
                    text.Write(data, p, n); p += n;
                    if (text.Length > AppLimits.MaxMetadataEntryBytes) break;
                }
                var comment = Encoding.Latin1.GetString(text.ToArray());
                result.AddText("Comment", comment);
                result.Raw.Add(new RawMetadataItem("GIF", "21FE", "Comment Extension", "Extension", text.Length, TextSafety.Limit(comment, 4096)));
            }
        }
        result.FrameCount = Math.Max(1, frames);
    }

    private static void ReadIsoBmff(Stream stream, ParsedContainer result, CancellationToken ct)
    {
        while (stream.Position + 8 <= stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            var header = new byte[8];
            stream.ReadExactly(header);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var type = Encoding.ASCII.GetString(header[4..]);
            long boxSize = size;
            var headerSize = 8;
            if (size == 1)
            {
                var large = new byte[8]; stream.ReadExactly(large);
                boxSize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(large)); headerSize = 16;
            }
            else if (size == 0) boxSize = stream.Length - stream.Position + 8;
            if (boxSize < headerSize || boxSize - headerSize > stream.Length - stream.Position) throw new InvalidDataException($"ISO BMFF {type} box長が不正です。");
            var payload = boxSize - headerSize;
            var previewLength = (int)Math.Min(payload, 256);
            var preview = new byte[previewLength]; stream.ReadExactly(preview);
            BinaryHelpers.Skip(stream, payload - previewLength);
            result.Raw.Add(new RawMetadataItem("ISO BMFF", type, type, "Box", boxSize, BinaryHelpers.Preview(preview), type == "mdat"));
        }
    }

    private static int ReadU24(ReadOnlySpan<byte> d) => d[0] | d[1] << 8 | d[2] << 16;
    private static int PngChannels(byte colorType) => colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 1 };
    private static string PngValue(string type, byte[] data) => type switch
    {
        "IHDR" when data.Length == 13 => $"{BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4))} × {BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4))}, bit depth {data[8]}, color type {data[9]}",
        "pHYs" when data.Length == 9 => $"X={BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4))}, Y={BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4))}, unit={data[8]}",
        "tEXt" or "zTXt" or "iTXt" => TextSafety.Limit(Encoding.UTF8.GetString(data), 4096),
        _ => BinaryHelpers.Preview(data)
    };
    private static string PngChunkName(string type) => type switch
    {
        "IHDR" => "Image Header", "PLTE" => "Palette", "IDAT" => "Image Data", "IEND" => "Image End",
        "tEXt" => "Text", "zTXt" => "Compressed Text", "iTXt" => "International Text", "eXIf" => "EXIF",
        "iCCP" => "ICC Profile", "pHYs" => "Physical Pixel Dimensions", "tIME" => "Last Modification Time",
        "gAMA" => "Gamma", "cHRM" => "Chromaticities", "sRGB" => "Standard RGB", "acTL" => "APNG Animation Control",
        "fcTL" => "APNG Frame Control", "fdAT" => "APNG Frame Data", "tRNS" => "Transparency", _ => "Unknown/Custom Chunk"
    };
    private static string JpegMarkerName(int marker) => marker switch
    {
        0xE0 => "APP0 / JFIF", 0xE1 => "APP1 / EXIF or XMP", 0xE2 => "APP2 / ICC",
        0xED => "APP13 / IPTC", 0xEE => "APP14 / Adobe", 0xFE => "COM",
        _ when IsSof(marker) => "Start of Frame", _ when marker is >= 0xE0 and <= 0xEF => $"APP{marker - 0xE0}", _ => $"Marker FF{marker:X2}"
    };
    private static string JpegValue(int marker, byte[] data)
    {
        if (marker == 0xE1 && data.AsSpan().StartsWith("Exif\0\0"u8)) return "EXIF/TIFF data";
        if (marker == 0xE1 && data.AsSpan().StartsWith("http://ns.adobe.com/xap/1.0/\0"u8)) return TextSafety.Limit(Encoding.UTF8.GetString(data, 29, data.Length - 29), 4096);
        if (marker == 0xFE) return TextSafety.Limit(Encoding.Latin1.GetString(data), 4096);
        return BinaryHelpers.Preview(data);
    }
    private static bool IsSof(int marker) => marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;
    private static string WebPChunkName(string type) => type switch
    {
        "VP8X" => "Extended Header", "VP8 " => "Lossy Image", "VP8L" => "Lossless Image", "ALPH" => "Alpha",
        "ANIM" => "Animation Parameters", "ANMF" => "Animation Frame", "EXIF" => "EXIF", "XMP " => "XMP", "ICCP" => "ICC Profile", _ => "Unknown/Custom Chunk"
    };
}

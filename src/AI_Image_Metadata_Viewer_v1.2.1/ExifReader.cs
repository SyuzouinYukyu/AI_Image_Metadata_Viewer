using System.Text;

namespace AIImageMetadataViewer;

internal sealed class ExifDocument
{
    public required byte[] Bytes { get; init; }
    public required int TiffStart { get; init; }
    public required bool LittleEndian { get; init; }
    public required ExifDirectory Root { get; init; }
}

internal sealed class ExifDirectory
{
    public required string Name { get; init; }
    public List<ExifEntry> Entries { get; } = [];
    public Dictionary<ushort, List<ExifDirectory>> Children { get; } = [];
}

internal sealed class ExifEntry
{
    public required ushort Tag { get; init; }
    public required ushort Type { get; init; }
    public required uint Count { get; init; }
    public required byte[] Data { get; init; }
    public required string Text { get; init; }
    public required string Name { get; init; }
}

internal static class ExifReader
{
    private static readonly Dictionary<ushort, string> TagNames = new()
    {
        [0x010E] = "ImageDescription", [0x010F] = "Make", [0x0110] = "Model",
        [0x0112] = "Orientation", [0x011A] = "XResolution", [0x011B] = "YResolution",
        [0x0128] = "ResolutionUnit", [0x0131] = "Software", [0x0132] = "DateTime",
        [0x013B] = "Artist", [0x014A] = "SubIFDs", [0x8298] = "Copyright",
        [0x829A] = "ExposureTime", [0x829D] = "FNumber", [0x8769] = "ExifIFD",
        [0x8773] = "ICCProfile", [0x8825] = "GPSIFD", [0x8827] = "ISOSpeedRatings",
        [0x9000] = "ExifVersion", [0x9003] = "DateTimeOriginal", [0x9004] = "DateTimeDigitized",
        [0x920A] = "FocalLength", [0x927C] = "MakerNote", [0x9286] = "UserComment",
        [0x9290] = "SubSecTime", [0x9291] = "SubSecTimeOriginal", [0x9292] = "SubSecTimeDigitized",
        [0xA001] = "ColorSpace", [0xA002] = "PixelXDimension", [0xA003] = "PixelYDimension",
        [0xA005] = "InteropIFD", [0xA430] = "CameraOwnerName", [0xA431] = "BodySerialNumber",
        [0xA432] = "LensSpecification", [0xA433] = "LensMake", [0xA434] = "LensModel",
        [0xA435] = "LensSerialNumber", [0x9C9B] = "XPTitle", [0x9C9C] = "XPComment",
        [0x9C9D] = "XPAuthor", [0x9C9E] = "XPKeywords", [0x9C9F] = "XPSubject",
        [0x02BC] = "XMP", [0x83BB] = "IPTC"
    };

    private static readonly int[] TypeSizes = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4 };

    public static ExifDocument Parse(byte[] data)
    {
        var tiff = data.AsSpan().StartsWith("Exif\0\0"u8) ? 6 : 0;
        if (data.Length - tiff < 8) throw new InvalidDataException("EXIF/TIFFヘッダーが短すぎます。");
        var endian = data.AsSpan(tiff, 2);
        var le = endian.SequenceEqual("II"u8);
        if (!le && !endian.SequenceEqual("MM"u8)) throw new InvalidDataException("EXIFバイト順が不正です。");
        if (BinaryHelpers.U16(data.AsSpan(tiff + 2, 2), le) != 42) throw new InvalidDataException("TIFF識別子が不正です。");
        var first = BinaryHelpers.U32(data.AsSpan(tiff + 4, 4), le);
        var visited = new HashSet<uint>();
        var root = ReadDirectory(data, tiff, first, le, "IFD0", 0, visited);
        return new ExifDocument { Bytes = data, TiffStart = tiff, LittleEndian = le, Root = root };
    }

    public static void AddTo(ParsedContainer target, byte[] data, string section)
    {
        try
        {
            var doc = Parse(data);
            AddDirectory(target, doc.Root, section, doc.LittleEndian);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        {
            target.Raw.Add(new RawMetadataItem(section, "EXIF", "破損EXIF", "Error", data.Length,
                TextSafety.Limit(ex.Message, 2000)));
        }
    }

    private static ExifDirectory ReadDirectory(byte[] data, int tiff, uint relativeOffset, bool le,
        string name, int depth, HashSet<uint> visited)
    {
        if (depth > 12) throw new InvalidDataException("EXIF IFDの深度が上限を超えました。");
        if (!visited.Add(relativeOffset)) throw new InvalidDataException("EXIF IFD参照が循環しています。");
        var absolute = checked(tiff + (int)relativeOffset);
        if (absolute < tiff || absolute + 2 > data.Length) throw new InvalidDataException("EXIF IFDオフセットが範囲外です。");
        var count = BinaryHelpers.U16(data.AsSpan(absolute, 2), le);
        if (count > 4096) throw new InvalidDataException("EXIF IFD項目数が上限を超えました。");
        var required = checked(absolute + 2 + count * 12 + 4);
        if (required > data.Length) throw new InvalidDataException("EXIF IFDが途中で切れています。");
        var dir = new ExifDirectory { Name = name };
        for (var i = 0; i < count; i++)
        {
            var p = absolute + 2 + i * 12;
            var tag = BinaryHelpers.U16(data.AsSpan(p, 2), le);
            var type = BinaryHelpers.U16(data.AsSpan(p + 2, 2), le);
            var itemCount = BinaryHelpers.U32(data.AsSpan(p + 4, 4), le);
            var size = GetDataSize(type, itemCount);
            if (size > AppLimits.MaxMetadataEntryBytes) throw new InvalidDataException("EXIF項目が大きすぎます。");
            byte[] value;
            if (size <= 4) value = data.AsSpan(p + 8, size).ToArray();
            else
            {
                var valueOffset = BinaryHelpers.U32(data.AsSpan(p + 8, 4), le);
                var valueAbsolute = checked(tiff + (int)valueOffset);
                if (valueAbsolute < tiff || valueAbsolute + size > data.Length)
                    throw new InvalidDataException($"EXIFタグ0x{tag:X4}の値が範囲外です。");
                value = data.AsSpan(valueAbsolute, size).ToArray();
            }
            var entry = new ExifEntry
            {
                Tag = tag, Type = type, Count = itemCount, Data = value,
                Name = TagNames.GetValueOrDefault(tag, $"Tag 0x{tag:X4}"),
                Text = FormatValue(tag, type, itemCount, value, le)
            };
            dir.Entries.Add(entry);

            if (tag is 0x8769 or 0x8825 or 0xA005 or 0x014A && type is 4 or 13)
            {
                var offsets = ReadUIntValues(value, type, itemCount, le).Take(256).ToArray();
                var label = tag switch { 0x8769 => "ExifIFD", 0x8825 => "GPS", 0xA005 => "Interop", _ => "SubIFD" };
                foreach (var childOffset in offsets.Where(x => x != 0))
                {
                    var child = ReadDirectory(data, tiff, childOffset, le, label, depth + 1, visited);
                    if (!dir.Children.TryGetValue(tag, out var children)) dir.Children[tag] = children = [];
                    children.Add(child);
                }
            }
        }
        var nextOffset = BinaryHelpers.U32(data.AsSpan(absolute + 2 + count * 12, 4), le);
        if (nextOffset != 0)
        {
            var next = ReadDirectory(data, tiff, nextOffset, le, name == "IFD0" ? "IFD1" : $"{name}/Next", depth + 1, visited);
            dir.Children[0xFFFF] = [next];
        }
        return dir;
    }

    private static int GetDataSize(ushort type, uint count)
    {
        if (type >= TypeSizes.Length || TypeSizes[type] == 0) throw new InvalidDataException($"未知のEXIF型: {type}");
        var length = (long)TypeSizes[type] * count;
        if (length > int.MaxValue) throw new InvalidDataException("EXIF値が大きすぎます。");
        return (int)length;
    }

    private static void AddDirectory(ParsedContainer target, ExifDirectory directory, string prefix, bool le)
    {
        foreach (var e in directory.Entries)
        {
            var value = TextSafety.Limit(e.Text, 4096);
            target.Raw.Add(new RawMetadataItem($"{prefix}/{directory.Name}", $"0x{e.Tag:X4}", e.Name,
                $"TIFF type {e.Type} × {e.Count}", e.Data.Length, value));
            if (directory.Name == "IFD0" && e.Tag == 0x0112 && TryFirstUInt(e, le, out var orientation) && orientation is >= 1 and <= 8)
                target.Orientation = (int)orientation;
            else if (directory.Name == "IFD0" && e.Tag == 0x011A && TryRational(e, le, out var x)) target.DpiX = x;
            else if (directory.Name == "IFD0" && e.Tag == 0x011B && TryRational(e, le, out var y)) target.DpiY = y;
            else if (e.Tag == 0xA001 && TryFirstUInt(e, le, out var color))
                target.ColorSpace = color == 1 ? "sRGB" : color == 0xFFFF ? "Uncalibrated" : $"EXIF {color}";

            if (e.Tag is 0x010E or 0x0131 or 0x9286 or 0x9C9C or 0x9C9B or 0x9C9E)
                target.AddText(e.Name, e.Text);
        }
        foreach (var children in directory.Children.Values)
            foreach (var child in children) AddDirectory(target, child, prefix, le);
    }

    private static bool TryFirstUInt(ExifEntry e, bool le, out uint value)
    {
        value = 0;
        if (e.Type == 3 && e.Data.Length >= 2) { value = BinaryHelpers.U16(e.Data, le); return true; }
        if (e.Type is 4 or 13 && e.Data.Length >= 4) { value = BinaryHelpers.U32(e.Data, le); return true; }
        return false;
    }

    private static bool TryRational(ExifEntry e, bool le, out double value)
    {
        value = 0;
        if (e.Type != 5 || e.Data.Length < 8) return false;
        var n = BinaryHelpers.U32(e.Data.AsSpan(0, 4), le);
        var d = BinaryHelpers.U32(e.Data.AsSpan(4, 4), le);
        if (d == 0) return false;
        value = (double)n / d;
        return true;
    }

    private static IEnumerable<uint> ReadUIntValues(byte[] value, ushort type, uint count, bool le)
    {
        var width = type == 3 ? 2 : 4;
        var actual = Math.Min((int)Math.Min(count, int.MaxValue), value.Length / width);
        for (var i = 0; i < actual; i++)
            yield return type == 3 ? BinaryHelpers.U16(value.AsSpan(i * 2, 2), le) : BinaryHelpers.U32(value.AsSpan(i * 4, 4), le);
    }

    private static string FormatValue(ushort tag, ushort type, uint count, byte[] value, bool le)
    {
        if (tag == 0x9286) return DecodeUserComment(value);
        if (tag is >= 0x9C9B and <= 0x9C9F)
            return Encoding.Unicode.GetString(value).TrimEnd('\0');
        if (type == 2) return DecodeAscii(value);
        if (type == 7) return BinaryHelpers.Preview(value);
        if (type is 1 or 6) return string.Join(", ", value.Take(64)) + (value.Length > 64 ? ", …" : string.Empty);
        if (type is 3 or 4 or 9 or 13)
            return string.Join(", ", ReadIntegerStrings(value, type, count, le).Take(64)) + (count > 64 ? ", …" : string.Empty);
        if (type is 5 or 10)
            return string.Join(", ", ReadRationalStrings(value, type, count, le).Take(32)) + (count > 32 ? ", …" : string.Empty);
        return BinaryHelpers.Preview(value);
    }

    private static IEnumerable<string> ReadIntegerStrings(byte[] value, ushort type, uint count, bool le)
    {
        var width = type == 3 ? 2 : 4;
        var actual = Math.Min((int)Math.Min(count, int.MaxValue), value.Length / width);
        for (var i = 0; i < actual; i++)
        {
            var span = value.AsSpan(i * width, width);
            if (type == 9) yield return unchecked((int)BinaryHelpers.U32(span, le)).ToString();
            else yield return (type == 3 ? BinaryHelpers.U16(span, le) : BinaryHelpers.U32(span, le)).ToString();
        }
    }

    private static IEnumerable<string> ReadRationalStrings(byte[] value, ushort type, uint count, bool le)
    {
        var actual = Math.Min((int)Math.Min(count, int.MaxValue), value.Length / 8);
        for (var i = 0; i < actual; i++)
        {
            var span = value.AsSpan(i * 8, 8);
            var uN = BinaryHelpers.U32(span[..4], le);
            var uD = BinaryHelpers.U32(span[4..], le);
            if (type == 10)
                yield return $"{unchecked((int)uN)}/{unchecked((int)uD)}";
            else yield return $"{uN}/{uD}";
        }
    }

    private static string DecodeAscii(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;
        return TextSafety.ReplaceInvalidSurrogates(Encoding.Latin1.GetString(bytes, 0, end));
    }

    private static string DecodeUserComment(byte[] bytes)
    {
        if (bytes.Length < 8) return DecodeAscii(bytes);
        var prefix = Encoding.ASCII.GetString(bytes, 0, 8);
        var body = bytes.AsSpan(8).ToArray();
        try
        {
            if (prefix.StartsWith("ASCII", StringComparison.Ordinal)) return DecodeAscii(body);
            if (prefix.StartsWith("UNICODE", StringComparison.Ordinal))
            {
                if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE) return Encoding.Unicode.GetString(body, 2, body.Length - 2).TrimEnd('\0');
                if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(body, 2, body.Length - 2).TrimEnd('\0');
                return Encoding.BigEndianUnicode.GetString(body).TrimEnd('\0');
            }
            return DecodeAscii(body);
        }
        catch { return BinaryHelpers.Preview(body); }
    }
}

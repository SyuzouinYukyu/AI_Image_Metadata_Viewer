using System.Text;

namespace AIImageMetadataViewer;

internal static class ExifInPlaceSanitizer
{
    private static readonly HashSet<ushort> PrivacyTags =
    [
        0x010E, 0x010F, 0x0110, 0x0131, 0x0132, 0x013B, 0x8298, 0x8825,
        0x9003, 0x9004, 0x927C, 0x9286, 0x9290, 0x9291, 0x9292,
        0xA430, 0xA431, 0xA432, 0xA433, 0xA434, 0xA435,
        0x9C9B, 0x9C9C, 0x9C9D, 0x9C9E, 0x9C9F, 0x02BC, 0x83BB
    ];

    private static readonly HashSet<ushort> DisplayTags =
    [
        0x0100, 0x0101, 0x0102, 0x0103, 0x0106, 0x0111, 0x0112, 0x0115,
        0x0116, 0x0117, 0x011A, 0x011B, 0x011C, 0x0128, 0x0144, 0x0145,
        0x014A, 0x0201, 0x0202, 0x8769, 0x8773, 0xA001, 0xA002, 0xA003, 0xA005
    ];

    private static readonly int[] TypeSizes = { 0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 4 };

    private readonly record struct ByteRange(int Start, int Length)
    {
        public int End => checked(Start + Length);
        public bool Overlaps(ByteRange other) => Start < other.End && other.Start < End;
    }

    private sealed class DirectoryNode
    {
        public required uint RelativeOffset { get; init; }
        public required int AbsoluteOffset { get; init; }
        public required ushort Count { get; init; }
        public uint NextOffset { get; set; }
        public List<EntryRef> Entries { get; } = [];
    }

    private sealed class EntryRef
    {
        public required int Position { get; init; }
        public required ushort Tag { get; init; }
        public required ushort Type { get; init; }
        public required uint Count { get; init; }
        public required ByteRange ValueRange { get; init; }
        public required bool IsInline { get; init; }
        public string Text { get; init; } = string.Empty;
        public List<uint> PointerTargets { get; } = [];
    }

    public static byte[]? Sanitize(byte[] data, RemovalMode mode, AiSource source)
    {
        if (mode == RemovalMode.Complete) return null;
        if (mode == RemovalMode.AiOnly && source == AiSource.None) return data;
        try
        {
            var tiff = data.AsSpan().StartsWith("Exif\0\0"u8) ? 6 : 0;
            if (data.Length - tiff < 8) throw new InvalidDataException("EXIF/TIFFヘッダーが短すぎます。");
            var littleEndian = data[tiff] == (byte)'I' && data[tiff + 1] == (byte)'I';
            if (!littleEndian && !(data[tiff] == (byte)'M' && data[tiff + 1] == (byte)'M'))
                throw new InvalidDataException("EXIFバイト順が不正です。");
            if (BinaryHelpers.U16(data.AsSpan(tiff + 2, 2), littleEndian) != 42)
                throw new InvalidDataException("TIFF識別子が不正です。");

            var first = BinaryHelpers.U32(data.AsSpan(tiff + 4, 4), littleEndian);
            var directories = new Dictionary<uint, DirectoryNode>();
            ReadDirectory(data, tiff, first, littleEndian, 0, directories);
            if (first == 0 || directories.Count == 0) return data;

            var keptEntries = directories.Values.ToDictionary(
                node => node.RelativeOffset,
                node => node.Entries.Where(entry => Keep(entry, mode, source)).ToArray());
            var reachable = new HashSet<uint>();
            MarkReachable(first, directories, keptEntries, reachable);

            foreach (var node in directories.Values.Where(x => reachable.Contains(x.RelativeOffset)))
            {
                foreach (var removedPointer in node.Entries.Where(x => !keptEntries[node.RelativeOffset].Contains(x)))
                    if (removedPointer.PointerTargets.Any(reachable.Contains))
                        throw new InvalidDataException("削除対象IFDが保持対象からも共有参照されているため、安全に物理消去できません。");
            }

            var changed = directories.Values.Any(node =>
                !reachable.Contains(node.RelativeOffset) || keptEntries[node.RelativeOffset].Length != node.Entries.Count);
            if (!changed) return data;

            var protectedRanges = new List<ByteRange> { new(0, checked(tiff + 8)) };
            foreach (var node in directories.Values.Where(x => reachable.Contains(x.RelativeOffset)))
            {
                var kept = keptEntries[node.RelativeOffset];
                protectedRanges.Add(new ByteRange(node.AbsoluteOffset, checked(2 + kept.Length * 12 + 4)));
                foreach (var entry in kept)
                    if (!entry.IsInline) protectedRanges.Add(entry.ValueRange);
                AddReferencedImageDataRanges(data, tiff, node, kept, littleEndian, protectedRanges);
            }

            var wipeRanges = new List<ByteRange>();
            foreach (var node in directories.Values)
            {
                if (!reachable.Contains(node.RelativeOffset))
                {
                    wipeRanges.Add(new ByteRange(node.AbsoluteOffset, checked(2 + node.Count * 12 + 4)));
                    foreach (var entry in node.Entries)
                        if (!entry.IsInline) wipeRanges.Add(entry.ValueRange);
                    AddReferencedImageDataRanges(data, tiff, node, node.Entries.ToArray(), littleEndian, wipeRanges);
                    continue;
                }

                var kept = keptEntries[node.RelativeOffset];
                var newStructureLength = checked(2 + kept.Length * 12 + 4);
                var oldStructureLength = checked(2 + node.Count * 12 + 4);
                if (oldStructureLength > newStructureLength)
                    wipeRanges.Add(new ByteRange(node.AbsoluteOffset + newStructureLength, oldStructureLength - newStructureLength));
                foreach (var removed in node.Entries.Where(x => !kept.Contains(x)))
                    if (!removed.IsInline) wipeRanges.Add(removed.ValueRange);
            }

            foreach (var wipe in wipeRanges.Where(x => x.Length > 0))
            {
                if (wipe.Start < tiff || wipe.End > data.Length)
                    throw new InvalidDataException("物理消去範囲がEXIF領域外です。");
                if (protectedRanges.Any(keep => wipe.Overlaps(keep)))
                    throw new InvalidDataException("削除対象データが保持対象データと領域共有されているため、安全に物理消去できません。");
            }

            var output = (byte[])data.Clone();
            foreach (var node in directories.Values.Where(x => reachable.Contains(x.RelativeOffset)))
            {
                var kept = keptEntries[node.RelativeOffset];
                BinaryHelpers.W16(output.AsSpan(node.AbsoluteOffset, 2), (ushort)kept.Length, littleEndian);
                var destination = node.AbsoluteOffset + 2;
                foreach (var entry in kept)
                {
                    data.AsSpan(entry.Position, 12).CopyTo(output.AsSpan(destination, 12));
                    destination += 12;
                }
                BinaryHelpers.W32(output.AsSpan(destination, 4), node.NextOffset, littleEndian);
            }
            foreach (var wipe in wipeRanges.Where(x => x.Length > 0))
                output.AsSpan(wipe.Start, wipe.Length).Clear();
            return output;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException(
                "EXIFの削除対象を、MakerNote・IFD1・Thumbnail・private tag等を壊さず物理消去できないため処理を拒否しました。", ex);
        }
    }

    private static void MarkReachable(uint offset, IReadOnlyDictionary<uint, DirectoryNode> directories,
        IReadOnlyDictionary<uint, EntryRef[]> keptEntries, HashSet<uint> reachable)
    {
        if (offset == 0 || !reachable.Add(offset)) return;
        if (!directories.TryGetValue(offset, out var node))
            throw new InvalidDataException("参照先IFDがありません。");
        foreach (var entry in keptEntries[offset])
            foreach (var target in entry.PointerTargets)
                MarkReachable(target, directories, keptEntries, reachable);
        if (node.NextOffset != 0) MarkReachable(node.NextOffset, directories, keptEntries, reachable);
    }

    private static bool Keep(EntryRef entry, RemovalMode mode, AiSource source) => mode switch
    {
        RemovalMode.AiOnly => !IsAiEntry(entry, source),
        RemovalMode.Privacy => !PrivacyTags.Contains(entry.Tag),
        RemovalMode.PreserveDisplayOnly => DisplayTags.Contains(entry.Tag),
        _ => true
    };

    private static bool IsAiEntry(EntryRef entry, AiSource source)
    {
        if (entry.Tag is 0x9286 or 0x9C9C &&
            (LooksAiText(entry.Text) || source is AiSource.Automatic1111 or AiSource.Automatic1111Compatible))
            return true;
        return source == AiSource.NovelAI && entry.Tag is 0x010E or 0x0131 or 0x9286 or 0x9C9C;
    }

    private static bool LooksAiText(string value) =>
        value.Contains("Steps:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("NovelAI", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("\"class_type\"", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("stable-diffusion", StringComparison.OrdinalIgnoreCase);

    private static DirectoryNode? ReadDirectory(byte[] data, int tiff, uint relativeOffset, bool littleEndian,
        int depth, Dictionary<uint, DirectoryNode> directories)
    {
        if (relativeOffset == 0) return null;
        if (directories.TryGetValue(relativeOffset, out var existing)) return existing;
        if (depth > 24) throw new InvalidDataException("EXIF IFDの深度が上限を超えました。");
        var absolute = checked(tiff + (int)relativeOffset);
        if (absolute < tiff || absolute + 2 > data.Length)
            throw new InvalidDataException("EXIF IFDオフセットが範囲外です。");
        var count = BinaryHelpers.U16(data.AsSpan(absolute, 2), littleEndian);
        if (count > 4096) throw new InvalidDataException("EXIF IFD項目数が上限を超えました。");
        var end = checked(absolute + 2 + count * 12 + 4);
        if (end > data.Length) throw new InvalidDataException("EXIF IFDが途中で切れています。");
        var node = new DirectoryNode { RelativeOffset = relativeOffset, AbsoluteOffset = absolute, Count = count };
        directories.Add(relativeOffset, node);
        for (var index = 0; index < count; index++)
        {
            var position = absolute + 2 + index * 12;
            var tag = BinaryHelpers.U16(data.AsSpan(position, 2), littleEndian);
            var type = BinaryHelpers.U16(data.AsSpan(position + 2, 2), littleEndian);
            var itemCount = BinaryHelpers.U32(data.AsSpan(position + 4, 4), littleEndian);
            var valueRange = GetValueRange(data, tiff, position, type, itemCount, littleEndian, out var inline);
            var text = tag is 0x010E or 0x0131 or 0x9286 or 0x9C9C
                ? ReadText(data, valueRange, tag, type)
                : string.Empty;
            var entry = new EntryRef
            {
                Position = position, Tag = tag, Type = type, Count = itemCount,
                ValueRange = valueRange, IsInline = inline, Text = text
            };
            node.Entries.Add(entry);
            if (tag is 0x8769 or 0x8825 or 0xA005 or 0x014A && type is 4 or 13)
            {
                foreach (var child in ReadUnsignedValues(data, valueRange, type, itemCount, littleEndian, 256))
                {
                    if (child == 0) continue;
                    entry.PointerTargets.Add(child);
                    ReadDirectory(data, tiff, child, littleEndian, depth + 1, directories);
                }
            }
        }
        node.NextOffset = BinaryHelpers.U32(data.AsSpan(absolute + 2 + count * 12, 4), littleEndian);
        if (node.NextOffset != 0) ReadDirectory(data, tiff, node.NextOffset, littleEndian, depth + 1, directories);
        return node;
    }

    private static ByteRange GetValueRange(byte[] data, int tiff, int position, ushort type,
        uint count, bool littleEndian, out bool inline)
    {
        if (type >= TypeSizes.Length || TypeSizes[type] == 0)
            throw new InvalidDataException($"未知のEXIF型: {type}");
        var longSize = checked((long)TypeSizes[type] * count);
        if (longSize > AppLimits.MaxMetadataEntryBytes || longSize > int.MaxValue)
            throw new InvalidDataException("EXIF項目が大きすぎます。");
        var size = (int)longSize;
        inline = size <= 4;
        var absolute = inline
            ? position + 8
            : checked(tiff + (int)BinaryHelpers.U32(data.AsSpan(position + 8, 4), littleEndian));
        if (absolute < tiff || absolute + size > data.Length)
            throw new InvalidDataException("EXIF値オフセットが範囲外です。");
        return new ByteRange(absolute, size);
    }

    private static IReadOnlyList<uint> ReadUnsignedValues(byte[] data, ByteRange range, ushort type,
        uint count, bool littleEndian, int maximum)
    {
        var result = new List<uint>();
        var actual = Math.Min((int)Math.Min(count, (uint)maximum), type == 3 ? range.Length / 2 : range.Length / 4);
        for (var index = 0; index < actual; index++)
        {
            var value = type == 3
                ? BinaryHelpers.U16(data.AsSpan(range.Start + index * 2, 2), littleEndian)
                : BinaryHelpers.U32(data.AsSpan(range.Start + index * 4, 4), littleEndian);
            result.Add(value);
        }
        return result;
    }

    private static void AddReferencedImageDataRanges(byte[] data, int tiff, DirectoryNode node,
        EntryRef[] entries, bool littleEndian, List<ByteRange> ranges)
    {
        AddOffsetLengthPair(0x0201, 0x0202);
        AddOffsetLengthPair(0x0111, 0x0117);
        AddOffsetLengthPair(0x0144, 0x0145);
        return;

        void AddOffsetLengthPair(ushort offsetTag, ushort lengthTag)
        {
            var offsets = entries.FirstOrDefault(x => x.Tag == offsetTag);
            var lengths = entries.FirstOrDefault(x => x.Tag == lengthTag);
            if (offsets is null || lengths is null) return;
            var offsetValues = ReadUnsignedValues(data, offsets.ValueRange, offsets.Type, offsets.Count, littleEndian, 4096);
            var lengthValues = ReadUnsignedValues(data, lengths.ValueRange, lengths.Type, lengths.Count, littleEndian, 4096);
            if (offsetValues.Count != lengthValues.Count)
                throw new InvalidDataException($"IFD 0x{node.RelativeOffset:X} の画像offset/count数が一致しません。");
            for (var index = 0; index < offsetValues.Count; index++)
            {
                if (lengthValues[index] == 0) continue;
                var start = checked(tiff + (int)offsetValues[index]);
                var length = checked((int)lengthValues[index]);
                if (start < tiff || start + length > data.Length)
                    throw new InvalidDataException("Thumbnail/Strip/Tile領域がEXIF範囲外です。");
                ranges.Add(new ByteRange(start, length));
            }
        }
    }

    private static string ReadText(byte[] data, ByteRange range, ushort tag, ushort type)
    {
        var value = data.AsSpan(range.Start, range.Length).ToArray();
        if (tag == 0x9286) return DecodeUserComment(value);
        if (tag == 0x9C9C) return Encoding.Unicode.GetString(value).TrimEnd('\0');
        if (type == 2) return DecodeAscii(value);
        return BinaryHelpers.Preview(value, 4096);
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
                if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
                    return Encoding.Unicode.GetString(body, 2, body.Length - 2).TrimEnd('\0');
                if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
                    return Encoding.BigEndianUnicode.GetString(body, 2, body.Length - 2).TrimEnd('\0');
                return Encoding.BigEndianUnicode.GetString(body).TrimEnd('\0');
            }
            return DecodeAscii(body);
        }
        catch
        {
            return BinaryHelpers.Preview(body);
        }
    }
}

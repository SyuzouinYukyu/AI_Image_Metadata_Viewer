using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AIImageMetadataViewer;

internal static class BinaryHelpers
{
    public static ushort U16(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);

    public static uint U32(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);

    public static ulong U64(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(data) : BinaryPrimitives.ReadUInt64BigEndian(data);

    public static void W16(Span<byte> data, ushort value, bool littleEndian)
    {
        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(data, value);
        else BinaryPrimitives.WriteUInt16BigEndian(data, value);
    }

    public static void W32(Span<byte> data, uint value, bool littleEndian)
    {
        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        else BinaryPrimitives.WriteUInt32BigEndian(data, value);
    }

    public static async Task<byte[]> ReadBytesAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        if (length < 0) throw new InvalidDataException("負のデータ長です。");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return data;
    }

    public static void Skip(Stream stream, long count)
    {
        if (count < 0 || count > stream.Length - stream.Position)
            throw new InvalidDataException("メタデータ長がファイル範囲外です。");
        stream.Seek(count, SeekOrigin.Current);
    }

    public static string Preview(ReadOnlySpan<byte> data, int maxBytes = 256)
    {
        var bytes = data[..Math.Min(data.Length, maxBytes)];
        var printable = bytes.Count(x => x is 9 or 10 or 13 || x is >= 32 and <= 126);
        if (bytes.Length > 0 && printable >= bytes.Length * 3 / 4)
        {
            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            return TextSafety.Limit(text, 1024) + (data.Length > maxBytes ? " …" : string.Empty);
        }
        return Convert.ToHexString(bytes) + (data.Length > maxBytes ? "…" : string.Empty);
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    public static string Mime(ImageContainerFormat format) => format switch
    {
        ImageContainerFormat.Png => "image/png",
        ImageContainerFormat.Jpeg => "image/jpeg",
        ImageContainerFormat.WebP => "image/webp",
        ImageContainerFormat.Tiff => "image/tiff",
        ImageContainerFormat.Bmp => "image/bmp",
        ImageContainerFormat.Gif => "image/gif",
        ImageContainerFormat.Avif => "image/avif",
        ImageContainerFormat.Heif => "image/heif",
        ImageContainerFormat.Jxl => "image/jxl",
        _ => "application/octet-stream"
    };

    public static ImageContainerFormat DetectFormat(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 8 && h[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ImageContainerFormat.Png;
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ImageContainerFormat.Jpeg;
        if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("WEBP"u8)) return ImageContainerFormat.WebP;
        if (h.Length >= 4 && ((h[0] == (byte)'I' && h[1] == (byte)'I' && h[2] == 42 && h[3] == 0) ||
                            (h[0] == (byte)'M' && h[1] == (byte)'M' && h[2] == 0 && h[3] == 42))) return ImageContainerFormat.Tiff;
        if (h.Length >= 2 && h[..2].SequenceEqual("BM"u8)) return ImageContainerFormat.Bmp;
        if (h.Length >= 6 && (h[..6].SequenceEqual("GIF87a"u8) || h[..6].SequenceEqual("GIF89a"u8))) return ImageContainerFormat.Gif;
        if (h.Length >= 12 && h.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(h.Slice(8, 4));
            if (brand is "avif" or "avis") return ImageContainerFormat.Avif;
            if (brand is "heic" or "heix" or "hevc" or "hevx" or "mif1" or "msf1") return ImageContainerFormat.Heif;
        }
        if (h.Length >= 2 && h[0] == 0xFF && h[1] == 0x0A) return ImageContainerFormat.Jxl;
        if (h.Length >= 12 && h[..12].SequenceEqual(new byte[] { 0, 0, 0, 12, 0x4A, 0x58, 0x4C, 0x20, 13, 10, 0x87, 10 })) return ImageContainerFormat.Jxl;
        return ImageContainerFormat.Unknown;
    }
}

using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;

namespace AIImageMetadataViewer;

public sealed record RemovalResult(string OutputPath, IReadOnlyList<RemovalPlanItem> Plan, string Verification);

internal static class MetadataRemovalService
{
    internal static Func<string, CancellationToken, Task>? BeforeFinalCommitForTests { get; set; }
    private static readonly HashSet<string> PrivacyTextKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Author", "Copyright", "Comment", "Description", "Software", "Creation Time", "CreationTime",
        "XML:com.adobe.xmp", "Raw profile type xmp", "Artist", "Owner", "Camera", "GPS", "DateTime"
    };
    private static readonly HashSet<string> AiTextKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "parameters", "prompt", "workflow", "negative_prompt"
    };

    public static IReadOnlyList<RemovalPlanItem> CreatePlan(AnalysisResult analysis, RemovalMode mode)
    {
        var list = new List<RemovalPlanItem>();
        foreach (var item in analysis.RawMetadata)
        {
            var remove = ShouldRemoveRaw(item, mode, analysis.Ai.Source);
            list.Add(new RemovalPlanItem(item.Section, $"{item.Identifier} / {item.Name}", remove ? "削除" : "保護",
                item.IsImagePayload ? "画像本体" : remove ? RemovalReason(mode) : ProtectionReason(item, mode)));
        }
        return list;
    }

    public static async Task<RemovalResult> ExecuteAsync(string sourcePath, AnalysisResult analysis,
        RemovalMode mode, bool overwriteSource, CancellationToken ct)
    {
        var currentSnapshot = await FileSnapshot.CaptureAsync(sourcePath, ct);
        if (!analysis.Snapshot.Matches(currentSnapshot)) throw new SourceChangedException();
        if (analysis.BasicInfo.Format is not (ImageContainerFormat.Png or ImageContainerFormat.Jpeg or ImageContainerFormat.WebP))
            throw new NotSupportedException("この形式では画像本体を維持した無劣化削除を保証できないため処理しません。PNG/JPEG/WebPのみ対応します。");
        var plan = CreatePlan(analysis, mode);
        var directory = Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("出力先フォルダーを取得できません。");
        var temp = Path.Combine(directory, $".{Path.GetFileName(sourcePath)}.{Guid.NewGuid():N}.tmp");
        var final = overwriteSource ? sourcePath : UniqueOutputPath(sourcePath);
        try
        {
            await Task.Run(() => Rewrite(sourcePath, temp, analysis.BasicInfo.Format, mode, analysis.Ai.Source, ct), ct);
            var verification = await VerifyAsync(sourcePath, temp, analysis.BasicInfo, mode, analysis.Ai.Source, ct);
            if (overwriteSource)
            {
                var beforeFinalCommit = BeforeFinalCommitForTests;
                if (beforeFinalCommit is not null) await beforeFinalCommit(sourcePath, ct);
                ct.ThrowIfCancellationRequested();
                var finalSnapshot = await FileSnapshot.CaptureAsync(sourcePath, ct);
                if (!analysis.Snapshot.Matches(finalSnapshot)) throw new SourceChangedException();
                File.Replace(temp, sourcePath, null, true);
            }
            else File.Move(temp, final);
            return new RemovalResult(final, plan, verification);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static void Rewrite(string source, string output, ImageContainerFormat format, RemovalMode mode, AiSource aiSource, CancellationToken ct)
    {
        switch (format)
        {
            case ImageContainerFormat.Png: RewritePng(source, output, mode, aiSource, ct); break;
            case ImageContainerFormat.Jpeg: RewriteJpeg(source, output, mode, aiSource, ct); break;
            case ImageContainerFormat.WebP: RewriteWebP(source, output, mode, aiSource, ct); break;
            default: throw new NotSupportedException();
        }
    }

    private static void RewritePng(string source, string output, RemovalMode mode, AiSource aiSource, CancellationToken ct)
    {
        using var input = OpenRead(source);
        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var signature = new byte[8]; input.ReadExactly(signature); target.Write(signature);
        while (input.Position < input.Length)
        {
            ct.ThrowIfCancellationRequested();
            var header = new byte[8]; input.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var type = Encoding.ASCII.GetString(header[4..]);
            if (length > int.MaxValue || length > input.Length - input.Position - 4) throw new InvalidDataException($"PNG {type}長が不正です。");
            byte[]? data = null;
            var needData = type is "tEXt" or "zTXt" or "iTXt" or "eXIf" || length <= 1024;
            if (needData && length <= AppLimits.MaxMetadataEntryBytes) { data = new byte[(int)length]; input.ReadExactly(data); }
            else BinaryHelpers.Skip(input, length);
            var oldCrc = new byte[4]; input.ReadExactly(oldCrc);

            var keep = true;
            byte[]? replacement = data;
            if (type is "tEXt" or "zTXt" or "iTXt")
            {
                if (data is null) throw new InvalidDataException($"{type}が安全な処理上限を超えています。");
                var key = ReadPngTextKey(data);
                keep = !ShouldRemoveText(key, TryReadPngTextValue(type, data), mode, aiSource);
            }
            else if (type == "eXIf")
            {
                if (data is null) throw new InvalidDataException("eXIfが安全な処理上限を超えています。");
                replacement = ExifInPlaceSanitizer.Sanitize(data, mode, aiSource);
                keep = replacement is not null;
            }
            else keep = KeepPngChunk(type, mode);

            if (keep)
            {
                if (data is null)
                {
                    target.Write(header);
                    input.Position -= length + 4; // payload先頭へ戻す
                    CopyExactly(input, target, length, ct);
                    input.Position += 4; // CRCは読み取り済みの位置へ
                    target.Write(oldCrc);
                }
                else if (ReferenceEquals(replacement, data))
                {
                    target.Write(header); target.Write(data); target.Write(oldCrc);
                }
                else WritePngChunk(target, type, replacement ?? []);
            }
            if (type == "IEND") break;
        }
        target.Flush(true);
    }

    private static void RewriteJpeg(string source, string output, RemovalMode mode, AiSource aiSource, CancellationToken ct)
    {
        using var input = OpenRead(source);
        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var soi = new byte[2]; input.ReadExactly(soi);
        if (soi[0] != 0xFF || soi[1] != 0xD8) throw new InvalidDataException("JPEG SOIがありません。");
        target.Write(soi);
        while (input.Position < input.Length)
        {
            ct.ThrowIfCancellationRequested();
            var prefix = input.ReadByte();
            if (prefix != 0xFF) throw new InvalidDataException("JPEGマーカー同期が失われました。");
            var marker = input.ReadByte();
            while (marker == 0xFF) marker = input.ReadByte();
            if (marker < 0) throw new EndOfStreamException();
            if (marker == 0xD9) { target.WriteByte(0xFF); target.WriteByte(0xD9); break; }
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) { target.WriteByte(0xFF); target.WriteByte((byte)marker); continue; }
            var len = new byte[2]; input.ReadExactly(len);
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(len);
            if (segmentLength < 2 || segmentLength - 2 > input.Length - input.Position) throw new InvalidDataException("JPEGセグメント長が不正です。");
            if (marker == 0xDA)
            {
                target.WriteByte(0xFF); target.WriteByte((byte)marker); target.Write(len);
                CopyExactly(input, target, input.Length - input.Position, ct);
                break;
            }
            var data = new byte[segmentLength - 2]; input.ReadExactly(data);
            var replacement = ProcessJpegSegment(marker, data, mode, aiSource);
            if (replacement is null) continue;
            if (replacement.Length > ushort.MaxValue - 2) throw new InvalidDataException("書き換え後JPEGセグメントが上限を超えます。");
            target.WriteByte(0xFF); target.WriteByte((byte)marker);
            var newLen = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(newLen, (ushort)(replacement.Length + 2));
            target.Write(newLen); target.Write(replacement);
        }
        target.Flush(true);
    }

    private static byte[]? ProcessJpegSegment(int marker, byte[] data, RemovalMode mode, AiSource aiSource)
    {
        if (marker == 0xE1 && data.AsSpan().StartsWith("Exif\0\0"u8)) return ExifInPlaceSanitizer.Sanitize(data, mode, aiSource);
        if (marker == 0xE1 && data.AsSpan().StartsWith("http://ns.adobe.com/xap/1.0/\0"u8))
        {
            var text = Encoding.UTF8.GetString(data);
            return mode is RemovalMode.Privacy or RemovalMode.PreserveDisplayOnly or RemovalMode.Complete || mode == RemovalMode.AiOnly && LooksAi(text) ? null : data;
        }
        if (marker == 0xFE)
        {
            var text = Encoding.Latin1.GetString(data);
            return mode is RemovalMode.Privacy or RemovalMode.PreserveDisplayOnly or RemovalMode.Complete || mode == RemovalMode.AiOnly && LooksAi(text) ? null : data;
        }
        if (marker == 0xED && mode is RemovalMode.Privacy or RemovalMode.PreserveDisplayOnly or RemovalMode.Complete) return null;
        if (marker is >= 0xE0 and <= 0xEF)
        {
            if (mode == RemovalMode.Complete) return null;
            if (mode == RemovalMode.PreserveDisplayOnly && marker is not (0xE0 or 0xE2 or 0xEE)) return null;
        }
        return data;
    }

    private static void RewriteWebP(string source, string output, RemovalMode mode, AiSource aiSource, CancellationToken ct)
    {
        var transforms = InspectWebP(source, mode, aiSource, ct);
        var hasIcc = mode != RemovalMode.Complete && WebPHasChunk(source, "ICCP");
        var hasExif = transforms.Values.Any(x => x.Type == "EXIF" && x.Data is not null);
        var hasXmp = transforms.Values.Any(x => x.Type == "XMP " && x.Data is not null) ||
                     !transforms.Values.Any(x => x.Type == "XMP ") && WebPHasChunk(source, "XMP ");
        using var input = OpenRead(source);
        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Span<byte> riff = stackalloc byte[12]; input.ReadExactly(riff); target.Write(riff);
        while (input.Position + 8 <= input.Length)
        {
            ct.ThrowIfCancellationRequested();
            var chunkStart = input.Position;
            var header = new byte[8]; input.ReadExactly(header);
            var type = Encoding.ASCII.GetString(header[..4]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (length > int.MaxValue || length > input.Length - input.Position) throw new InvalidDataException("WebPチャンク長が不正です。");
            var padded = length + (length & 1);
            if (transforms.TryGetValue(chunkStart, out var transform))
            {
                BinaryHelpers.Skip(input, padded);
                if (transform.Data is not null) WriteWebPChunk(target, type, transform.Data);
                continue;
            }
            if (type == "VP8X")
            {
                var data = new byte[length]; input.ReadExactly(data);
                if ((length & 1) != 0) input.ReadByte();
                if (data.Length >= 1)
                {
                    data[0] = (byte)(data[0] & ~(0x20 | 0x08 | 0x04));
                    if (hasIcc) data[0] |= 0x20;
                    if (hasExif) data[0] |= 0x08;
                    if (hasXmp) data[0] |= 0x04;
                }
                WriteWebPChunk(target, type, data);
            }
            else
            {
                target.Write(header); CopyExactly(input, target, padded, ct);
            }
        }
        var riffSize = target.Length - 8;
        if (riffSize > uint.MaxValue) throw new InvalidDataException("出力WebPがRIFFサイズ上限を超えます。");
        target.Position = 4; Span<byte> sizeBytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)riffSize); target.Write(sizeBytes);
        target.Flush(true);
    }

    private sealed record WebPTransform(string Type, byte[]? Data);

    private static bool WebPHasChunk(string source, string wanted)
    {
        using var input = OpenRead(source); input.Position = 12;
        while (input.Position + 8 <= input.Length)
        {
            var header = new byte[8]; input.ReadExactly(header);
            var type = Encoding.ASCII.GetString(header, 0, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (length > input.Length - input.Position) return false;
            if (type == wanted) return true;
            BinaryHelpers.Skip(input, length + (length & 1));
        }
        return false;
    }

    private static Dictionary<long, WebPTransform> InspectWebP(string source, RemovalMode mode, AiSource aiSource, CancellationToken ct)
    {
        var map = new Dictionary<long, WebPTransform>();
        using var input = OpenRead(source); input.Position = 12;
        while (input.Position + 8 <= input.Length)
        {
            ct.ThrowIfCancellationRequested();
            var start = input.Position;
            var h = new byte[8]; input.ReadExactly(h);
            var type = Encoding.ASCII.GetString(h[..4]); var length = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
            if (length > int.MaxValue || length > input.Length - input.Position) throw new InvalidDataException("WebPチャンク長が不正です。");
            var remove = type switch
            {
                "XMP " => mode is RemovalMode.Privacy or RemovalMode.PreserveDisplayOnly or RemovalMode.Complete,
                "ICCP" => mode == RemovalMode.Complete,
                _ => false
            };
            if (type == "EXIF")
            {
                if (length > AppLimits.MaxMetadataEntryBytes)
                {
                    if (mode == RemovalMode.Complete) { map[start] = new(type, null); BinaryHelpers.Skip(input, length + (length & 1)); continue; }
                    throw new InvalidDataException("WebP EXIFが安全な処理上限を超えています。");
                }
                var data = new byte[length]; input.ReadExactly(data); if ((length & 1) != 0) input.ReadByte();
                map[start] = new(type, ExifInPlaceSanitizer.Sanitize(data, mode, aiSource)); continue;
            }
            if (type == "XMP " && mode == RemovalMode.AiOnly && length <= AppLimits.MaxMetadataEntryBytes)
            {
                var data = new byte[length]; input.ReadExactly(data); if ((length & 1) != 0) input.ReadByte();
                map[start] = new(type, LooksAi(Encoding.UTF8.GetString(data)) ? null : data); continue;
            }
            if (remove) map[start] = new(type, null);
            BinaryHelpers.Skip(input, length + (length & 1));
        }
        return map;
    }

    private static async Task<string> VerifyAsync(string source, string candidate, ImageBasicInfo original,
        RemovalMode mode, AiSource sourceType, CancellationToken ct)
    {
        var beforePayload = await Task.Run(() => HashContainerParts(source, original.Format, true, ct), ct);
        var afterPayload = await Task.Run(() => HashContainerParts(candidate, original.Format, true, ct), ct);
        if (!beforePayload.SequenceEqual(afterPayload))
            throw new InvalidDataException("検証失敗: 圧縮画像ペイロードが一致しません。");
        if (mode != RemovalMode.Complete)
        {
            var beforeIcc = await Task.Run(() => HashContainerParts(source, original.Format, false, ct), ct);
            var afterIcc = await Task.Run(() => HashContainerParts(candidate, original.Format, false, ct), ct);
            if (!beforeIcc.SequenceEqual(afterIcc))
                throw new InvalidDataException("検証失敗: ICCプロファイルが一致しません。");
        }
        var parsed = await Task.Run(() => ContainerMetadataReader.Read(candidate, ct), ct);
        if (parsed.Format != original.Format) throw new InvalidDataException("検証失敗: 形式が変化しました。");
        if (parsed.Width != original.Width || parsed.Height != original.Height) throw new InvalidDataException("検証失敗: Width/Heightが変化しました。");
        var beforeOrientation = mode == RemovalMode.Complete ? 1 : original.Orientation;
        var afterOrientation = mode == RemovalMode.Complete ? 1 : parsed.Orientation;
        var before = await Task.Run(() => ImageDecoder.Decode(source, beforeOrientation, ct), ct);
        var after = await Task.Run(() => ImageDecoder.Decode(candidate, afterOrientation, ct), ct);
        using (before.Bitmap) using (after.Bitmap)
        {
            if (before.Bitmap is null || after.Bitmap is null) throw new InvalidDataException("検証失敗: 画像を再オープンできません。");
            var beforeHash = HashRgba(before.Bitmap);
            var afterHash = HashRgba(after.Bitmap);
            if (!beforeHash.SequenceEqual(afterHash)) throw new InvalidDataException("検証失敗: Orientation適用後のRGBA表示結果が一致しません。");
        }
        var ai = AiMetadataParser.Parse(parsed);
        if (mode == RemovalMode.AiOnly && ai.Source != AiSource.None)
            throw new InvalidDataException($"検証失敗: 生成AI情報が残っています（{ai.SourceLabel}）。");
        return mode == RemovalMode.Complete
            ? "再オープン、形式、Width/Height、圧縮画像ペイロード、画像本体RGBA SHA-256、対象メタデータを検証済み（Orientation/ICCは削除対象）"
            : "再オープン、形式、Width/Height、圧縮画像ペイロード、ICC、Orientation適用後RGBA SHA-256、対象メタデータを検証済み";
    }

    private static byte[] HashContainerParts(string path, ImageContainerFormat format, bool imagePayload, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = OpenRead(path);
        switch (format)
        {
            case ImageContainerFormat.Png:
                HashPngParts(stream, hash, imagePayload, ct);
                break;
            case ImageContainerFormat.Jpeg:
                HashJpegParts(stream, hash, imagePayload, ct);
                break;
            case ImageContainerFormat.WebP:
                HashWebPParts(stream, hash, imagePayload, ct);
                break;
            default:
                throw new NotSupportedException();
        }
        return hash.GetHashAndReset();
    }

    private static void HashPngParts(Stream stream, IncrementalHash hash, bool imagePayload, CancellationToken ct)
    {
        BinaryHelpers.Skip(stream, 8);
        while (stream.Position + 12 <= stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            var header = new byte[8];
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            if (length > stream.Length - stream.Position - 4) throw new InvalidDataException("PNGチャンク長が不正です。");
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var selected = imagePayload ? type is "IDAT" or "fdAT" : type == "iCCP";
            if (selected)
            {
                hash.AppendData(header.AsSpan(4, 4));
                AppendExactly(stream, hash, length, ct);
            }
            else BinaryHelpers.Skip(stream, length);
            BinaryHelpers.Skip(stream, 4);
            if (type == "IEND") break;
        }
    }

    private static void HashJpegParts(Stream stream, IncrementalHash hash, bool imagePayload, CancellationToken ct)
    {
        Span<byte> soi = stackalloc byte[2];
        stream.ReadExactly(soi);
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (stream.ReadByte() != 0xFF) throw new InvalidDataException("JPEGマーカー同期が失われました。");
            var marker = stream.ReadByte();
            while (marker == 0xFF) marker = stream.ReadByte();
            if (marker < 0) throw new EndOfStreamException();
            if (marker == 0xD9) break;
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue;
            var lengthBytes = new byte[2];
            stream.ReadExactly(lengthBytes);
            var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (length < 2 || length - 2 > stream.Length - stream.Position) throw new InvalidDataException("JPEGセグメント長が不正です。");
            if (marker == 0xDA)
            {
                if (imagePayload)
                {
                    hash.AppendData([0xFF, (byte)marker]);
                    hash.AppendData(lengthBytes);
                    AppendExactly(stream, hash, stream.Length - stream.Position, ct);
                }
                return;
            }
            var data = new byte[length - 2];
            stream.ReadExactly(data);
            if (!imagePayload && marker == 0xE2 && data.AsSpan().StartsWith("ICC_PROFILE\0"u8))
                hash.AppendData(data);
        }
    }

    private static void HashWebPParts(Stream stream, IncrementalHash hash, bool imagePayload, CancellationToken ct)
    {
        BinaryHelpers.Skip(stream, 12);
        while (stream.Position + 8 <= stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            var header = new byte[8];
            stream.ReadExactly(header);
            var type = Encoding.ASCII.GetString(header, 0, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            var padded = length + (length & 1);
            if (padded > stream.Length - stream.Position) throw new InvalidDataException("WebPチャンク長が不正です。");
            var selected = imagePayload ? type is "VP8 " or "VP8L" or "ANIM" or "ANMF" : type == "ICCP";
            if (selected)
            {
                hash.AppendData(header);
                AppendExactly(stream, hash, padded, ct);
            }
            else BinaryHelpers.Skip(stream, padded);
        }
    }

    private static void AppendExactly(Stream stream, IncrementalHash hash, long count, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];
        while (count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            count -= read;
        }
    }

    private static byte[] HashRgba(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        using var normalized = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(normalized)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImageUnscaled(bitmap, 0, 0); }
        var bits = normalized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var sha = SHA256.Create();
            var row = new byte[bitmap.Width * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length);
                sha.TransformBlock(row, 0, row.Length, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0); return sha.Hash!;
        }
        finally { normalized.UnlockBits(bits); }
    }

    private static bool ShouldRemoveRaw(RawMetadataItem item, RemovalMode mode, AiSource aiSource)
    {
        if (item.IsImagePayload || item.Identifier is "IHDR" or "IEND" or "PLTE" or "tRNS" or "acTL" or "fcTL" or "DIB" or "LSD") return false;
        if (mode == RemovalMode.Complete) return item.Section is not "解析";
        if (mode == RemovalMode.PreserveDisplayOnly)
            return item.Identifier is not ("iCCP" or "sRGB" or "gAMA" or "cHRM" or "pHYs" or "APP0" or "FFE0" or "APP2" or "FFE2" or "APP14" or "FFEE");
        if (mode == RemovalMode.Privacy)
            return item.Name.Contains("GPS", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("XMP", StringComparison.OrdinalIgnoreCase) ||
                   item.Name.Contains("IPTC", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("Comment", StringComparison.OrdinalIgnoreCase) ||
                   item.Name.Contains("Software", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("DateTime", StringComparison.OrdinalIgnoreCase) ||
                   item.Name is "Make" or "Model" or "Artist" or "Copyright" or "MakerNote" or "CameraOwnerName" or "BodySerialNumber" or "LensSerialNumber";
        return AiTextKeys.Contains(item.Identifier) || item.Identifier is "tEXt" or "zTXt" or "iTXt" &&
               AiTextKeys.Any(key => item.Value.StartsWith(key, StringComparison.OrdinalIgnoreCase)) || aiSource != AiSource.None &&
               (item.Name is "UserComment" or "XPComment" || aiSource == AiSource.NovelAI && item.Name is "ImageDescription" or "Software");
    }

    private static bool ShouldRemoveText(string key, string value, RemovalMode mode, AiSource aiSource) => mode switch
    {
        RemovalMode.Complete => true,
        RemovalMode.PreserveDisplayOnly => true,
        RemovalMode.Privacy => PrivacyTextKeys.Contains(key),
        RemovalMode.AiOnly => AiTextKeys.Contains(key) || aiSource == AiSource.NovelAI && key is "Software" or "Description" or "Comment" || LooksAi(value),
        _ => false
    };

    private static bool KeepPngChunk(string type, RemovalMode mode)
    {
        if (char.IsUpper(type[0]) || type is "tRNS" or "acTL" or "fcTL" or "fdAT") return true;
        return mode switch
        {
            RemovalMode.Complete => false,
            RemovalMode.PreserveDisplayOnly => type is "iCCP" or "sRGB" or "gAMA" or "cHRM" or "pHYs",
            RemovalMode.Privacy => type is not "tIME",
            _ => true
        };
    }

    private static string ReadPngTextKey(byte[] data)
    {
        var p = Array.IndexOf(data, (byte)0);
        if (p is < 1 or > 79) throw new InvalidDataException("PNGテキストキーワードが不正です。");
        return Encoding.Latin1.GetString(data, 0, p);
    }

    private static string TryReadPngTextValue(string type, byte[] data)
    {
        try
        {
            var p = Array.IndexOf(data, (byte)0);
            if (type == "tEXt") return Encoding.Latin1.GetString(data, p + 1, data.Length - p - 1);
            return BinaryHelpers.Preview(data, 1024);
        }
        catch { return string.Empty; }
    }

    private static bool LooksAi(string value) => value.Contains("Steps:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("ComfyUI", StringComparison.OrdinalIgnoreCase) || value.Contains("NovelAI", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("\"class_type\"", StringComparison.OrdinalIgnoreCase) || value.Contains("stable-diffusion", StringComparison.OrdinalIgnoreCase);

    private static string RemovalReason(RemovalMode mode) => mode switch
    {
        RemovalMode.AiOnly => "生成AI情報", RemovalMode.Privacy => "プライバシー情報",
        RemovalMode.PreserveDisplayOnly => "表示維持に不要なメタデータ", _ => "完全削除モード"
    };
    private static string ProtectionReason(RawMetadataItem item, RemovalMode mode) => item.IsImagePayload ? "画像本体" :
        mode == RemovalMode.Complete ? "コンテナ成立に必要" : "表示結果・色・向き、またはモード対象外";

    private static string UniqueOutputPath(string source)
    {
        var directory = Path.GetDirectoryName(source)!; var stem = Path.GetFileNameWithoutExtension(source); var ext = Path.GetExtension(source);
        var candidate = Path.Combine(directory, $"{stem}_metadata_removed{ext}");
        for (var i = 1; File.Exists(candidate); i++) candidate = Path.Combine(directory, $"{stem}_metadata_removed_{i:000}{ext}");
        return candidate;
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
    private static void CopyExactly(Stream input, Stream output, long count, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];
        while (count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException();
            output.Write(buffer, 0, read); count -= read;
        }
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length); output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type); output.Write(typeBytes); output.Write(data);
        var crc = Crc32(typeBytes, data); Span<byte> crcBytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc); output.Write(crcBytes);
    }

    private static void WriteWebPChunk(Stream output, string type, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes(type)); Span<byte> len = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)data.Length); output.Write(len); output.Write(data);
        if ((data.Length & 1) != 0) output.WriteByte(0);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data)) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1; }
        return crc ^ 0xFFFFFFFFu;
    }
}

internal static class ExifSanitizer
{
    private static readonly HashSet<ushort> PrivacyTags =
    [
        0x010E, 0x010F, 0x0110, 0x0131, 0x0132, 0x013B, 0x8298, 0x8825,
        0x9003, 0x9004, 0x927C, 0x9286, 0x9290, 0x9291, 0x9292,
        0xA430, 0xA431, 0xA432, 0xA433, 0xA434, 0xA435,
        0x9C9B, 0x9C9C, 0x9C9D, 0x9C9E, 0x9C9F, 0x02BC, 0x83BB
    ];
    private static readonly HashSet<ushort> DisplayTags = [0x0112, 0x011A, 0x011B, 0x0128, 0xA001, 0x8773];

    public static byte[]? Sanitize(byte[] data, RemovalMode mode, AiSource source)
    {
        if (mode == RemovalMode.Complete) return null;
        ExifDocument doc;
        try { doc = ExifReader.Parse(data); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        {
            if (mode is RemovalMode.Privacy or RemovalMode.PreserveDisplayOnly) throw new InvalidDataException("破損EXIFを安全に選別できません。完全削除モードのみ使用できます。", ex);
            return data;
        }
        var root = Filter(doc.Root, mode, source);
        if (!HasData(root)) return null;
        return Build(doc, root, data.AsSpan().StartsWith("Exif\0\0"u8));
    }

    private static ExifDirectory Filter(ExifDirectory sourceDir, RemovalMode mode, AiSource source)
    {
        var result = new ExifDirectory { Name = sourceDir.Name };
        foreach (var e in sourceDir.Entries)
        {
            var pointer = e.Tag is 0x8769 or 0x8825 or 0xA005 or 0x014A;
            if (pointer)
            {
                if (mode == RemovalMode.Privacy && e.Tag == 0x8825) continue;
                if (sourceDir.Children.TryGetValue(e.Tag, out var children))
                {
                    var filtered = children.Select(x => Filter(x, mode, source)).Where(HasData).ToList();
                    if (filtered.Count > 0) result.Children[e.Tag] = filtered;
                }
                continue;
            }
            var remove = mode switch
            {
                RemovalMode.PreserveDisplayOnly => !DisplayTags.Contains(e.Tag),
                RemovalMode.Privacy => PrivacyTags.Contains(e.Tag),
                RemovalMode.AiOnly => IsAiEntry(e, source),
                _ => false
            };
            if (!remove) result.Entries.Add(e);
        }
        return result;
    }

    private static bool IsAiEntry(ExifEntry e, AiSource source)
    {
        if (e.Tag is 0x9286 or 0x9C9C && (LooksAiText(e.Text) || source is AiSource.Automatic1111 or AiSource.Automatic1111Compatible)) return true;
        return source == AiSource.NovelAI && e.Tag is 0x010E or 0x0131 or 0x9286 or 0x9C9C;
    }

    private static bool LooksAiText(string value) => value.Contains("Steps:", StringComparison.OrdinalIgnoreCase) || value.Contains("NovelAI", StringComparison.OrdinalIgnoreCase) || value.Contains("\"class_type\"", StringComparison.OrdinalIgnoreCase);
    private static bool HasData(ExifDirectory d) => d.Entries.Count > 0 || d.Children.Values.Any(x => x.Any(HasData));

    private sealed class BuildDirectory
    {
        public required ExifDirectory Source { get; init; }
        public List<BuildEntry> Entries { get; } = [];
        public uint Offset { get; set; }
    }
    private sealed class BuildEntry
    {
        public required ushort Tag { get; init; }
        public required ushort Type { get; init; }
        public required uint Count { get; init; }
        public required byte[] Data { get; init; }
        public uint DataOffset { get; set; }
    }

    private static byte[] Build(ExifDocument original, ExifDirectory filtered, bool prefix)
    {
        var all = new List<BuildDirectory>();
        var pointerTargets = new Dictionary<BuildEntry, List<BuildDirectory>>();
        BuildDirectory Make(ExifDirectory dir)
        {
            var build = new BuildDirectory { Source = dir }; all.Add(build);
            foreach (var e in dir.Entries.OrderBy(x => x.Tag)) build.Entries.Add(new BuildEntry { Tag = e.Tag, Type = e.Type, Count = e.Count, Data = e.Data });
            foreach (var pair in dir.Children.OrderBy(x => x.Key))
            {
                var childBuilds = pair.Value.Select(Make).ToList();
                var pointerData = new byte[childBuilds.Count * 4];
                var pointer = new BuildEntry { Tag = pair.Key, Type = 4, Count = (uint)childBuilds.Count, Data = pointerData };
                build.Entries.Add(pointer);
                pointerTargets[pointer] = childBuilds;
            }
            build.Entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            return build;
        }
        var root = Make(filtered);
        uint cursor = 8;
        foreach (var d in all) { d.Offset = cursor; cursor = checked(cursor + (uint)(2 + d.Entries.Count * 12 + 4)); }
        foreach (var pointer in pointerTargets)
            for (var i = 0; i < pointer.Value.Count; i++) BinaryHelpers.W32(pointer.Key.Data.AsSpan(i * 4, 4), pointer.Value[i].Offset, original.LittleEndian);
        foreach (var d in all)
            foreach (var e in d.Entries)
                if (e.Data.Length > 4) { if ((cursor & 1) != 0) cursor++; e.DataOffset = cursor; cursor = checked(cursor + (uint)e.Data.Length); }
        if (cursor > AppLimits.MaxMetadataEntryBytes) throw new InvalidDataException("再構築EXIFが上限を超えます。");
        var tiff = new byte[cursor];
        tiff[0] = tiff[1] = original.LittleEndian ? (byte)'I' : (byte)'M';
        BinaryHelpers.W16(tiff.AsSpan(2, 2), 42, original.LittleEndian); BinaryHelpers.W32(tiff.AsSpan(4, 4), root.Offset, original.LittleEndian);
        foreach (var d in all)
        {
            var pos = (int)d.Offset; BinaryHelpers.W16(tiff.AsSpan(pos, 2), (ushort)d.Entries.Count, original.LittleEndian); pos += 2;
            foreach (var e in d.Entries)
            {
                BinaryHelpers.W16(tiff.AsSpan(pos, 2), e.Tag, original.LittleEndian); BinaryHelpers.W16(tiff.AsSpan(pos + 2, 2), e.Type, original.LittleEndian);
                BinaryHelpers.W32(tiff.AsSpan(pos + 4, 4), e.Count, original.LittleEndian);
                if (e.Data.Length <= 4) e.Data.CopyTo(tiff, pos + 8);
                else { BinaryHelpers.W32(tiff.AsSpan(pos + 8, 4), e.DataOffset, original.LittleEndian); e.Data.CopyTo(tiff, (int)e.DataOffset); }
                pos += 12;
            }
            BinaryHelpers.W32(tiff.AsSpan(pos, 4), 0, original.LittleEndian);
        }
        if (!prefix) return tiff;
        var output = new byte[tiff.Length + 6]; "Exif\0\0"u8.CopyTo(output); tiff.CopyTo(output, 6); return output;
    }

}

using System.Drawing.Imaging;
using SkiaSharp;

namespace AIImageMetadataViewer;

internal sealed class DecodeResult
{
    public Bitmap? Bitmap { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitDepth { get; init; }
    public string PixelFormat { get; init; } = string.Empty;
    public bool HasAlpha { get; init; }
    public int FrameCount { get; init; } = 1;
    public string Warning { get; init; } = string.Empty;
}

internal static class ImageAnalysisService
{
    public static async Task<AnalysisResult> AnalyzeAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = await FileSnapshot.CaptureAsync(path, cancellationToken);
            var result = await AnalyzeOnceAsync(path, before, cancellationToken);
            var after = await FileSnapshot.CaptureAsync(path, cancellationToken);
            if (before.Matches(after)) return result;
            result.Dispose();
        }
        throw new IOException("ファイルが解析中に変更され続けたため、安定した状態を取得できません。");
    }

    private static async Task<AnalysisResult> AnalyzeOnceAsync(string path, FileSnapshot snapshot, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("ファイルが見つかりません。", path);
        ParsedContainer container;
        string parseError = string.Empty;
        try { container = await Task.Run(() => ContainerMetadataReader.Read(path, cancellationToken), cancellationToken); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        {
            container = new ParsedContainer { Format = await DetectOnlyAsync(path, cancellationToken) };
            parseError = $"メタデータ解析: {ex.Message}";
            container.Raw.Add(new RawMetadataItem("解析", "error", "解析エラー", "Error", info.Length, parseError));
        }

        var decodeTask = Task.Run(() => ImageDecoder.Decode(path, container.Orientation, cancellationToken), cancellationToken);
        DecodeResult decoded;
        try { decoded = await decodeTask; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or OutOfMemoryException or ExternalException)
        {
            decoded = new DecodeResult { Warning = $"画像表示: {ex.Message}" };
        }

        var width = container.Width > 0 ? container.Width : decoded.Width;
        var height = container.Height > 0 ? container.Height : decoded.Height;
        var basic = new ImageBasicInfo
        {
            FileName = info.Name,
            FullPath = info.FullName,
            Format = container.Format,
            Mime = BinaryHelpers.Mime(container.Format),
            FileSize = info.Length,
            Width = width,
            Height = height,
            AspectRatio = Aspect(width, height),
            BitDepth = container.BitDepth > 0 ? container.BitDepth.ToString() : decoded.BitDepth > 0 ? decoded.BitDepth.ToString() : "—",
            PixelFormat = string.IsNullOrEmpty(decoded.PixelFormat) ? "—" : decoded.PixelFormat,
            ColorSpace = string.IsNullOrEmpty(container.ColorSpace) ? "未指定" : container.ColorSpace,
            Alpha = container.HasAlpha || decoded.HasAlpha ? "あり" : "なし/未検出",
            Dpi = container.DpiX > 0 ? $"{container.DpiX:0.##} × {container.DpiY:0.##}" : "未指定",
            FrameCount = Math.Max(container.FrameCount, decoded.FrameCount),
            Orientation = container.Orientation,
            CreatedAt = info.CreationTime,
            ModifiedAt = info.LastWriteTime,
            Sha256 = snapshot.Sha256,
            DecodeWarning = string.Join(" / ", new[] { parseError, decoded.Warning }.Where(x => !string.IsNullOrWhiteSpace(x)))
        };
        return new AnalysisResult
        {
            BasicInfo = basic,
            Ai = AiMetadataParser.Parse(container),
            RawMetadata = container.Raw,
            Snapshot = snapshot,
            Bitmap = decoded.Bitmap,
            Error = basic.DecodeWarning
        };
    }

    private static async Task<ImageContainerFormat> DetectOnlyAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64, true);
        var header = new byte[(int)Math.Min(64, fs.Length)];
        await fs.ReadExactlyAsync(header, ct);
        return BinaryHelpers.DetectFormat(header);
    }

    private static string Aspect(int w, int h)
    {
        if (w <= 0 || h <= 0) return "—";
        var a = w; var b = h;
        while (b != 0) (a, b) = (b, a % b);
        return $"{w / a}:{h / a} ({(double)w / h:0.####})";
    }
}

internal static class ImageDecoder
{
    public static DecodeResult Decode(string path, int orientation, CancellationToken ct)
    {
        Exception? skiaError = null;
        try { return DecodeSkia(path, orientation, ct); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OutOfMemoryException) { skiaError = ex; }
        try { return DecodeGdi(path, orientation, ct); }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException or OutOfMemoryException)
        {
            throw new InvalidDataException($"デコードできません。Skia: {skiaError?.Message}; Windows: {ex.Message}");
        }
    }

    private static DecodeResult DecodeSkia(string path, int orientation, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var stream = new SKManagedStream(file, false);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("SkiaSharpが画像形式を認識できません。");
        var source = codec.Info;
        var pixels = checked((long)source.Width * source.Height);
        if (source.Width <= 0 || source.Height <= 0 || pixels > AppLimits.MaxDecodedPixels)
            throw new InvalidDataException($"画像サイズが安全上の上限（{AppLimits.MaxDecodedPixels:N0}画素）を超えています。");
        ct.ThrowIfCancellationRequested();
        var targetInfo = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var skBitmap = new SKBitmap(targetInfo);
        var status = codec.GetPixels(targetInfo, skBitmap.GetPixels());
        if (status is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            throw new InvalidDataException($"SkiaSharpデコードエラー: {status}");
        var bitmap = CopyBitmap(skBitmap);
        ApplyOrientation(bitmap, orientation);
        return new DecodeResult
        {
            Bitmap = bitmap, Width = source.Width, Height = source.Height,
            BitDepth = source.BitsPerPixel, PixelFormat = $"{source.ColorType} / {source.AlphaType}",
            HasAlpha = source.AlphaType != SKAlphaType.Opaque, FrameCount = Math.Max(1, codec.FrameCount),
            Warning = status == SKCodecResult.IncompleteInput ? "画像データが途中で切れています。表示は部分的です。" : string.Empty
        };
    }

    private static DecodeResult DecodeGdi(string path, int orientation, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var image = Image.FromStream(file, true, true);
        var pixels = checked((long)image.Width * image.Height);
        if (pixels > AppLimits.MaxDecodedPixels) throw new InvalidDataException("画像がデコード画素数上限を超えています。");
        ct.ThrowIfCancellationRequested();
        var frameCount = image.FrameDimensionsList.Length == 0 ? 1 : image.GetFrameCount(new FrameDimension(image.FrameDimensionsList[0]));
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(SafeDpi(image.HorizontalResolution), SafeDpi(image.VerticalResolution));
        using (var g = Graphics.FromImage(bitmap)) g.DrawImageUnscaled(image, 0, 0);
        ApplyOrientation(bitmap, orientation);
        return new DecodeResult
        {
            Bitmap = bitmap, Width = image.Width, Height = image.Height, BitDepth = Image.GetPixelFormatSize(image.PixelFormat),
            PixelFormat = image.PixelFormat.ToString(), HasAlpha = Image.IsAlphaPixelFormat(image.PixelFormat), FrameCount = frameCount
        };
    }

    private static float SafeDpi(float dpi) => float.IsFinite(dpi) && dpi is >= 1 and <= 9600 ? dpi : 96;

    private static unsafe Bitmap CopyBitmap(SKBitmap source)
    {
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var target = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var rowBytes = Math.Min(source.RowBytes, Math.Abs(target.Stride));
            var src = (byte*)source.GetPixels();
            var dst = (byte*)target.Scan0;
            for (var y = 0; y < source.Height; y++)
                Buffer.MemoryCopy(src + y * source.RowBytes, dst + y * target.Stride, Math.Abs(target.Stride), rowBytes);
        }
        finally { bitmap.UnlockBits(target); }
        return bitmap;
    }

    internal static void ApplyOrientation(Bitmap bitmap, int orientation)
    {
        var transform = orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX, 5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone
        };
        if (transform != RotateFlipType.RotateNoneFlipNone) bitmap.RotateFlip(transform);
    }
}

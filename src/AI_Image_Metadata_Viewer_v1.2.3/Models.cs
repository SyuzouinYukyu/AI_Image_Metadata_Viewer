using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace AIImageMetadataViewer;

public enum ImageContainerFormat
{
    Unknown, Png, Jpeg, WebP, Tiff, Bmp, Gif, Avif, Heif, Jxl
}

public enum AiSource
{
    None, Automatic1111, Automatic1111Compatible, ComfyUI, NovelAI, Other, Unknown
}

public enum RemovalMode
{
    AiOnly,
    Privacy,
    PreserveDisplayOnly,
    Complete
}

public sealed record RawMetadataItem(
    string Section,
    string Identifier,
    string Name,
    string Kind,
    long Size,
    string Value,
    bool IsImagePayload = false);

public sealed record MetadataField(string Group, string Key, string Value);

public sealed record RemovalPlanItem(
    string Section,
    string Name,
    string Action,
    string Reason);

public sealed record FileSnapshot(long Length, long LastWriteTimeUtcTicks, string Sha256)
{
    public bool Matches(FileSnapshot other) =>
        Length == other.Length &&
        LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks &&
        Sha256.Equals(other.Sha256, StringComparison.OrdinalIgnoreCase);

    public static async Task<FileSnapshot> CaptureAsync(string path, CancellationToken cancellationToken)
    {
        var before = new FileInfo(path);
        if (!before.Exists) throw new FileNotFoundException("ファイルが見つかりません。", path);
        var length = before.Length;
        var ticks = before.LastWriteTimeUtc.Ticks;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            hash.AppendData(buffer, 0, read);
        var digest = Convert.ToHexString(hash.GetHashAndReset());
        var after = new FileInfo(path);
        if (!after.Exists || after.Length != length || after.LastWriteTimeUtc.Ticks != ticks)
            throw new IOException("ファイルが読み取り中に変更されました。");
        return new FileSnapshot(length, ticks, digest);
    }
}

public sealed class SourceChangedException : IOException
{
    public SourceChangedException() : base("解析後に元ファイルが変更されました。最新状態を再解析してから、内容を確認してもう一度実行してください。") { }
}

public sealed class AiMetadata
{
    private readonly List<MetadataField> _fields = [];

    public AiSource Source { get; set; }
    public string SourceLabel => Source switch
    {
        AiSource.Automatic1111 => "AUTOMATIC1111",
        AiSource.Automatic1111Compatible => "AUTOMATIC1111互換",
        AiSource.ComfyUI => "ComfyUI",
        AiSource.NovelAI => "NovelAI",
        AiSource.Other => "その他生成AI",
        AiSource.Unknown => "形式不明",
        _ => "生成AI情報なし"
    };

    public string PositivePrompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public string RawPromptJson { get; set; } = string.Empty;
    public string RawWorkflowJson { get; set; } = string.Empty;
    public string WorkflowSummary { get; set; } = string.Empty;
    public IReadOnlyList<MetadataField> Fields => new ReadOnlyCollection<MetadataField>(_fields);

    public void Add(string group, string key, object? value)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _fields.Add(new MetadataField(group, key, TextSafety.Limit(text, AppLimits.MaxDisplayedValueChars)));
    }

    public IEnumerable<MetadataField> Find(string key) =>
        _fields.Where(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

public sealed class ImageBasicInfo
{
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public ImageContainerFormat Format { get; set; }
    public string Mime { get; set; } = "application/octet-stream";
    public long FileSize { get; init; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long PixelCount => (long)Width * Height;
    public string AspectRatio { get; set; } = "—";
    public string BitDepth { get; set; } = "—";
    public string PixelFormat { get; set; } = "—";
    public string ColorSpace { get; set; } = "—";
    public string Alpha { get; set; } = "—";
    public string Dpi { get; set; } = "—";
    public int FrameCount { get; set; } = 1;
    public int Orientation { get; set; } = 1;
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; init; }
    public string Sha256 { get; set; } = string.Empty;
    public string DecodeWarning { get; set; } = string.Empty;
}

public sealed class AnalysisResult : IDisposable
{
    public required ImageBasicInfo BasicInfo { get; init; }
    public required AiMetadata Ai { get; init; }
    public required IReadOnlyList<RawMetadataItem> RawMetadata { get; init; }
    public required FileSnapshot Snapshot { get; init; }
    public Bitmap? Bitmap { get; set; }
    public string Error { get; set; } = string.Empty;

    public void Dispose() => Bitmap?.Dispose();
}

public sealed class ParsedContainer
{
    public ImageContainerFormat Format { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitDepth { get; set; }
    public bool HasAlpha { get; set; }
    public int FrameCount { get; set; } = 1;
    public double DpiX { get; set; }
    public double DpiY { get; set; }
    public int Orientation { get; set; } = 1;
    public string ColorSpace { get; set; } = string.Empty;
    public List<RawMetadataItem> Raw { get; } = [];
    public Dictionary<string, List<string>> Text { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddText(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) return;
        if (!Text.TryGetValue(key, out var values)) Text[key] = values = [];
        values.Add(TextSafety.Limit(value, AppLimits.MaxMetadataTextChars));
    }
}

public static class AppLimits
{
    public const int MaxMetadataEntryBytes = 16 * 1024 * 1024;
    public const int MaxMetadataTotalBytes = 64 * 1024 * 1024;
    public const int MaxMetadataTextChars = 8 * 1024 * 1024;
    public const int MaxDisplayedValueChars = 1_000_000;
    public const int MaxJsonDepth = 128;
    public const int MaxJsonNodes = 100_000;
    public const long MaxDecodedPixels = 120_000_000;
}

public static class TextSafety
{
    public static string Limit(string value, int maxChars)
    {
        if (value.Length <= maxChars) return ReplaceInvalidSurrogates(value);
        return ReplaceInvalidSurrogates(value[..maxChars]) + $"\n…（{value.Length - maxChars:N0}文字省略）";
    }

    public static string ReplaceInvalidSurrogates(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]))
            {
                if (i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1])) { i++; continue; }
                chars[i] = '\uFFFD';
            }
            else if (char.IsLowSurrogate(chars[i])) chars[i] = '\uFFFD';
        }
        return new string(chars);
    }
}

using System.Text.Json;

namespace AIImageMetadataViewer;

internal sealed class AppSettings
{
    public float FontSize { get; set; } = 15;
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public int WindowWidth { get; set; } = 1400;
    public int WindowHeight { get; set; } = 900;
    public bool Maximized { get; set; }
    public int MainSplitter { get; set; } = 820;
    public int LeftSplitter { get; set; } = 620;
    public int PromptSplitter { get; set; } = 360;
    public bool IncludeSubfolders { get; set; }
    public int LastTab { get; set; }
    public RemovalMode RemovalMode { get; set; } = RemovalMode.AiOnly;
    public bool OverwriteSource { get; set; }
}

internal static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    internal static string? TestSettingsPathOverride { get; set; }

    public static string SettingsPath =>
        TestSettingsPathOverride ?? Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings();
            Validate(settings);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static bool TrySave(AppSettings settings, out string error)
    {
        var temp = string.Empty;
        try
        {
            Validate(settings);
            var directory = Path.GetDirectoryName(SettingsPath)
                ?? throw new IOException("設定ファイルの保存先を取得できません。");
            temp = Path.Combine(directory, $".settings.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, SettingsPath, true);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { if (temp.Length > 0 && File.Exists(temp)) File.Delete(temp); } catch { }
            error = $"設定を保存できませんでした。\n保存先: {SettingsPath}\n\n{ex.Message}";
            return false;
        }
    }

    internal static void Validate(AppSettings settings)
    {
        if (!float.IsFinite(settings.FontSize) || settings.FontSize is < 8 or > 32)
            settings.FontSize = 15;
        settings.WindowWidth = Math.Clamp(settings.WindowWidth, 720, 10000);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight, 520, 10000);
        settings.MainSplitter = Math.Clamp(settings.MainSplitter, 200, 9000);
        settings.LeftSplitter = Math.Clamp(settings.LeftSplitter, 150, 9000);
        settings.PromptSplitter = Math.Clamp(settings.PromptSplitter, 120, 9000);
        settings.LastTab = Math.Clamp(settings.LastTab, 0, 6);
        if (!Enum.IsDefined(settings.RemovalMode)) settings.RemovalMode = RemovalMode.AiOnly;
    }
}

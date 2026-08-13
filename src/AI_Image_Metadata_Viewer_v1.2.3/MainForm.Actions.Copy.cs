using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AIImageMetadataViewer;

internal sealed partial class MainForm
{
    private void CopyOverview(object? feedbackTarget = null)
    {
        if (_overviewFields.Count == 0) return;
        CopyText(string.Join(Environment.NewLine, _overviewFields.Select(x => $"{x.Key}: {x.Value}")), feedbackTarget);
    }

    private void CopyPrimaryGenerationInfo(object? feedbackTarget = null)
    {
        if (_current is null) return;
        CopyText(BuildPrimaryGenerationInfo(_current), feedbackTarget);
    }

    internal static string BuildPrimaryGenerationInfo(AnalysisResult analysis)
    {
        var b = analysis.BasicInfo;
        var lines = new List<string>
        {
            "=== 画像情報 ===",
            $"ファイル名: {b.FileName}",
            $"形式: {b.Format}",
            $"MIME: {b.Mime}",
            $"容量: {b.FileSize:N0} bytes ({FormatBytes(b.FileSize)})",
            $"幅: {b.Width:N0}",
            $"高さ: {b.Height:N0}",
            $"総画素数: {b.PixelCount:N0}",
            $"Aspect Ratio: {b.AspectRatio}",
            $"Bit Depth: {b.BitDepth}",
            $"Pixel Format: {b.PixelFormat}",
            $"Color Space: {b.ColorSpace}",
            $"Alpha: {b.Alpha}",
            $"DPI: {b.Dpi}",
            $"Frame Count: {b.FrameCount}",
            $"Orientation: {b.Orientation}",
            $"生成元: {analysis.Ai.SourceLabel}",
            string.Empty,
            "=== ポジティブプロンプト ===",
            string.IsNullOrEmpty(analysis.Ai.PositivePrompt) ? "（なし）" : analysis.Ai.PositivePrompt,
            string.Empty,
            "=== ネガティブプロンプト ===",
            string.IsNullOrEmpty(analysis.Ai.NegativePrompt) ? "（なし）" : analysis.Ai.NegativePrompt,
            string.Empty,
            "=== 生成設定 ==="
        };
        AppendFieldsOrNone(lines, analysis.Ai.Fields.Where(IsPrimaryGenerationSettingsField));
        lines.Add(string.Empty);
        lines.Add("=== Model / LoRA ===");
        AppendFieldsOrNone(lines, analysis.Ai.Fields.Where(IsModelField));
        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsModelField(MetadataField field) =>
        field.Group.StartsWith("Model", StringComparison.OrdinalIgnoreCase);

    private static bool IsGenerationSettingsField(MetadataField field) =>
        field.Group != "Prompt" && !IsModelField(field);

    private static bool IsPrimaryGenerationSettingsField(MetadataField field) =>
        IsGenerationSettingsField(field) &&
        !field.Key.Equals("Positive Prompt", StringComparison.OrdinalIgnoreCase) &&
        !field.Key.Equals("Negative Prompt", StringComparison.OrdinalIgnoreCase);

    private static void AppendFieldsOrNone(List<string> lines, IEnumerable<MetadataField> fields)
    {
        var count = 0;
        foreach (var field in fields)
        {
            lines.Add($"{field.Key}: {field.Value}");
            count++;
        }
        if (count == 0) lines.Add("（なし）");
    }

    private void SaveJson(string json, string defaultName)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        using var dialog = new SaveFileDialog { Filter = "JSON|*.json|すべてのファイル|*.*", FileName = defaultName, AddExtension = true, DefaultExt = "json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, json, new UTF8Encoding(false)); _stateLabel.Text = "JSONを保存しました"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(ex.Message, "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ClearCurrentUi()
    {
        _canvas.SetImage(null); _overviewFields.Clear(); _overviewGrid.Rows.Clear(); _settingsGrid.Rows.Clear(); _modelGrid.Rows.Clear(); _rawGrid.Rows.Clear(); _removalGrid.Rows.Clear();
        _positiveText.Clear(); _negativeText.Clear(); _promptJsonText.Clear(); _workflowJsonText.Clear(); _workflowTree.Nodes.Clear(); _executeRemovalButton.Enabled = false;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"]; double n = bytes; var i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.O)) { OpenFiles(); return true; }
        if (keyData == Keys.Left) { Navigate(-1); return true; }
        if (keyData == Keys.Right) { Navigate(1); return true; }
        if (keyData == (Keys.Control | Keys.D0)) { _canvas.Fit(); return true; }
        if (keyData == (Keys.Control | Keys.D1)) { _canvas.ActualSize(); return true; }
        if (keyData is (Keys.Control | Keys.Add) or (Keys.Control | Keys.Oemplus)) { _canvas.ZoomBy(1.2f); return true; }
        if (keyData is (Keys.Control | Keys.Subtract) or (Keys.Control | Keys.OemMinus)) { _canvas.ZoomBy(1 / 1.2f); return true; }
        if (keyData == Keys.F5) { _ = LoadSelectedAsync(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AIImageMetadataViewer;

internal sealed partial class MainForm
{
    private static string ShortJson(JsonElement e)
    {
        var value = e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText();
        return value.Length <= 300 ? value : value[..300] + "…";
    }

    private void RefreshRemovalPlan()
    {
        _removalGrid.Rows.Clear(); _executeRemovalButton.Enabled = false;
        if (_current is null || _removalMode.SelectedIndex < 0) return;
        var mode = (RemovalMode)_removalMode.SelectedIndex;
        foreach (var item in MetadataRemovalService.CreatePlan(_current, mode))
        {
            var index = _removalGrid.Rows.Add(item.Section, item.Name, item.Action, item.Reason);
            if (item.Action == "削除") _removalGrid.Rows[index].DefaultCellStyle.BackColor = Color.MistyRose;
            else _removalGrid.Rows[index].DefaultCellStyle.BackColor = Color.Honeydew;
        }
        OptimizeGridColumns(_removalGrid);
        _executeRemovalButton.Enabled = _current.BasicInfo.Format is ImageContainerFormat.Png or ImageContainerFormat.Jpeg or ImageContainerFormat.WebP;
    }

    private async Task ExecuteRemovalAsync()
    {
        if (_current is null || _fileList.SelectedItem is not FileListItem selected) return;
        var mode = (RemovalMode)_removalMode.SelectedIndex;
        var overwrite = _overwriteCheck.Checked;
        var warning = mode == RemovalMode.Complete
            ? "完全削除ではOrientation・ICC等も削除対象になり、表示色や向きが変化する可能性があります。\n\n"
            : string.Empty;
        if (overwrite) warning += "元ファイルを安全な一時ファイル処理後に置換します。元へ反映すると取り消せません。\n\n";
        var message = warning + "一覧の内容でメタデータ削除を実行しますか？";
        if (MessageBox.Show(message, "メタデータ削除の確認", MessageBoxButtons.YesNo, mode == RemovalMode.Complete || overwrite ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _executeRemovalButton.Enabled = false; _stateLabel.Text = "無劣化削除・再検証中…"; UseWaitCursor = true;
        try
        {
            var result = await MetadataRemovalService.ExecuteAsync(selected.Path, _current, mode, overwrite, CancellationToken.None);
            _stateLabel.Text = "削除完了・検証済み";
            MessageBox.Show($"完了しました。\n\n出力: {result.OutputPath}\n{result.Verification}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (overwrite) await LoadSelectedAsync(); else ReplaceQueueWithPaths([result.OutputPath]);
        }
        catch (SourceChangedException ex)
        {
            _stateLabel.Text = "元ファイル変更を検出・再解析中…";
            await LoadSelectedAsync();
            MessageBox.Show(ex.Message, "再確認が必要です", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ExternalException)
        {
            _stateLabel.Text = $"削除失敗（原本維持）: {ex.Message}";
            MessageBox.Show($"処理に失敗しました。原本は変更されていません。\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; RefreshRemovalPlan(); }
    }

    private void ApplySearch()
    {
        var query = _searchBox.Text.Trim();
        foreach (var grid in _searchableGrids)
        {
            grid.CurrentCell = null;
            foreach (DataGridViewRow row in grid.Rows)
                row.Visible = query.Length == 0 || row.Cells.Cast<DataGridViewCell>().Any(c => c is not DataGridViewButtonCell && (c.Value?.ToString()?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false));
            OptimizeGridColumns(grid);
        }
    }

    private void Navigate(int delta)
    {
        if (_fileList.Items.Count == 0) return;
        _fileList.SelectedIndex = Math.Clamp((_fileList.SelectedIndex < 0 ? 0 : _fileList.SelectedIndex) + delta, 0, _fileList.Items.Count - 1);
    }

    private void OpenInExplorer()
    {
        if (_fileList.SelectedItem is not FileListItem selected) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selected.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { _stateLabel.Text = $"Explorerを開けません: {ex.Message}"; }
    }

    private void CopyCurrent(bool fullPath, object? feedbackTarget)
    {
        if (_fileList.SelectedItem is FileListItem selected)
            CopyText(fullPath ? selected.Path : Path.GetFileName(selected.Path), feedbackTarget);
    }

    private bool CopyText(string text, object? feedbackTarget = null)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            Clipboard.SetText(text);
            if (feedbackTarget is not null) _copyFeedback.ShowSuccess(feedbackTarget);
            return true;
        }
        catch (ExternalException)
        {
            MessageBox.Show("クリップボードが他のアプリで使用中です。", "コピー", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
    }

    private void CopyRawAll(object? feedbackTarget = null)
    {
        if (_current is null) return;
        CopyText(string.Join(Environment.NewLine, _current.RawMetadata.Select(x => $"[{x.Section}] {x.Identifier} {x.Name} ({x.Kind}, {x.Size:N0} bytes) = {x.Value}")), feedbackTarget);
    }

}

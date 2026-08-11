using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AIImageMetadataViewer;

internal sealed partial class MainForm
{
    private void RestoreSettings()
    {
        var requested = new Rectangle(_settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight);
        var visible = _settings.WindowX != int.MinValue && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(requested));
        if (visible)
        {
            StartPosition = FormStartPosition.Manual; Bounds = requested;
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen; Size = new Size(_settings.WindowWidth, _settings.WindowHeight);
        }
        _tabs.SelectedIndex = Math.Clamp(_settings.LastTab, 0, _tabs.TabCount - 1);
        Shown += (_, _) =>
        {
            RestoreSplitter(_mainSplit, _settings.MainSplitter);
            RestoreSplitter(_leftSplit, _settings.LeftSplitter);
            RestoreSplitter(_promptSplit, _settings.PromptSplitter);
            if (_settings.Maximized) WindowState = FormWindowState.Maximized;
        };
    }

    private void SaveSettings()
    {
        if (_settingsSaved) return;
        _settingsSaved = true;
        _analysisCts?.Cancel();
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WindowX = bounds.X; _settings.WindowY = bounds.Y; _settings.WindowWidth = bounds.Width; _settings.WindowHeight = bounds.Height;
        _settings.Maximized = WindowState == FormWindowState.Maximized;
        _settings.MainSplitter = _mainSplit.SplitterDistance;
        _settings.LeftSplitter = _leftSplit.SplitterDistance;
        _settings.PromptSplitter = _promptSplit.SplitterDistance;
        _settings.IncludeSubfolders = _recursiveButton.Checked; _settings.LastTab = _tabs.SelectedIndex;
        _settings.RemovalMode = (RemovalMode)Math.Max(0, _removalMode.SelectedIndex); _settings.OverwriteSource = _overwriteCheck.Checked;
        if (!SettingsService.TrySave(_settings, out var error))
            MessageBox.Show(error, "設定保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static void RestoreSplitter(SplitContainer split, int requested)
    {
        var available = (split.Orientation == Orientation.Vertical ? split.ClientSize.Width : split.ClientSize.Height)
            - split.SplitterWidth;
        var maximum = available - split.Panel2MinSize;
        if (maximum < split.Panel1MinSize) return;
        split.SplitterDistance = Math.Clamp(requested, split.Panel1MinSize, maximum);
    }

    private void OpenFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "画像を開く", Multiselect = true, CheckFileExists = true,
            Filter = "画像|*.png;*.jpg;*.jpeg;*.webp;*.tif;*.tiff;*.bmp;*.gif;*.avif;*.heic;*.heif;*.jxl|すべてのファイル|*.*"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) ReplaceQueueWithPaths(dialog.FileNames);
    }

    private void RegisterDropTargets(Control root)
    {
        try
        {
            root.AllowDrop = true;
            root.DragEnter += OnDragEnter;
            root.DragDrop += OnDragDrop;
        }
        catch { }
        foreach (Control child in root.Controls) RegisterDropTargets(child);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) ReplaceQueueWithPaths(paths);
    }

    private void ReplaceQueueWithPaths(IEnumerable<string> paths)
    {
        var batch = paths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var inputVersion = Interlocked.Increment(ref _inputVersion);
        var nextInputCancellation = new CancellationTokenSource();
        var previousInputCancellation = Interlocked.Exchange(ref _inputCts, nextInputCancellation);
        if (previousInputCancellation is not null)
        {
            try { previousInputCancellation.Cancel(); } catch (ObjectDisposedException) { }
            previousInputCancellation.Dispose();
        }

        Interlocked.Increment(ref _loadVersion);
        var previousAnalysisCancellation = Interlocked.Exchange(ref _analysisCts, null);
        if (previousAnalysisCancellation is not null)
        {
            try { previousAnalysisCancellation.Cancel(); } catch (ObjectDisposedException) { }
            previousAnalysisCancellation.Dispose();
        }
        _current?.Dispose();
        _current = null;
        _knownFiles.Clear();
        _fileList.Items.Clear();
        ClearCurrentUi();
        _pathDropText.Clear();
        _summaryLabel.Text = "画像 0/0";
        UseWaitCursor = false;

        if (batch.Length == 0)
        {
            _stateLabel.Text = "対象画像はありません（キューを空にしました）";
            Interlocked.CompareExchange(ref _inputCts, null, nextInputCancellation);
            nextInputCancellation.Dispose();
            return;
        }
        _stateLabel.Text = "新しい入力を確認中…";
        _ = ProcessReplacementInputAsync(batch, _recursiveButton.Checked, inputVersion, nextInputCancellation);
    }

    private async Task ProcessReplacementInputAsync(string[] batch, bool recursive, int inputVersion,
        CancellationTokenSource inputCancellation)
    {
        try
        {
            var expansion = await Task.Run(() => ExpandPaths(batch, recursive, inputCancellation.Token), inputCancellation.Token);
            inputCancellation.Token.ThrowIfCancellationRequested();
            await RunOnUiThreadAsync(() =>
            {
                if (_isClosing || IsDisposed || inputVersion != _inputVersion || inputCancellation.IsCancellationRequested) return;
                _knownFiles.Clear();
                _fileList.BeginUpdate();
                try
                {
                    _fileList.Items.Clear();
                    foreach (var path in expansion.Files)
                    {
                        if (_knownFiles.Add(path)) _fileList.Items.Add(new FileListItem(path));
                    }
                }
                finally { _fileList.EndUpdate(); }
                if (_fileList.Items.Count > 0) _fileList.SelectedIndex = 0;
                if (expansion.Errors.Count > 0)
                    _stateLabel.Text = $"一部を読み込めませんでした: {expansion.Errors[0]}";
                else if (_fileList.Items.Count == 0)
                    _stateLabel.Text = "対象画像はありません（キューを空にしました）";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (_isClosing || IsDisposed || inputVersion != _inputVersion) return;
                _knownFiles.Clear();
                _fileList.Items.Clear();
                ClearCurrentUi();
                _pathDropText.Clear();
                _summaryLabel.Text = "画像 0/0";
                _stateLabel.Text = $"入力エラー（キューを空にしました）: {ex.Message}";
            });
        }
        finally
        {
            Interlocked.CompareExchange(ref _inputCts, null, inputCancellation);
            inputCancellation.Dispose();
        }
    }

    private static (List<string> Files, List<string> Errors) ExpandPaths(string[] input, bool recursive,
        CancellationToken ct)
    {
        var files = new List<string>(); var errors = new List<string>();
        foreach (var raw in input)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var full = Path.GetFullPath(raw);
                if (File.Exists(full))
                {
                    if (SupportedExtensions.Contains(Path.GetExtension(full))) files.Add(full);
                }
                else if (Directory.Exists(full))
                {
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = recursive, IgnoreInaccessible = true, ReturnSpecialDirectories = false,
                        AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive
                    };
                    foreach (var file in Directory.EnumerateFiles(full, "*", options))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (SupportedExtensions.Contains(Path.GetExtension(file))) files.Add(file);
                    }
                }
                else errors.Add($"見つかりません: {full}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { errors.Add($"{raw}: {ex.Message}"); }
        }
        return (files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList(), errors);
    }

    private static (List<string> Files, List<string> Errors) ExpandPaths(string[] input, bool recursive)
        => ExpandPaths(input, recursive, CancellationToken.None);

    private async Task LoadSelectedAsync()
    {
        if (_isClosing || IsDisposed) return;
        if (_fileList.SelectedItem is not FileListItem selected) return;
        var version = Interlocked.Increment(ref _loadVersion);
        var nextCancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(ref _analysisCts, nextCancellation);
        if (previousCancellation is not null)
        {
            try { previousCancellation.Cancel(); } catch (ObjectDisposedException) { }
            previousCancellation.Dispose();
        }
        var ct = nextCancellation.Token;
        _stateLabel.Text = "解析中…"; _summaryLabel.Text = $"画像 {_fileList.SelectedIndex + 1}/{_fileList.Items.Count}";
        _pathDropText.Text = selected.Path;
        UseWaitCursor = true;
        try
        {
            var analysis = await ImageAnalysisService.AnalyzeAsync(selected.Path, ct);
            if (ct.IsCancellationRequested || version != _loadVersion || _isClosing || IsDisposed)
            {
                analysis.Dispose();
                return;
            }
            var applied = false;
            await RunOnUiThreadAsync(() =>
            {
                if (ct.IsCancellationRequested || version != _loadVersion || _isClosing || IsDisposed) return;
                _current?.Dispose();
                _current = analysis;
                _canvas.SetImage(analysis.Bitmap);
                PopulateCurrent();
                _stateLabel.Text = string.IsNullOrEmpty(analysis.Error) ? "完了" : analysis.Error;
                _summaryLabel.Text = $"{analysis.Ai.SourceLabel} | 項目 {analysis.Ai.Fields.Count + analysis.RawMetadata.Count:N0} | 画像 {_fileList.SelectedIndex + 1}/{_fileList.Items.Count}";
                applied = true;
            });
            if (!applied) analysis.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_isClosing || IsDisposed) return;
            await RunOnUiThreadAsync(() =>
            {
                if (_isClosing || IsDisposed || version != _loadVersion) return;
                _current?.Dispose();
                _current = null;
                ClearCurrentUi();
                _stateLabel.Text = $"エラー: {ex.Message}";
            });
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                if (!_isClosing && !IsDisposed && version == _loadVersion) UseWaitCursor = false;
            });
        }
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        if (_isClosing || IsDisposed) return Task.CompletedTask;
        if (!InvokeRequired)
        {
            action();
            return Task.CompletedTask;
        }
        if (!IsHandleCreated) return Task.CompletedTask;
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke((MethodInvoker)(() =>
            {
                try
                {
                    if (!_isClosing && !IsDisposed) action();
                    completion.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            completion.TrySetResult(null);
        }
        return completion.Task;
    }

    private void PopulateCurrent()
    {
        if (_current is null) return;
        var b = _current.BasicInfo;
        var fields = new List<MetadataField>
        {
            new("ファイル", "ファイル名", b.FileName), new("ファイル", "フルパス", b.FullPath),
            new("ファイル", "形式", b.Format.ToString()), new("ファイル", "MIME", b.Mime),
            new("ファイル", "容量", $"{b.FileSize:N0} bytes ({FormatBytes(b.FileSize)})"),
            new("画像", "幅", b.Width.ToString("N0")), new("画像", "高さ", b.Height.ToString("N0")),
            new("画像", "総画素数", b.PixelCount.ToString("N0")), new("画像", "Aspect Ratio", b.AspectRatio),
            new("画像", "Bit Depth", b.BitDepth), new("画像", "Pixel Format", b.PixelFormat),
            new("画像", "Color Space", b.ColorSpace), new("画像", "Alpha", b.Alpha), new("画像", "DPI", b.Dpi),
            new("画像", "Frame Count", b.FrameCount.ToString()), new("画像", "Orientation", b.Orientation.ToString()),
            new("日時", "作成日時", b.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")), new("日時", "更新日時", b.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")),
            new("ハッシュ", "SHA-256", b.Sha256), new("生成AI", "生成元", _current.Ai.SourceLabel)
        };
        if (!string.IsNullOrWhiteSpace(b.DecodeWarning)) fields.Add(new("状態", "注意", b.DecodeWarning));
        _overviewFields.Clear();
        _overviewFields.AddRange(fields);
        FillMetadataGrid(_overviewGrid, fields);
        _positiveText.Text = _current.Ai.PositivePrompt; _negativeText.Text = _current.Ai.NegativePrompt;
        FillMetadataGrid(_settingsGrid, _current.Ai.Fields.Where(x => !x.Group.StartsWith("Model", StringComparison.OrdinalIgnoreCase) && x.Group != "Prompt"));
        FillMetadataGrid(_modelGrid, _current.Ai.Fields.Where(x => x.Group.StartsWith("Model", StringComparison.OrdinalIgnoreCase)));
        FillRawGrid();
        _promptJsonText.Text = TextSafety.Limit(_current.Ai.RawPromptJson, AppLimits.MaxDisplayedValueChars);
        _workflowJsonText.Text = TextSafety.Limit(_current.Ai.RawWorkflowJson, AppLimits.MaxDisplayedValueChars);
        PopulateWorkflowTree(); RefreshRemovalPlan(); ApplySearch();
    }

    private void FillMetadataGrid(DataGridView grid, IEnumerable<MetadataField> fields)
    {
        grid.Rows.Clear();
        foreach (var field in fields) grid.Rows.Add(field.Group, field.Key, field.Value, "コピー");
        OptimizeGridColumns(grid);
    }

    private void FillRawGrid()
    {
        _rawGrid.Rows.Clear();
        if (_current is null) return;
        foreach (var item in _current.RawMetadata)
            _rawGrid.Rows.Add(item.Section, item.Identifier, item.Name, item.Kind, item.Size.ToString("N0"), item.Value, "コピー");
        OptimizeGridColumns(_rawGrid);
    }

    private void PopulateWorkflowTree()
    {
        _workflowTree.BeginUpdate(); _workflowTree.Nodes.Clear();
        try
        {
            var count = 0;
            if (!string.IsNullOrWhiteSpace(_promptJsonText.Text))
            {
                var root = _workflowTree.Nodes.Add("Prompt API Graph"); AddJsonTree(root, _promptJsonText.Text, ref count); root.Expand();
            }
            if (!string.IsNullOrWhiteSpace(_workflowJsonText.Text))
            {
                var root = _workflowTree.Nodes.Add("Workflow / Nodes / Links"); AddJsonTree(root, _workflowJsonText.Text, ref count); root.Expand();
            }
            if (_workflowTree.Nodes.Count == 0 && _current is not null && !string.IsNullOrWhiteSpace(_current.Ai.WorkflowSummary))
                _workflowTree.Nodes.Add(_current.Ai.WorkflowSummary);
        }
        finally { _workflowTree.EndUpdate(); }
    }

    private static void AddJsonTree(TreeNode parent, string json, ref int count)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = AppLimits.MaxJsonDepth, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            AddElement(parent, doc.RootElement, ref count, 0);
        }
        catch (JsonException ex) { parent.Nodes.Add($"JSON解析エラー: {ex.Message}"); }
    }

    private static void AddElement(TreeNode parent, JsonElement element, ref int count, int depth)
    {
        if (count >= 5000) { if (parent.Nodes.Count == 0 || parent.Nodes[^1].Text != "…表示上限") parent.Nodes.Add("…表示上限"); return; }
        if (depth > 64) { parent.Nodes.Add("…深度上限"); return; }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (++count > 5000) break;
                var node = parent.Nodes.Add(property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? property.Name : $"{property.Name} = {ShortJson(property.Value)}");
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) AddElement(node, property.Value, ref count, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (++count > 5000) break;
                var node = parent.Nodes.Add(item.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? $"[{index}]" : $"[{index}] = {ShortJson(item)}");
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array) AddElement(node, item, ref count, depth + 1);
                index++;
            }
        }
    }

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

    private void CopyOverview(object? feedbackTarget = null)
    {
        if (_overviewFields.Count == 0) return;
        CopyText(string.Join(Environment.NewLine, _overviewFields.Select(x => $"{x.Key}: {x.Value}")), feedbackTarget);
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

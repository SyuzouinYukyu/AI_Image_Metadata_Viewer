using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AIImageMetadataViewer;

internal sealed partial class MainForm
{
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
        FillMetadataGrid(_settingsGrid, _current.Ai.Fields.Where(IsGenerationSettingsField));
        FillMetadataGrid(_modelGrid, _current.Ai.Fields.Where(IsModelField));
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

}

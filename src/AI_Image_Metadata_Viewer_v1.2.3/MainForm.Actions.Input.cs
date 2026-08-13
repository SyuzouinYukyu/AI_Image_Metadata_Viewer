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

}

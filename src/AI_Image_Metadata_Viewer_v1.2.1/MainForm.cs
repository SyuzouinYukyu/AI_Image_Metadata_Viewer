using System.Reflection;
using System.Text.Json;

namespace AIImageMetadataViewer;

internal sealed partial class MainForm : Form
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".webp", ".tif", ".tiff", ".bmp", ".gif", ".avif", ".heic", ".heif", ".jxl" };

    private readonly AppSettings _settings = SettingsService.Load();
    private readonly TableLayoutPanel _rootLayout = new()
    {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty
    };
    private readonly ToolStrip _tools = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Fill, AutoSize = true };
    private readonly ToolStripButton _recursiveButton = new("サブフォルダー") { CheckOnClick = true, ToolTipText = "フォルダーのサブフォルダーも読み込む（既定OFF）" };
    private readonly ToolStripTextBox _searchBox = new() { AutoSize = false, Width = 190, ToolTipText = "現在画像のメタデータを検索" };
    private readonly TableLayoutPanel _pathDropPanel = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(4, 2, 4, 2) };
    private readonly TextBox _pathDropText = new() { Dock = DockStyle.Fill, ReadOnly = true, TabStop = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly SplitContainer _mainSplit = NewSplit(Orientation.Vertical, 760, 260, 420);
    private readonly SplitContainer _leftSplit = NewSplit(Orientation.Horizontal, 570, 180, 120);
    private readonly SplitContainer _promptSplit = NewSplit(Orientation.Horizontal, 360, 120, 120);
    private readonly ImageCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly ListBox _fileList = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly FlowLayoutPanel _tabNavigation = new()
    {
        Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = Padding.Empty,
        Padding = new Padding(3)
    };
    private readonly TabControl _tabs = new HeaderlessTabControl { Dock = DockStyle.Fill };
    private readonly List<Button> _tabButtons = [];
    private readonly DataGridView _overviewGrid;
    private readonly Button _overviewCopyButton = new() { Text = "概要をコピー", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly DataGridView _settingsGrid;
    private readonly DataGridView _modelGrid;
    private readonly DataGridView _rawGrid;
    private readonly TextBox _positiveText = CreatePromptTextBox();
    private readonly TextBox _negativeText = CreatePromptTextBox();
    private readonly TreeView _workflowTree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly TextBox _promptJsonText = CreateJsonTextBox();
    private readonly TextBox _workflowJsonText = CreateJsonTextBox();
    private readonly DataGridView _removalGrid;
    private readonly ComboBox _removalMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _overwriteCheck = new() { Text = "元ファイルへ反映（既定OFF）", AutoSize = true, Dock = DockStyle.Fill };
    private readonly Button _executeRemovalButton = new() { Text = "削除を実行…", AutoSize = true, Enabled = false };
    private readonly StatusStrip _status = new() { Dock = DockStyle.Fill, SizingGrip = false };
    private readonly ToolStripStatusLabel _stateLabel = new("待機中");
    private readonly ToolStripStatusLabel _summaryLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _zoomLabel = new("Zoom: —");
    private readonly HashSet<string> _knownFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MetadataField> _overviewFields = [];
    private readonly List<DataGridView> _searchableGrids = [];
    private readonly HashSet<DataGridView> _optimizingGrids = [];
    private readonly List<Button> _copyButtons = [];
    private readonly List<ToolStripItem> _copyToolItems = [];
    private readonly CopyFeedbackManager _copyFeedback = new();
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _inputCts;
    private AnalysisResult? _current;
    private int _inputVersion;
    private int _loadVersion;
    private Font? _appFont;
    private Font? _promptDisplayFont;
    private bool _settingsSaved;
    private bool _isClosing;

    internal TableLayoutPanel RootLayoutForTests => _rootLayout;
    internal TabControl TabsForTests => _tabs;
    internal IReadOnlyList<Button> TabNavigationButtonsForTests => _tabButtons;
    internal SplitContainer MainSplitForTests => _mainSplit;
    internal SplitContainer LeftSplitForTests => _leftSplit;
    internal SplitContainer PromptSplitForTests => _promptSplit;
    internal IReadOnlyList<Button> CopyButtonsForTests => _copyButtons;
    internal int ActiveCopyFeedbackCountForTests => _copyFeedback.ActiveCount;

    public MainForm()
    {
        Text = "AI Image Metadata Viewer v1.2.1";
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(720, 520);
        Size = new Size(1400, 900);
        KeyPreview = true;
        AllowDrop = true;
        SetApplicationIcon();
        _appFont = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily,
            _settings.FontSize, FontStyle.Regular, GraphicsUnit.Point);
        Font = _appFont;
        _tools.Font = _appFont;
        _status.Font = _appFont;
        _promptDisplayFont = new Font("Consolas", _settings.FontSize, FontStyle.Regular, GraphicsUnit.Point);
        _positiveText.Font = _promptDisplayFont;
        _negativeText.Font = _promptDisplayFont;
        _overviewGrid = CreateMetadataGrid("区分", "項目");
        _settingsGrid = CreateMetadataGrid("区分", "項目");
        _modelGrid = CreateMetadataGrid("区分", "項目");
        _rawGrid = CreateRawGrid();
        _removalGrid = CreateRemovalGrid();
        BuildToolStrip();
        BuildTabs();
        BuildLayout();
        ConfigureGridMetrics();
        ConfigureEvents();
        RestoreSettings();
        RegisterDropTargets(this);
    }

    private static SplitContainer NewSplit(Orientation orientation, int distance, int panel1Minimum, int panel2Minimum) => new VisibleSplitContainer
    {
        Size = new Size(1200, 700),
        SplitterDistance = distance,
        Panel1MinSize = panel1Minimum,
        Panel2MinSize = panel2Minimum,
        SplitterWidth = 9,
        Dock = DockStyle.Fill,
        Orientation = orientation,
        BackColor = SystemColors.ControlDark
    };

    private void SetApplicationIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AIImageMetadataViewer.app.ico");
            if (stream is not null) Icon = new Icon(stream);
        }
        catch
        {
            // アイコン読込失敗だけでアプリを停止しない。
        }
    }

    private void BuildToolStrip()
    {
        AddToolButton("開く…", (_, _) => OpenFiles(), "Ctrl+O");
        AddToolButton("前へ", (_, _) => Navigate(-1), "←");
        AddToolButton("次へ", (_, _) => Navigate(1), "→");
        _tools.Items.Add(new ToolStripSeparator());
        AddToolButton("Fit", (_, _) => _canvas.Fit(), "Ctrl+0");
        AddToolButton("100%", (_, _) => _canvas.ActualSize(), "Ctrl+1");
        AddToolButton("縮小", (_, _) => _canvas.ZoomBy(1 / 1.2f), "Ctrl+-");
        AddToolButton("拡大", (_, _) => _canvas.ZoomBy(1.2f), "Ctrl++");
        _tools.Items.Add(new ToolStripSeparator());
        AddToolButton("Explorer", (_, _) => OpenInExplorer(), "ファイル位置を開く");
        _copyToolItems.Add(AddToolButton("名前コピー", (sender, _) => CopyCurrent(false, sender), "ファイル名をコピー"));
        _copyToolItems.Add(AddToolButton("パスコピー", (sender, _) => CopyCurrent(true, sender), "フルパスをコピー"));
        _tools.Items.Add(new ToolStripSeparator());
        _recursiveButton.Checked = _settings.IncludeSubfolders;
        _tools.Items.Add(_recursiveButton);
        _tools.Items.Add(new ToolStripSeparator());
        _tools.Items.Add(new ToolStripLabel("検索:"));
        _tools.Items.Add(_searchBox);
    }

    private void BuildLayout()
    {
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _pathDropPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _pathDropPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _pathDropPanel.Controls.Add(new Label { Text = "現在のパス:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _pathDropPanel.Controls.Add(_pathDropText, 1, 0);
        _leftSplit.Panel1.BackColor = SystemColors.Control;
        _leftSplit.Panel2.BackColor = SystemColors.Control;
        _mainSplit.Panel1.BackColor = SystemColors.Control;
        _mainSplit.Panel2.BackColor = SystemColors.Control;
        _leftSplit.Panel1.Controls.Add(_canvas);
        var listGroup = new GroupBox { Text = "ファイル一覧", Dock = DockStyle.Fill };
        listGroup.Controls.Add(_fileList);
        _leftSplit.Panel2.Controls.Add(listGroup);
        _mainSplit.Panel1.Controls.Add(_leftSplit);
        var metadataLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty
        };
        metadataLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        metadataLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        metadataLayout.Controls.Add(_tabNavigation, 0, 0);
        metadataLayout.Controls.Add(_tabs, 0, 1);
        _mainSplit.Panel2.Controls.Add(metadataLayout);
        _status.Items.AddRange([_stateLabel, new ToolStripStatusLabel(" | "), _summaryLabel, _zoomLabel]);
        _rootLayout.Controls.Add(_tools, 0, 0);
        _rootLayout.Controls.Add(_pathDropPanel, 0, 1);
        _rootLayout.Controls.Add(_mainSplit, 0, 2);
        _rootLayout.Controls.Add(_status, 0, 3);
        Controls.Add(_rootLayout);
    }

    private void BuildTabs()
    {
        _tabs.TabPages.Add(BuildOverviewTab());
        _tabs.TabPages.Add(BuildPromptTab());
        _tabs.TabPages.Add(BuildSettingsTab());
        _tabs.TabPages.Add(MakeTab("Model / LoRA", _modelGrid));
        _tabs.TabPages.Add(BuildWorkflowTab());
        _tabs.TabPages.Add(BuildRawTab());
        _tabs.TabPages.Add(BuildRemovalTab());
        for (var index = 0; index < _tabs.TabCount; index++)
        {
            var tabIndex = index;
            var button = new Button
            {
                Text = _tabs.TabPages[index].Text, AutoSize = true, FlatStyle = FlatStyle.Flat,
                Margin = new Padding(2), Padding = new Padding(6, 2, 6, 2), UseVisualStyleBackColor = false
            };
            button.Click += (_, _) => _tabs.SelectedIndex = tabIndex;
            _tabButtons.Add(button);
            _tabNavigation.Controls.Add(button);
        }
        UpdateTabNavigation();
        _searchableGrids.AddRange([_overviewGrid, _settingsGrid, _modelGrid, _rawGrid]);
    }

    private TabPage BuildOverviewTab()
    {
        var page = new TabPage("概要") { Padding = new Padding(4) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(_overviewGrid, 0, 0);
        _overviewCopyButton.Click += (sender, _) => CopyOverview(sender);
        _copyButtons.Add(_overviewCopyButton);
        table.Controls.Add(_overviewCopyButton, 0, 1);
        page.Controls.Add(table);
        return page;
    }

    private TabPage BuildPromptTab()
    {
        var page = new TabPage("Prompt") { Padding = new Padding(4) };
        _promptSplit.Panel1.BackColor = SystemColors.Control;
        _promptSplit.Panel2.BackColor = SystemColors.Control;
        _promptSplit.Panel1.Controls.Add(PromptGroup("Positive Prompt", _positiveText));
        _promptSplit.Panel2.Controls.Add(PromptGroup("Negative Prompt", _negativeText));
        page.Controls.Add(_promptSplit);
        return page;
    }

    private Control PromptGroup(string title, TextBox text)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(4) };
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(text, 0, 0);
        var copy = new Button { Text = "全体をコピー", AutoSize = true, Anchor = AnchorStyles.Right };
        copy.Click += (sender, _) => CopyText(text.Text, sender);
        _copyButtons.Add(copy);
        table.Controls.Add(copy, 0, 1);
        group.Controls.Add(table);
        return group;
    }

    private TabPage BuildWorkflowTab()
    {
        var page = new TabPage("Workflow") { Padding = new Padding(4) };
        var outer = NewSplit(Orientation.Vertical, 300, 150, 220);
        outer.Panel1.BackColor = SystemColors.Control;
        outer.Panel2.BackColor = SystemColors.Control;
        outer.Panel1.Controls.Add(_workflowTree);
        var jsonTabs = new TabControl { Dock = DockStyle.Fill, Multiline = true };
        jsonTabs.TabPages.Add(MakeTab("RAW Prompt JSON", _promptJsonText));
        jsonTabs.TabPages.Add(MakeTab("RAW Workflow JSON", _workflowJsonText));
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(jsonTabs, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        var copyPrompt = NewCopyButton("Prompt JSONコピー", (sender, _) => CopyText(_promptJsonText.Text, sender));
        var copyWorkflow = NewCopyButton("Workflow JSONコピー", (sender, _) => CopyText(_workflowJsonText.Text, sender));
        var savePrompt = new Button { Text = "Prompt JSON保存…", AutoSize = true };
        savePrompt.Click += (_, _) => SaveJson(_promptJsonText.Text, "prompt.json");
        var saveWorkflow = new Button { Text = "Workflow JSON保存…", AutoSize = true };
        saveWorkflow.Click += (_, _) => SaveJson(_workflowJsonText.Text, "workflow.json");
        buttons.Controls.AddRange([copyPrompt, copyWorkflow, savePrompt, saveWorkflow]);
        right.Controls.Add(buttons, 0, 1);
        outer.Panel2.Controls.Add(right);
        page.Controls.Add(outer);
        return page;
    }

    private TabPage BuildRawTab()
    {
        var page = new TabPage("RAW Metadata") { Padding = new Padding(4) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(_rawGrid, 0, 0);
        var copy = NewCopyButton("RAW Metadata全体コピー", (sender, _) => CopyRawAll(sender));
        copy.Anchor = AnchorStyles.Right;
        table.Controls.Add(copy, 0, 1);
        page.Controls.Add(table);
        return page;
    }

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("生成設定") { Padding = new Padding(4) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(_settingsGrid, 0, 0);
        var copy = NewCopyButton("全生成設定コピー", (sender, _) =>
        {
            if (_current is not null) CopyText(string.Join(Environment.NewLine,
                _current.Ai.Fields.Where(x => x.Group != "Prompt").Select(x => $"{x.Key}={x.Value}")), sender);
        });
        copy.Anchor = AnchorStyles.Right;
        table.Controls.Add(copy, 0, 1);
        page.Controls.Add(table);
        return page;
    }

    private TabPage BuildRemovalTab()
    {
        var page = new TabPage("メタデータ削除") { Padding = new Padding(4) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var modeTable = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2 };
        modeTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        modeTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modeTable.Controls.Add(new Label { Text = "削除モード:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _removalMode.Items.AddRange([
            "A. 生成AI情報のみ削除", "B. プライバシーメタデータ削除",
            "C. 保護情報以外をすべて削除", "D. 完全削除（上級・表示変化の可能性）"]);
        _removalMode.SelectedIndex = (int)_settings.RemovalMode;
        modeTable.Controls.Add(_removalMode, 1, 0);
        _overwriteCheck.Checked = _settings.OverwriteSource;
        modeTable.Controls.Add(_overwriteCheck, 1, 1);
        table.Controls.Add(modeTable, 0, 0);
        table.Controls.Add(_removalGrid, 0, 1);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
        bottom.Controls.Add(_executeRemovalButton);
        bottom.Controls.Add(new Label { Text = "PNG/JPEG/WebPのみ無劣化処理。実行前一覧の「保護」を維持します。", AutoSize = true, Anchor = AnchorStyles.Left, AutoEllipsis = true });
        table.Controls.Add(bottom, 0, 2);
        page.Controls.Add(table);
        return page;
    }

    private Button NewCopyButton(string text, EventHandler action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += action;
        _copyButtons.Add(button);
        return button;
    }

    private void ConfigureGridMetrics()
    {
        var copyWidth = TextRenderer.MeasureText(CopyFeedbackManager.SuccessText, _appFont, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 34;
        foreach (var button in _copyButtons)
        {
            var preferred = button.GetPreferredSize(Size.Empty);
            button.MinimumSize = new Size(Math.Max(preferred.Width, copyWidth), preferred.Height);
        }
        foreach (var item in _copyToolItems)
        {
            var preferred = item.GetPreferredSize(Size.Empty);
            item.AutoSize = false;
            item.Size = new Size(Math.Max(preferred.Width, copyWidth), preferred.Height);
        }
        foreach (var grid in new[] { _overviewGrid, _settingsGrid, _modelGrid, _rawGrid, _removalGrid })
        {
            grid.Font = _appFont;
            grid.ColumnHeadersDefaultCellStyle.Font = _appFont;
            grid.RowTemplate.Height = Math.Max(34, _appFont!.Height + 12);
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            if (grid.Columns.Contains("Copy"))
            {
                var column = grid.Columns["Copy"]!;
                column.MinimumWidth = copyWidth;
                column.Width = copyWidth;
            }
            grid.ShowCellToolTips = true;
            grid.CellToolTipTextNeeded += GridCellToolTipTextNeeded;
            grid.Resize += (_, _) => OptimizeGridColumns(grid);
        }
    }

    private void OptimizeGridColumns(DataGridView grid)
    {
        if (grid.IsDisposed || grid.Columns.Count == 0 || grid.ClientSize.Width <= 0 || !_optimizingGrids.Add(grid)) return;
        try
        {
            var visibleColumns = grid.Columns.Cast<DataGridViewColumn>().Where(x => x.Visible).ToArray();
            if (visibleColumns.Length == 0) return;
            var available = Math.Max(180, grid.ClientSize.Width - 4 -
                (grid.DisplayedRowCount(false) < grid.Rows.GetRowCount(DataGridViewElementStates.Visible) ? SystemInformation.VerticalScrollBarWidth : 0));
            var natural = new Dictionary<DataGridViewColumn, int>();
            var assigned = new Dictionary<DataGridViewColumn, int>();
            foreach (var column in visibleColumns)
            {
                var headerWidth = MeasureGridText(column.HeaderText) + 28;
                var contentWidth = headerWidth;
                if (column is DataGridViewButtonColumn)
                {
                    contentWidth = Math.Max(headerWidth, MeasureGridText(CopyFeedbackManager.SuccessText) + 34);
                }
                else
                {
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        if (!row.Visible || row.IsNewRow) continue;
                        var value = row.Cells[column.Index].Value?.ToString() ?? string.Empty;
                        if (value.Length > 2048) { contentWidth = 1600; break; }
                        contentWidth = Math.Max(contentWidth, Math.Min(1600, MeasureGridText(value) + 24));
                    }
                }
                natural[column] = Math.Clamp(contentWidth, Math.Max(45, headerWidth), 1600);
                assigned[column] = natural[column];
            }

            var excess = assigned.Values.Sum() - available;
            var clipped = new HashSet<DataGridViewColumn>();
            if (excess > 0)
            {
                var candidates = visibleColumns
                    .Where(x => x is not DataGridViewButtonColumn)
                    .OrderByDescending(x => x.Name is "Value" or "Reason")
                    .ThenByDescending(x => natural[x])
                    .ToArray();
                foreach (var column in candidates)
                {
                    if (excess <= 0) break;
                    var minimum = column.Name is "Value" or "Reason" ? 180 : Math.Max(70, MeasureGridText(column.HeaderText) + 28);
                    var reduction = Math.Min(excess, Math.Max(0, assigned[column] - minimum));
                    if (reduction <= 0) continue;
                    assigned[column] -= reduction;
                    excess -= reduction;
                    clipped.Add(column);
                }
            }

            grid.SuspendLayout();
            try
            {
                grid.AllowUserToResizeColumns = clipped.Count > 0;
                foreach (var column in visibleColumns)
                {
                    column.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    column.Resizable = clipped.Contains(column) ? DataGridViewTriState.True : DataGridViewTriState.False;
                    column.MinimumWidth = Math.Min(column.MinimumWidth, assigned[column]);
                    column.Width = assigned[column];
                }
                grid.ScrollBars = clipped.Count > 0 || excess > 0 ? ScrollBars.Both : ScrollBars.Vertical;
            }
            finally { grid.ResumeLayout(); }
        }
        finally { _optimizingGrids.Remove(grid); }
    }

    private int MeasureGridText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return TextRenderer.MeasureText(text, _appFont, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
    }

    private void GridCellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0 || e.ColumnIndex < 0 ||
            grid.Columns[e.ColumnIndex] is DataGridViewButtonColumn) return;
        var cell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
        var value = cell.Value?.ToString() ?? string.Empty;
        if (value.Length > 0 && MeasureGridText(value) + 12 > cell.Size.Width) e.ToolTipText = value;
    }

    private void ConfigureEvents()
    {
        _fileList.SelectedIndexChanged += async (_, _) => await LoadSelectedAsync();
        _canvas.ZoomChanged += (_, _) => _zoomLabel.Text = $"Zoom: {_canvas.Zoom * 100:0.#}%{(_canvas.IsFit ? " (Fit)" : string.Empty)}";
        _searchBox.TextChanged += (_, _) => ApplySearch();
        _recursiveButton.CheckedChanged += (_, _) => _settings.IncludeSubfolders = _recursiveButton.Checked;
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            _settings.LastTab = _tabs.SelectedIndex;
            UpdateTabNavigation();
        };
        _removalMode.SelectedIndexChanged += (_, _) => RefreshRemovalPlan();
        _executeRemovalButton.Click += async (_, _) => await ExecuteRemovalAsync();
        FormClosing += (_, _) =>
        {
            _isClosing = true;
            Interlocked.Increment(ref _loadVersion);
            Interlocked.Increment(ref _inputVersion);
            _analysisCts?.Cancel();
            _inputCts?.Cancel();
            SaveSettings();
        };
    }

    private ToolStripButton AddToolButton(string text, EventHandler action, string tip)
    {
        var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip };
        button.Click += action;
        _tools.Items.Add(button);
        return button;
    }

    private void UpdateTabNavigation()
    {
        for (var index = 0; index < _tabButtons.Count; index++)
        {
            var selected = index == _tabs.SelectedIndex;
            _tabButtons[index].BackColor = selected ? SystemColors.Highlight : SystemColors.Control;
            _tabButtons[index].ForeColor = selected ? SystemColors.HighlightText : SystemColors.ControlText;
            _tabButtons[index].FlatAppearance.BorderSize = selected ? 2 : 1;
        }
    }

    private static TabPage MakeTab(string name, Control child)
    {
        var page = new TabPage(name) { Padding = new Padding(4) };
        child.Dock = DockStyle.Fill;
        page.Controls.Add(child);
        return page;
    }

    private static TextBox CreatePromptTextBox() => new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true,
        WordWrap = false, AcceptsReturn = true, MaxLength = AppLimits.MaxDisplayedValueChars
    };

    private static TextBox CreateJsonTextBox() => new()
    {
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true,
        WordWrap = false, MaxLength = AppLimits.MaxDisplayedValueChars
    };

    private DataGridView CreateMetadataGrid(string groupHeader, string keyHeader)
    {
        var grid = BaseGrid();
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Group", HeaderText = groupHeader, Width = 140, SortMode = DataGridViewColumnSortMode.Automatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Key", HeaderText = keyHeader, Width = 170, SortMode = DataGridViewColumnSortMode.Automatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "値", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 160, SortMode = DataGridViewColumnSortMode.Automatic });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Copy", HeaderText = "", Text = "コピー", UseColumnTextForButtonValue = true });
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Copy") return;
            var cell = (DataGridViewButtonCell)grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            CopyText(grid.Rows[e.RowIndex].Cells["Value"].Value?.ToString() ?? string.Empty, cell);
        };
        return grid;
    }

    private DataGridView CreateRawGrid()
    {
        var grid = BaseGrid();
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Section", HeaderText = "区分", Width = 105 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Identifier", HeaderText = "識別子", Width = 95 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "名称", Width = 160 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "種類", Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "サイズ", Width = 95 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "RAW値/構造", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180 });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Copy", HeaderText = "", Text = "コピー", UseColumnTextForButtonValue = true });
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Copy") return;
            var cell = (DataGridViewButtonCell)grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            CopyText(grid.Rows[e.RowIndex].Cells["Value"].Value?.ToString() ?? string.Empty, cell);
        };
        return grid;
    }

    private static DataGridView CreateRemovalGrid()
    {
        var grid = BaseGrid();
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Section", HeaderText = "区分", Width = 125 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "項目", Width = 220 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Action", HeaderText = "削除/保護", Width = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reason", HeaderText = "理由", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 180 });
        return grid;
    }

    private static DataGridView BaseGrid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = true, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.Fixed3D
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _copyFeedback.Dispose();
            _isClosing = true;
            Interlocked.Increment(ref _loadVersion);
            Interlocked.Increment(ref _inputVersion);
            var cancellation = Interlocked.Exchange(ref _analysisCts, null);
            if (cancellation is not null)
            {
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
                cancellation.Dispose();
            }
            var inputCancellation = Interlocked.Exchange(ref _inputCts, null);
            if (inputCancellation is not null)
            {
                try { inputCancellation.Cancel(); } catch (ObjectDisposedException) { }
                inputCancellation.Dispose();
            }
            _current?.Dispose();
            _current = null;
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _appFont?.Dispose();
            _appFont = null;
            _promptDisplayFont?.Dispose();
            _promptDisplayFont = null;
        }
    }

    private sealed record FileListItem(string Path)
    {
        public override string ToString() => System.IO.Path.GetFileName(Path);
    }
}

internal sealed class VisibleSplitContainer : SplitContainer
{
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateCursorForPoint(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        Cursor = Cursors.Default;
        base.OnMouseLeave(e);
    }

    internal void UpdateCursorForPoint(Point point)
    {
        Cursor = SplitterRectangle.Contains(point)
            ? Orientation == Orientation.Vertical ? Cursors.VSplit : Cursors.HSplit
            : Cursors.Default;
    }
}

internal sealed class HeaderlessTabControl : TabControl
{
    private const int TcmAdjustRect = 0x1328;

    public HeaderlessTabControl()
    {
        Appearance = TabAppearance.FlatButtons;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(0, 1);
        Multiline = true;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == TcmAdjustRect && !DesignMode)
        {
            message.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref message);
    }
}

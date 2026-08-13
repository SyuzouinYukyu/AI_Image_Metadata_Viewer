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
    private readonly Button _primaryGenerationInfoCopyButton = new() { Text = "主要生成情報をコピー", AutoSize = true, Anchor = AnchorStyles.Right };
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
    internal Button OverviewCopyButtonForTests => _overviewCopyButton;
    internal Button PrimaryGenerationInfoCopyButtonForTests => _primaryGenerationInfoCopyButton;

    public MainForm()
    {
        Text = "AI Image Metadata Viewer v1.2.3";
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

}

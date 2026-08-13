using System.Reflection;
using System.Text.Json;

namespace AIImageMetadataViewer;

internal sealed partial class MainForm
{
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
        Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, ReadOnly = true,
        WordWrap = true, AcceptsReturn = true, MaxLength = AppLimits.MaxDisplayedValueChars
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

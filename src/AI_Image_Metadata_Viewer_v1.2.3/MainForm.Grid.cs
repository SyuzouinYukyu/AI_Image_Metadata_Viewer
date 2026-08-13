using System.Reflection;
using System.Text.Json;

namespace AIImageMetadataViewer;

internal sealed partial class MainForm
{
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

}

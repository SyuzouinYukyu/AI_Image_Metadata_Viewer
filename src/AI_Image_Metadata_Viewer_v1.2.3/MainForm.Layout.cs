using System.Reflection;
using System.Text.Json;

namespace AIImageMetadataViewer;

internal sealed partial class MainForm
{
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
        _primaryGenerationInfoCopyButton.Click += (sender, _) => CopyPrimaryGenerationInfo(sender);
        _copyButtons.AddRange([_overviewCopyButton, _primaryGenerationInfoCopyButton]);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty
        };
        buttons.Controls.AddRange([_primaryGenerationInfoCopyButton, _overviewCopyButton]);
        table.Controls.Add(buttons, 0, 1);
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

}

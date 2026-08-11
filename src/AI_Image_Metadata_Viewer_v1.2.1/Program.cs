namespace AIImageMetadataViewer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ShowFatal(e.ExceptionObject as Exception ?? new InvalidOperationException("不明なエラー"));
        Application.Run(new MainForm());
    }

    private static void ShowFatal(Exception exception)
    {
        try
        {
            var owner = Application.OpenForms.Cast<Form>().FirstOrDefault();
            if (owner is not null && owner.InvokeRequired)
            {
                owner.BeginInvoke(() => ErrorDialog.ShowException(exception));
                return;
            }
            ErrorDialog.ShowException(exception);
        }
        catch
        {
            // ログファイルは作成しない。表示不能時は静かに終了する。
        }
    }
}

internal sealed class ErrorDialog : Form
{
    private readonly string _details;

    private ErrorDialog(Exception exception)
    {
        _details = $"例外型: {exception.GetType().FullName}\r\n" +
                   $"メッセージ: {exception.Message}\r\n\r\n" +
                   $"スタックトレース:\r\n{exception.StackTrace ?? "（取得できません）"}\r\n\r\n" +
                   $"完全な例外情報:\r\n{exception}";
        Text = "AI Image Metadata Viewer - 予期しないエラー";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(700, 480);
        Size = new Size(900, 650);
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12)
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = "予期しないエラーが発生しました。下の詳細をコピーして報告できます。",
            AutoSize = true, MaximumSize = new Size(840, 0)
        }, 0, 0);
        table.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, Text = _details, Font = new Font(FontFamily.GenericMonospace, 10)
        }, 0, 1);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft
        };
        var close = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.OK };
        var copy = new Button { Text = "詳細をコピー", AutoSize = true };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_details); }
            catch (ExternalException ex) { MessageBox.Show(ex.Message, "コピーできません", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);
        table.Controls.Add(buttons, 0, 2);
        Controls.Add(table);
        AcceptButton = close;
        CancelButton = close;
    }

    internal static void ShowException(Exception exception)
    {
        using var dialog = new ErrorDialog(exception);
        dialog.ShowDialog();
    }
}

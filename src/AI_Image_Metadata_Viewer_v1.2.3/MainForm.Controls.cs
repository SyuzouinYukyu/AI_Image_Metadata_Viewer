using System.Reflection;
using System.Text.Json;

namespace AIImageMetadataViewer;

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

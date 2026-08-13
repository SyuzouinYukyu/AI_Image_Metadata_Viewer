namespace AIImageMetadataViewer;

internal sealed class CopyFeedbackManager : IDisposable
{
    internal const string SuccessText = "コピー済";
    private readonly Dictionary<object, FeedbackState> _active =
        new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    internal int ActiveCount => _active.Count;

    internal void ShowSuccess(object target)
    {
        if (_disposed) return;
        if (_active.TryGetValue(target, out var existing))
        {
            existing.Timer.Stop();
            SetSuccessText(target);
            existing.Timer.Start();
            return;
        }
        if (!TryCreateState(target, out var state)) return;
        state.Timer.Tick += (_, _) => Complete(target, state);
        _active.Add(target, state);
        SetSuccessText(target);
        state.Timer.Start();
    }

    private void Complete(object target, FeedbackState state)
    {
        state.Timer.Stop();
        if (_active.Remove(target))
        {
            try { state.Restore(); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        }
        state.Timer.Dispose();
    }

    private static bool TryCreateState(object target, out FeedbackState state)
    {
        switch (target)
        {
            case ButtonBase button:
            {
                var original = button.Text;
                state = new FeedbackState(new System.Windows.Forms.Timer { Interval = 1000 },
                    () => { if (!button.IsDisposed) button.Text = original; });
                return true;
            }
            case ToolStripItem item:
            {
                var original = item.Text;
                state = new FeedbackState(new System.Windows.Forms.Timer { Interval = 1000 },
                    () => { if (!item.IsDisposed) item.Text = original; });
                return true;
            }
            case DataGridViewButtonCell cell:
            {
                var originalValue = cell.Value;
                var originalUseColumnText = cell.UseColumnTextForButtonValue;
                state = new FeedbackState(new System.Windows.Forms.Timer { Interval = 1000 }, () =>
                {
                    cell.UseColumnTextForButtonValue = originalUseColumnText;
                    cell.Value = originalValue;
                });
                return true;
            }
            default:
                state = null!;
                return false;
        }
    }

    private static void SetSuccessText(object target)
    {
        switch (target)
        {
            case ButtonBase button when !button.IsDisposed:
                button.Text = SuccessText;
                break;
            case ToolStripItem item when !item.IsDisposed:
                item.Text = SuccessText;
                break;
            case DataGridViewButtonCell cell:
                cell.UseColumnTextForButtonValue = false;
                cell.Value = SuccessText;
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _active.Values)
        {
            state.Timer.Stop();
            state.Timer.Dispose();
            try { state.Restore(); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        }
        _active.Clear();
    }

    private sealed record FeedbackState(System.Windows.Forms.Timer Timer, Action Restore);
}

using System.Drawing.Drawing2D;

namespace AIImageMetadataViewer;

internal sealed class ImageCanvas : Control
{
    private Bitmap? _image;
    private float _zoom = 1;
    private PointF _pan;
    private bool _fit = true;
    private bool _panning;
    private Point _lastMouse;

    public event EventHandler? ZoomChanged;
    public float Zoom => _zoom;
    public bool IsFit => _fit;

    public ImageCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(31, 31, 31);
        TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        MouseWheel += (_, e) => ZoomAt(e.Location, e.Delta > 0 ? 1.2f : 1 / 1.2f);
        MouseDown += OnCanvasMouseDown;
        MouseMove += OnCanvasMouseMove;
        MouseUp += (_, _) => { _panning = false; Cursor = Cursors.Default; };
        MouseDoubleClick += (_, _) => { if (_fit) ActualSize(); else Fit(); };
    }

    public void SetImage(Bitmap? image)
    {
        _image = image;
        Fit();
        Invalidate();
    }

    public void Fit()
    {
        _fit = true;
        if (_image is not null && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            _zoom = Math.Min((float)ClientSize.Width / _image.Width, (float)ClientSize.Height / _image.Height);
            _zoom = Math.Clamp(_zoom, 0.001f, 100f);
        }
        else _zoom = 1;
        _pan = PointF.Empty;
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void ActualSize()
    {
        _fit = false; _zoom = 1; _pan = PointF.Empty;
        ZoomChanged?.Invoke(this, EventArgs.Empty); Invalidate();
    }

    public void ZoomBy(float factor) => ZoomAt(new Point(ClientSize.Width / 2, ClientSize.Height / 2), factor);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_fit) Fit();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawCheckerboard(e.Graphics);
        if (_image is null)
        {
            using var brush = new SolidBrush(Color.Gainsboro);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString("画像を開くか、ここへドロップしてください", Font, brush, ClientRectangle, format);
            return;
        }
        var w = _image.Width * _zoom; var h = _image.Height * _zoom;
        var x = (ClientSize.Width - w) / 2 + _pan.X;
        var y = (ClientSize.Height - h) / 2 + _pan.Y;
        e.Graphics.InterpolationMode = _zoom >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_image, new RectangleF(x, y, w, h));
    }

    private void DrawCheckerboard(Graphics g)
    {
        const int size = 16;
        using var a = new SolidBrush(Color.FromArgb(48, 48, 48));
        using var b = new SolidBrush(Color.FromArgb(58, 58, 58));
        for (var y = 0; y < Height; y += size)
            for (var x = 0; x < Width; x += size)
                g.FillRectangle(((x / size + y / size) & 1) == 0 ? a : b, x, y, size, size);
    }

    private void ZoomAt(Point cursor, float factor)
    {
        if (_image is null) return;
        var old = _zoom;
        var next = Math.Clamp(old * factor, 0.01f, 64f);
        if (Math.Abs(next - old) < 0.00001f) return;
        var center = new PointF(ClientSize.Width / 2f + _pan.X, ClientSize.Height / 2f + _pan.Y);
        var imageX = (cursor.X - center.X) / old;
        var imageY = (cursor.Y - center.Y) / old;
        _zoom = next; _fit = false;
        _pan.X += cursor.X - (center.X + imageX * next);
        _pan.Y += cursor.Y - (center.Y + imageY * next);
        ZoomChanged?.Invoke(this, EventArgs.Empty); Invalidate();
    }

    private void OnCanvasMouseDown(object? sender, MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left && _image is not null && (_image.Width * _zoom > Width || _image.Height * _zoom > Height))
        {
            _panning = true; _lastMouse = e.Location; Cursor = Cursors.Hand;
        }
    }

    private void OnCanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_panning) return;
        _pan.X += e.X - _lastMouse.X; _pan.Y += e.Y - _lastMouse.Y; _lastMouse = e.Location;
        Invalidate();
    }
}

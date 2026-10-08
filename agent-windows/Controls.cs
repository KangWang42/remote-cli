using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteCli {
/// The colours of the window: the same palette as the terminal's default skin on the phone.
public static class Theme {
    public static readonly Color Bg = Color.FromArgb(26, 27, 38), Panel = Color.FromArgb(34, 36, 54), Raised = Color.FromArgb(44, 47, 71),
        Line = Color.FromArgb(47, 51, 77), Ink = Color.FromArgb(192, 202, 245), Muted = Color.FromArgb(129, 137, 173), Accent = Color.FromArgb(122, 162, 247),
        OnAccent = Color.FromArgb(16, 18, 28), Good = Color.FromArgb(158, 206, 106), Busy = Color.FromArgb(224, 175, 104), Bad = Color.FromArgb(247, 118, 142);

    public static GraphicsPath Rounded(RectangleF r, float radius) {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
    /// The program's mark: a terminal window with a prompt, drawn at any size.
    public static Bitmap Mark(int size) {
        var picture = new Bitmap(size, size);
        using (var g = Graphics.FromImage(picture)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = size / 32f;
            using (var path = Rounded(new RectangleF(1 * u, 1 * u, 30 * u, 30 * u), 7 * u))
            using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), Raised, Bg)) g.FillPath(fill, path);
            using (var pen = new Pen(Accent, 3.2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(8 * u, 10.5f * u), new PointF(14.5f * u, 16 * u), new PointF(8 * u, 21.5f * u) });
            using (var pen = new Pen(Good, 3.2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, 17 * u, 22 * u, 24.5f * u, 22 * u);
        }
        return picture;
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    public static Icon MarkIcon(int size) {
        using (var picture = Mark(size)) {
            IntPtr handle = picture.GetHicon();
            try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
        }
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    /// A dark title bar where Windows offers one (10 version 2004 and later); older systems keep theirs.
    public static void DarkTitle(IntPtr window) { try { int on = 1; DwmSetWindowAttribute(window, 20, ref on, 4); } catch { } }

    public static Button Button(string text, bool primary = false) {
        var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Raised, ForeColor = primary ? OnAccent : Ink, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(150, 182, 250) : Color.FromArgb(54, 58, 86);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(105, 145, 235) : Line;
        return button;
    }
    public static Label Label(string text, Color color, Color back, float size = 0, bool bold = false) {
        var label = new Label { Text = text, ForeColor = color, BackColor = back, AutoEllipsis = true };
        if (size > 0 || bold) label.Font = new Font("Microsoft YaHei UI", size > 0 ? size : 9f, bold ? FontStyle.Bold : FontStyle.Regular);
        return label;
    }
}

/// A rounded panel that groups related controls.
public sealed class Card : Panel {
    public Card() { BackColor = Theme.Bg; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = 10f * e.Graphics.DpiX / 96f;
        using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius))
        using (var fill = new SolidBrush(Theme.Panel)) using (var edge = new Pen(Theme.Line)) { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path); }
    }
}

/// A text box on a rounded dark field.
public sealed class Field : Panel {
    public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Bg, ForeColor = Theme.Ink };
    public Field() { BackColor = Theme.Panel; DoubleBuffered = true; Controls.Add(Box); Resize += (s, e) => Arrange(); }
    void Arrange() { int inset = Math.Max(8, Height / 4); Box.SetBounds(inset, (Height - Box.PreferredHeight) / 2, Math.Max(10, Width - inset * 2), Box.PreferredHeight); }
    // Scaling the window for the screen also moves the box; it is centred again afterwards.
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Arrange(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Arrange(); }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 6f * e.Graphics.DpiX / 96f))
        using (var fill = new SolidBrush(Theme.Bg)) using (var edge = new Pen(Theme.Line)) { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path); }
    }
}

/// One of several choices: a title, a line of explanation, and a mark when it is the chosen one.
public sealed class Option : Control {
    public string Title = "", About = "";
    bool chosen, hover;
    public event EventHandler Chosen;
    public bool Checked { get { return chosen; } set { if (chosen == value) return; chosen = value; Invalidate(); } }
    public Option() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); BackColor = Theme.Panel; Cursor = Cursors.Hand; TabStop = true; }
    void Pick() { if (chosen) return; if (Chosen != null) Chosen(this, EventArgs.Empty); }
    protected override void OnClick(EventArgs e) { Focus(); Pick(); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Pick(); base.OnKeyDown(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = g.DpiX / 96f;
        using (var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8f * k))
        using (var fill = new SolidBrush(chosen ? Theme.Raised : hover ? Color.FromArgb(40, 43, 64) : Theme.Panel))
        using (var edge = new Pen(chosen ? Theme.Accent : Focused ? Theme.Muted : Theme.Line, chosen ? 1.6f : 1f)) { g.FillPath(fill, path); g.DrawPath(edge, path); }
        float ring = 16 * k, x = 14 * k, y = (Height - ring) / 2;
        using (var pen = new Pen(chosen ? Theme.Accent : Theme.Muted, 1.6f * k)) g.DrawEllipse(pen, x, y, ring, ring);
        if (chosen) using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, x + 4 * k, y + 4 * k, ring - 8 * k, ring - 8 * k);
        float left = x + ring + 12 * k;
        using (var bold = new Font(Font, FontStyle.Bold)) using (var ink = new SolidBrush(Theme.Ink)) using (var muted = new SolidBrush(Theme.Muted))
        using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }) {
            float line = bold.GetHeight(g), top = (Height - line * 2 - 2 * k) / 2;
            g.DrawString(Title, bold, ink, new RectangleF(left, top, Width - left - 8 * k, line), format);
            g.DrawString(About, Font, muted, new RectangleF(left, top + line + 2 * k, Width - left - 8 * k, line), format);
        }
    }
}
}

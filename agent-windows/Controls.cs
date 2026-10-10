using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteCli {
/// The look of the windows: the palette of the terminal's default skin on the phone, and the small things drawn with it.
public static class Theme {
    public static readonly Color Bg = Color.FromArgb(26, 27, 38), Side = Color.FromArgb(21, 22, 31), Panel = Color.FromArgb(34, 36, 54), Raised = Color.FromArgb(44, 47, 71),
        Hover = Color.FromArgb(54, 58, 86), Line = Color.FromArgb(47, 51, 77), Ink = Color.FromArgb(192, 202, 245), Muted = Color.FromArgb(129, 137, 173),
        Faint = Color.FromArgb(86, 92, 124), Accent = Color.FromArgb(122, 162, 247), OnAccent = Color.FromArgb(16, 18, 28), Good = Color.FromArgb(158, 206, 106),
        Busy = Color.FromArgb(224, 175, 104), Bad = Color.FromArgb(247, 118, 142);
    public const string Family = "Microsoft YaHei UI", Icons = "Segoe MDL2 Assets";
    // Glyphs of the icon typeface that ships with Windows 10 and 11.
    public const string IconLink = "", IconFolder = "", IconPulse = "", IconGear = "", IconCopy = "", IconRefresh = "",
        IconAdd = "", IconDelete = "", IconEdit = "", IconOpen = "", IconWifi = "", IconGlobe = "", IconServer = "",
        IconLock = "", IconDownload = "", IconCheck = "", IconPhone = "", IconPower = "", IconTerminal = "";

    public static Font Text(float size = 9f, bool bold = false) { return new Font(Family, size, bold ? FontStyle.Bold : FontStyle.Regular); }
    public static float Dpi(Graphics g) { return g.DpiX / 96f; }
    public static void Smooth(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; }

    public static GraphicsPath Rounded(RectangleF r, float radius) {
        float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
    public static void Fill(Graphics g, RectangleF r, float radius, Color fill, Color? edge = null, float edgeWidth = 1f) {
        using (var path = Rounded(r, radius)) {
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            if (edge.HasValue) using (var pen = new Pen(edge.Value, edgeWidth)) g.DrawPath(pen, path);
        }
    }
    /// The program's mark: a terminal window with a prompt, drawn at any size.
    public static Bitmap Mark(int size) {
        var picture = new Bitmap(size, size);
        using (var g = Graphics.FromImage(picture)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = size / 32f;
            using (var path = Rounded(new RectangleF(1 * u, 1 * u, 30 * u, 30 * u), 7 * u))
            using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), Color.FromArgb(58, 64, 104), Color.FromArgb(30, 32, 48))) g.FillPath(fill, path);
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
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr window, string app, string ids);
    /// A dark title bar where Windows offers one (10 version 2004 and later); older systems keep theirs.
    public static void DarkTitle(IntPtr window) {
        try { int on = 1; DwmSetWindowAttribute(window, 20, ref on, 4); int colour = Bg.R | Bg.G << 8 | Bg.B << 16; DwmSetWindowAttribute(window, 35, ref colour, 4); } catch { }
    }
    /// Dark scroll bars for a list.
    public static void DarkScroll(Control control) { control.HandleCreated += (s, e) => { try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch { } }; }

    public static Button Button(string text, bool primary = false) { return new RoundButton { Text = text, Kind = primary ? ButtonKind.Primary : ButtonKind.Normal }; }
    public static Label Label(string text, Color color, Color back, float size = 0, bool bold = false) {
        var label = new Label { Text = text, ForeColor = color, BackColor = back, AutoEllipsis = true, UseMnemonic = false };
        if (size > 0 || bold) label.Font = Text(size > 0 ? size : 9f, bold);
        return label;
    }
}

public enum ButtonKind { Normal, Primary, Ghost, Danger }

/// A button with rounded corners, an optional icon before its text, and the four looks used in the windows.
public sealed class RoundButton : Button {
    public ButtonKind Kind = ButtonKind.Normal;
    public string Glyph = "";
    bool over, down;
    public RoundButton() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand; UseVisualStyleBackColor = false; BackColor = Theme.Bg; ForeColor = Theme.Ink;
    }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) g.FillRectangle(back, ClientRectangle);
        Color fill, ink, edge;
        switch (Kind) {
            case ButtonKind.Primary: fill = down ? Color.FromArgb(104, 144, 234) : over ? Color.FromArgb(146, 180, 250) : Theme.Accent; ink = Theme.OnAccent; edge = fill; break;
            case ButtonKind.Ghost: fill = down ? Theme.Line : over ? Theme.Raised : (Parent == null ? Theme.Bg : Parent.BackColor); ink = over ? Theme.Ink : Theme.Muted; edge = fill; break;
            case ButtonKind.Danger: fill = down ? Color.FromArgb(92, 44, 58) : over ? Color.FromArgb(74, 40, 54) : Theme.Raised; ink = Theme.Bad; edge = over ? Theme.Bad : Theme.Line; break;
            default: fill = down ? Theme.Line : over ? Theme.Hover : Theme.Raised; ink = Theme.Ink; edge = Theme.Line; break;
        }
        if (!Enabled) { ink = Theme.Faint; if (Kind == ButtonKind.Primary) { fill = Theme.Raised; edge = Theme.Line; } }
        Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8f * k, fill, edge);
        if (Focused && ShowFocusCues) using (var ring = new Pen(Theme.Accent, 1.4f * k)) using (var path = Theme.Rounded(new RectangleF(1.5f, 1.5f, Width - 3, Height - 3), 7f * k)) g.DrawPath(ring, path);
        using (var brush = new SolidBrush(ink)) using (var icon = new Font(Theme.Icons, Font.Size + 1f)) {
            SizeF text = g.MeasureString(Text, Font), glyph = Glyph.Length > 0 ? g.MeasureString(Glyph, icon) : SizeF.Empty;
            float gap = Glyph.Length > 0 && Text.Length > 0 ? 3 * k : 0, total = text.Width + glyph.Width + gap, x = (Width - total) / 2;
            if (Glyph.Length > 0) { g.DrawString(Glyph, icon, brush, x, (Height - glyph.Height) / 2 + 1 * k); x += glyph.Width + gap; }
            g.DrawString(Text, Font, brush, x, (Height - text.Height) / 2);
        }
    }
}

/// A rounded panel that groups related controls.
public sealed class Card : Panel {
    public Color Fill = Theme.Panel;
    public Card() { BackColor = Theme.Panel; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) {
        Theme.Smooth(e.Graphics);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
        Theme.Fill(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 12f * Theme.Dpi(e.Graphics), Fill, Theme.Line);
    }
}

/// A text box on a rounded dark field.
public sealed class Field : Panel {
    public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Bg, ForeColor = Theme.Ink };
    public Field() { BackColor = Theme.Panel; DoubleBuffered = true; Controls.Add(Box); Box.GotFocus += (s, e) => Invalidate(); Box.LostFocus += (s, e) => Invalidate(); Click += (s, e) => Box.Focus(); }
    void Arrange() { int inset = Math.Max(8, Height / 3); Box.SetBounds(inset, (Height - Box.PreferredHeight) / 2, Math.Max(10, Width - inset * 2), Box.PreferredHeight); }
    // Scaling the window for the screen also moves the box; it is centred again afterwards.
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); Arrange(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Arrange(); }
    protected override void OnPaint(PaintEventArgs e) {
        Theme.Smooth(e.Graphics);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) e.Graphics.FillRectangle(back, ClientRectangle);
        Theme.Fill(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8f * Theme.Dpi(e.Graphics), Theme.Bg, Box.Focused && !Box.ReadOnly ? Theme.Accent : Theme.Line);
    }
}

/// One of several choices: a title, a line of explanation, and a mark when it is the chosen one. (Used by the installer.)
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
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8f * k, chosen ? Theme.Raised : hover ? Color.FromArgb(40, 43, 64) : Theme.Panel, chosen ? Theme.Accent : Theme.Line, chosen ? 1.6f : 1f);
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

/// An on/off switch.
public sealed class Switch : Control {
    bool on, over;
    public event EventHandler Changed;
    public bool On { get { return on; } set { if (on == value) return; on = value; Invalidate(); } }
    public Switch() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); Cursor = Cursors.Hand; TabStop = true; BackColor = Theme.Panel; }
    void Flip() { on = !on; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }
    protected override void OnClick(EventArgs e) { Focus(); Flip(); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Flip(); base.OnKeyDown(e); }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) g.FillRectangle(back, ClientRectangle);
        float h = Math.Min(Height - 2, Width / 1.9f), y = (Height - h) / 2, knob = h - 6 * Theme.Dpi(g) * 0.8f;
        Theme.Fill(g, new RectangleF(1, y, Width - 2, h), h / 2, on ? (over ? Color.FromArgb(146, 180, 250) : Theme.Accent) : (over ? Theme.Hover : Theme.Raised), on ? (Color?)null : Theme.Faint);
        float pad = (h - knob) / 2, x = on ? Width - 1 - pad - knob : 1 + pad;
        using (var brush = new SolidBrush(on ? Theme.OnAccent : Theme.Muted)) g.FillEllipse(brush, x, y + pad, knob, knob);
        if (Focused && ShowFocusCues) using (var ring = new Pen(Theme.Ink, 1f)) using (var path = Theme.Rounded(new RectangleF(0.5f, y - 0.5f, Width - 1, h + 1), h / 2)) g.DrawPath(ring, path);
    }
}

/// A row of choices of which one is chosen.
public sealed class Segmented : Control {
    public string[] Items = new string[0];
    int chosen, over = -1;
    public event EventHandler Changed;
    public int Chosen { get { return chosen; } set { if (chosen == value) return; chosen = value; Invalidate(); } }
    public Segmented() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); Cursor = Cursors.Hand; TabStop = true; BackColor = Theme.Panel; }
    int At(int x) { return Items.Length == 0 ? -1 : Math.Max(0, Math.Min(Items.Length - 1, x * Items.Length / Math.Max(1, Width))); }
    void Pick(int index) { if (index < 0 || index == chosen) return; chosen = index; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }
    /// As a click on that part would do.
    public void Choose(int index) { Pick(index); }
    protected override void OnMouseMove(MouseEventArgs e) { int at = At(e.X); if (at != over) { over = at; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { over = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); Pick(At(e.X)); base.OnMouseDown(e); }
    protected override bool IsInputKey(Keys key) { return key == Keys.Left || key == Keys.Right || base.IsInputKey(key); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Left) Pick(Math.Max(0, chosen - 1)); else if (e.KeyCode == Keys.Right) Pick(Math.Min(Items.Length - 1, chosen + 1)); base.OnKeyDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) g.FillRectangle(back, ClientRectangle);
        Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 9f * k, Theme.Bg, Focused && ShowFocusCues ? Theme.Muted : Theme.Line);
        if (Items.Length == 0) return;
        float each = (Width - 6 * k) / Items.Length;
        using (var bold = new Font(Font, FontStyle.Bold)) using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            for (int i = 0; i < Items.Length; i++) {
                var cell = new RectangleF(3 * k + i * each + 1, 3 * k + 1, each - 2, Height - 6 * k - 2);
                if (i == chosen) Theme.Fill(g, cell, 7f * k, Theme.Raised, Theme.Accent);
                else if (i == over) Theme.Fill(g, cell, 7f * k, Theme.Panel);
                using (var brush = new SolidBrush(i == chosen ? Theme.Ink : Theme.Muted)) g.DrawString(Items[i], i == chosen ? bold : Font, brush, cell, format);
            }
    }
}

/// An entry of the side bar: an icon, a name, and a small count when there is something to show.
public sealed class NavItem : Control {
    public string Glyph = "", Title = "", Badge = "";
    bool chosen, over;
    public bool Chosen { get { return chosen; } set { if (chosen == value) return; chosen = value; Invalidate(); } }
    public NavItem() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true); Cursor = Cursors.Hand; TabStop = true; BackColor = Theme.Side; }
    public void SetBadge(string text) { if (Badge == text) return; Badge = text; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        using (var back = new SolidBrush(Theme.Side)) g.FillRectangle(back, ClientRectangle);
        if (chosen || over || (Focused && ShowFocusCues)) Theme.Fill(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 9f * k, chosen ? Theme.Panel : Color.FromArgb(28, 30, 43), Focused && ShowFocusCues ? Theme.Muted : (Color?)null);
        if (chosen) Theme.Fill(g, new RectangleF(0, Height * 0.28f, 3 * k, Height * 0.44f), 1.5f * k, Theme.Accent);
        using (var icon = new Font(Theme.Icons, Font.Size + 2.5f)) using (var bold = new Font(Font, chosen ? FontStyle.Bold : FontStyle.Regular))
        using (var ink = new SolidBrush(chosen ? Theme.Ink : Theme.Muted)) using (var accent = new SolidBrush(chosen ? Theme.Accent : Theme.Muted))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap }) {
            g.DrawString(Glyph, icon, accent, new RectangleF(13 * k, 1 * k, 30 * k, Height), format);
            g.DrawString(Title, bold, ink, new RectangleF(44 * k, 0, Width - 44 * k, Height), format);
            if (Badge.Length > 0) {
                SizeF size = g.MeasureString(Badge, Font);
                var pill = new RectangleF(Width - size.Width - 22 * k, (Height - size.Height - 2 * k) / 2, size.Width + 10 * k, size.Height + 2 * k);
                Theme.Fill(g, pill, pill.Height / 2, Theme.Raised);
                using (var brush = new SolidBrush(Theme.Ink)) g.DrawString(Badge, Font, brush, pill.X + 5 * k, pill.Y + 1 * k);
            }
        }
    }
}

/// A coloured dot followed by a line of text: the state of something at a glance.
public sealed class StatusLine : Control {
    Color dot = Theme.Busy; string line = "";
    public StatusLine() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Theme.Bg; }
    public void Set(string text, Color colour) { if (text == line && colour == dot) return; line = text; dot = colour; Invalidate(); }
    public string Line { get { return line; } }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g), size = 8 * k;
        using (var back = new SolidBrush(Parent == null ? Theme.Bg : Parent.BackColor)) g.FillRectangle(back, ClientRectangle);
        using (var halo = new SolidBrush(Color.FromArgb(50, dot))) g.FillEllipse(halo, 1 * k, (Height - size) / 2 - 3 * k, size + 6 * k, size + 6 * k);
        using (var brush = new SolidBrush(dot)) g.FillEllipse(brush, 4 * k, (Height - size) / 2, size, size);
        using (var brush = new SolidBrush(Theme.Ink)) using (var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(line, Font, brush, new RectangleF(22 * k, 0, Width - 22 * k, Height), format);
    }
}

/// One row of a RowList.
public sealed class Row { public string Glyph = "", Title = "", About = "", Tag = ""; public Color TagColour = Theme.Muted; public object Value; }

/// A list of rows with an icon, a title, a second line and an optional tag at the right; a hint is shown while it is empty.
public sealed class RowList : ListBox {
    public string EmptyTitle = "", EmptyAbout = "";
    public readonly List<Row> Rows = new List<Row>();
    public RowList() {
        DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None; IntegralHeight = false; BackColor = Theme.Panel; ForeColor = Theme.Ink; ItemHeight = 54;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Theme.DarkScroll(this);
    }
    public void Show(IEnumerable<Row> rows) {
        int keep = SelectedIndex;
        BeginUpdate(); Rows.Clear(); Rows.AddRange(rows); Items.Clear(); foreach (var row in Rows) Items.Add(row.Title); EndUpdate();
        if (keep >= 0 && keep < Items.Count) SelectedIndex = keep;
        Invalidate();
    }
    public Row Selected { get { return SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null; } }
    protected override void OnDrawItem(DrawItemEventArgs e) {
        if (e.Index < 0 || e.Index >= Rows.Count) return;
        var g = e.Graphics; Theme.Smooth(g);
        float k = Theme.Dpi(g);
        Row row = Rows[e.Index];
        bool chosen = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(Theme.Panel)) g.FillRectangle(back, e.Bounds);
        var inner = new RectangleF(e.Bounds.X + 2 * k, e.Bounds.Y + 2 * k, e.Bounds.Width - 4 * k, e.Bounds.Height - 4 * k);
        if (chosen) Theme.Fill(g, inner, 9f * k, Theme.Raised, Theme.Line);
        var tile = new RectangleF(inner.X + 8 * k, inner.Y + (inner.Height - 34 * k) / 2, 34 * k, 34 * k);
        Theme.Fill(g, tile, 9f * k, chosen ? Theme.Hover : Theme.Bg);
        using (var icon = new Font(Theme.Icons, Font.Size + 3f)) using (var accent = new SolidBrush(Theme.Accent)) using (var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(row.Glyph, icon, accent, new RectangleF(tile.X, tile.Y + 1 * k, tile.Width, tile.Height), centre);
        float left = tile.Right + 12 * k, right = inner.Right - 10 * k;
        if (row.Tag.Length > 0) {
            SizeF size = g.MeasureString(row.Tag, Font);
            var pill = new RectangleF(right - size.Width - 14 * k, inner.Y + (inner.Height - size.Height - 4 * k) / 2, size.Width + 14 * k, size.Height + 4 * k);
            Theme.Fill(g, pill, pill.Height / 2, Color.FromArgb(36, row.TagColour));
            using (var brush = new SolidBrush(row.TagColour)) g.DrawString(row.Tag, Font, brush, pill.X + 7 * k, pill.Y + 2 * k);
            right = pill.X - 8 * k;
        }
        using (var bold = new Font(Font, FontStyle.Bold)) using (var ink = new SolidBrush(Theme.Ink)) using (var muted = new SolidBrush(Theme.Muted))
        using (var one = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }) using (var path = new StringFormat { Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap }) {
            float line = bold.GetHeight(g), top = inner.Y + (inner.Height - line * 2 - 2 * k) / 2;
            g.DrawString(row.Title, bold, ink, new RectangleF(left, top, right - left, line), one);
            g.DrawString(row.About, Font, muted, new RectangleF(left, top + line + 2 * k, right - left, line), path);
        }
    }
    const int Paint = 0x000F, EraseBackground = 0x0014;
    protected override void WndProc(ref Message m) {
        base.WndProc(ref m);
        // An empty list says what belongs in it.
        if (m.Msg == Paint && Items.Count == 0 && EmptyTitle.Length > 0) using (var g = CreateGraphics()) {
            Theme.Smooth(g);
            using (var back = new SolidBrush(Theme.Panel)) g.FillRectangle(back, ClientRectangle);
            using (var bold = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold)) using (var ink = new SolidBrush(Theme.Ink)) using (var muted = new SolidBrush(Theme.Muted))
            using (var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) {
                float mid = Height / 2f, line = bold.GetHeight(g);
                g.DrawString(EmptyTitle, bold, ink, new RectangleF(0, mid - line * 1.5f, Width, line * 1.4f), centre);
                g.DrawString(EmptyAbout, Font, muted, new RectangleF(Width * 0.1f, mid, Width * 0.8f, line * 2.6f), centre);
            }
        }
    }
}

/// A title, a line of explanation and a control at the right: one setting.
public sealed class SettingRow : Panel {
    readonly Control side;
    public SettingRow(string title, string about, Control control, int controlWidth, int controlHeight) {
        BackColor = Theme.Panel; Height = 60; side = control;
        var name = Theme.Label(title, Theme.Ink, Theme.Panel, 9.5f, true); name.SetBounds(18, 11, 360, 20);
        var text = Theme.Label(about, Theme.Muted, Theme.Panel, 8.5f); text.SetBounds(18, 32, 370, 18);
        Controls.Add(name); Controls.Add(text);
        if (control != null) { control.SetBounds(0, 0, controlWidth, controlHeight); Controls.Add(control); control.BringToFront(); }
    }
    // The control keeps to the right edge, also after the window was scaled for the screen.
    protected override void OnLayout(LayoutEventArgs e) {
        base.OnLayout(e);
        if (side == null) return;
        var wanted = new Point(Width - side.Width - Height * 18 / 60, (Height - side.Height) / 2);
        if (side.Location != wanted) side.Location = wanted;
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        using (var pen = new Pen(Theme.Line)) e.Graphics.DrawLine(pen, 18 * e.Graphics.DpiX / 96f, Height - 1, Width - 18 * e.Graphics.DpiX / 96f, Height - 1);
    }
}
}

using System.Diagnostics;
using Bfm.Core;
using Bfm.Interop;
using Microsoft.Win32;

namespace Bfm.UI;

/// <summary>The strip of battery chips laid over the empty left part of the taskbar.</summary>
internal sealed class BarForm : Form
{
    // Pixels of exactly this colour are transparent and click-through; close to the dark taskbar so
    // anti-aliased chip edges blend in.
    static readonly Color Key = Color.FromArgb(30, 30, 31);

    const int WmDeviceChange = 0x0219, WmDisplayChange = 0x007E, WmSettingChange = 0x001A, WmDpiChanged = 0x02E0;
    const int DbtDevNodesChanged = 0x0007;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly DeviceMonitor _monitor;
    readonly Settings _settings;
    readonly List<(Rectangle Rect, DeviceStatus Dev)> _chips = new();
    readonly ToolTip _tip = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
    readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 60_000 };
    readonly System.Windows.Forms.Timer _zOrderTimer = new() { Interval = 5_000 };
    readonly System.Windows.Forms.Timer _deviceChangeTimer = new() { Interval = 3_000 };
    readonly User32.WinEventProc _foregroundProc;
    readonly uint _taskbarCreatedMsg;

    IReadOnlyList<DeviceStatus> _devices = Array.Empty<DeviceStatus>();
    Font _font = null!, _iconFont = null!, _boltFont = null!;
    float _scale = 1f;
    IntPtr _hook;
    bool _fullscreenHidden, _trimmedOnce;
    Point _downAt;
    int _downOffset;
    bool _mouseDown, _dragging;
    int _hover = -1;
    FlyoutForm? _flyout;
    NotifyIcon? _notify;

    public BarForm(DeviceMonitor monitor, Settings settings)
    {
        _monitor = monitor;
        _settings = settings;

        Text = "BFM";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Key;
        TransparencyKey = Key;
        DoubleBuffered = true;

        ApplyScale(DeviceDpi);
        _devices = _monitor.Snapshot();
        LayoutChips();

        _monitor.Changed += () => BeginInvokeSafe(OnDevicesChanged);
        _monitor.LowBattery += d => BeginInvokeSafe(() => ShowLowBattery(d));

        _pollTimer.Tick += (_, _) => _ = _monitor.RefreshAsync(false);
        _zOrderTimer.Tick += (_, _) => KeepOnTop();
        _deviceChangeTimer.Tick += (_, _) =>
        {
            _deviceChangeTimer.Stop();
            _ = _monitor.RefreshAsync(true);
        };

        _foregroundProc = OnForegroundChanged;
        _taskbarCreatedMsg = User32.RegisterWindowMessageW("TaskbarCreated");
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExToolWindow = 0x80, WsExTopmost = 0x8, WsExNoActivate = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow | WsExTopmost | WsExNoActivate;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hook = User32.SetWinEventHook(User32.EventSystemForeground, User32.EventSystemForeground, IntPtr.Zero,
            _foregroundProc, 0, 0, User32.WineventOutOfContext);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionOnTaskbar();
        _pollTimer.Start();
        _zOrderTimer.Start();
        _ = _monitor.RefreshAsync(true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_hook != IntPtr.Zero) User32.UnhookWinEvent(_hook);
        _notify?.Dispose();
        base.OnFormClosed(e);
    }

    void BeginInvokeSafe(Action a)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
    }

    void ApplyScale(int dpi)
    {
        _scale = dpi / 96f;
        _font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        _iconFont = new Font(Theme.IconFontName, 10.5f, FontStyle.Regular, GraphicsUnit.Point);
        _boltFont = new Font(Theme.IconFontName, 7.5f, FontStyle.Regular, GraphicsUnit.Point);
    }

    int S(float v) => (int)Math.Round(v * _scale);

    IEnumerable<DeviceStatus> VisibleDevices() =>
        _devices.Where(d => !_settings.Hidden.Contains(d.Id) && (d.Connected || _settings.ShowDisconnected));

    void OnDevicesChanged()
    {
        _devices = _monitor.Snapshot();
        if (!_trimmedOnce)
        {
            _trimmedOnce = true;
            BeginInvokeSafe(Kernel32.TrimWorkingSet);
        }
        LayoutChips();
        PositionOnTaskbar();
        Invalidate();
        _flyout?.UpdateDevice(_devices.FirstOrDefault(d => d.Id == _flyout.DeviceId));
    }

    // ---------- layout & painting ----------

    void LayoutChips()
    {
        _chips.Clear();
        int x = S(2), h = S(30), y = S(2);
        int pad = S(9), iconW = S(16), gap = S(4);
        var visible = VisibleDevices().ToList();

        if (visible.Count == 0)
        {
            var placeholder = new DeviceStatus { Id = "", Name = "BFM — no devices yet", Kind = DeviceKind.Unknown };
            visible.Add(placeholder);
        }

        foreach (var d in visible)
        {
            var text = ChipText(d);
            var textW = TextRenderer.MeasureText(text, _font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int w = pad + iconW + S(5) + textW + (d.Charging == true ? S(10) : 0) + pad;
            _chips.Add((new Rectangle(x, y, w, h), d));
            x += w + gap;
        }
        ClientSize = new Size(x - gap + S(2), h + S(4));
    }

    static string ChipText(DeviceStatus d) => d.Id.Length == 0 ? "--" : d.Battery is int b ? $"{b}%" : "--";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        for (int i = 0; i < _chips.Count; i++)
        {
            var (r, d) = _chips[i];
            bool dim = !d.Connected;
            using (var bg = new SolidBrush(i == _hover ? Theme.ChipHover : Theme.Chip))
                Theme.Rounded(g, bg, r, S(6));

            var fg = dim ? Theme.TextDim : Theme.Text;
            int pad = S(9), iconW = S(16);
            var iconRect = new Rectangle(r.X + pad, r.Y, iconW, r.Height - S(3));
            TextRenderer.DrawText(g, Theme.Glyph(d.Kind), _iconFont, iconRect, fg, Theme.Chip,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var textRect = new Rectangle(iconRect.Right + S(5), r.Y, r.Right - iconRect.Right - S(5) - pad, r.Height - S(3));
            TextRenderer.DrawText(g, ChipText(d), _font, textRect, fg, Theme.Chip,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (d.Charging == true)
            {
                var boltRect = new Rectangle(r.Right - pad - S(10), r.Y, S(10), r.Height - S(3));
                TextRenderer.DrawText(g, Theme.Bolt, _boltFont, boltRect, Theme.Mid, Theme.Chip,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (d.Id.Length > 0)
            {
                var track = new RectangleF(r.X + pad, r.Bottom - S(5), r.Width - pad * 2, S(2));
                using var trackBrush = new SolidBrush(Theme.Track);
                g.FillRectangle(trackBrush, track);
                if (d.Battery is int level)
                {
                    using var fill = new SolidBrush(Theme.ForLevel(level, dim));
                    g.FillRectangle(fill, track.X, track.Y, track.Width * level / 100f, track.Height);
                }
            }
        }
    }

    // ---------- placement ----------

    void PositionOnTaskbar()
    {
        if (User32.GetTaskbar() is not { } tb) return;
        var r = tb.Rect;
        if (tb.Edge is 1 or 3)
        {
            int minX = r.Left + S(6);
            int maxX = Math.Max(minX, r.Right - Width - S(6));
            int x = Math.Clamp(minX + _settings.OffsetX, minX, maxX);
            int y = r.Top + (r.Height - Height) / 2;
            Location = new Point(x, y);
        }
        else
        {
            Location = new Point(r.Left + (r.Width - Width) / 2, r.Top + S(6) + _settings.OffsetX);
        }
        KeepOnTop();
    }

    void KeepOnTop()
    {
        if (!Visible) return;
        User32.SetWindowPos(Handle, User32.HwndTopmost, 0, 0, 0, 0,
            User32.SwpNoMove | User32.SwpNoSize | User32.SwpNoActivate | User32.SwpNoOwnerZOrder);
    }

    void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || hwnd == Handle || (_flyout is not null && hwnd == _flyout.Handle)) return;
        var cls = User32.ClassName(hwnd);

        bool fullscreen = false;
        if (cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            && User32.GetWindowRect(hwnd, out var wr))
        {
            var screen = Screen.FromHandle(hwnd).Bounds;
            var rect = wr.ToRectangle();
            fullscreen = rect.Left <= screen.Left && rect.Top <= screen.Top
                         && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
        }

        if (fullscreen && Visible)
        {
            _fullscreenHidden = true;
            _flyout?.Close();
            Hide();
        }
        else if (!fullscreen && _fullscreenHidden)
        {
            _fullscreenHidden = false;
            Show();
            PositionOnTaskbar();
        }
        else if (cls == "Shell_TrayWnd")
        {
            KeepOnTop();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDeviceChange && (int)m.WParam == DbtDevNodesChanged)
        {
            _deviceChangeTimer.Stop();
            _deviceChangeTimer.Start();
        }
        else if (m.Msg is WmDisplayChange or WmSettingChange || (m.Msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0))
        {
            BeginInvokeSafe(PositionOnTaskbar);
        }
        else if (m.Msg == WmDpiChanged)
        {
            base.WndProc(ref m);
            ApplyScale(DeviceDpi);
            LayoutChips();
            PositionOnTaskbar();
            Invalidate();
            return;
        }
        base.WndProc(ref m);
    }

    // ---------- mouse ----------

    int HitTest(Point p) => _chips.FindIndex(c => c.Rect.Contains(p));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _mouseDown = true;
        _dragging = false;
        _downAt = Cursor.Position;
        _downOffset = _settings.OffsetX;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_mouseDown)
        {
            int dx = Cursor.Position.X - _downAt.X;
            if (!_dragging && Math.Abs(dx) > S(5))
            {
                _dragging = true;
                _tip.Hide(this);
            }
            if (_dragging)
            {
                _settings.OffsetX = Math.Max(0, _downOffset + dx);
                PositionOnTaskbar();
                return;
            }
        }

        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Invalidate();
            _tip.Hide(this);
            if (hit >= 0) _tip.SetToolTip(this, TooltipFor(_chips[hit].Dev));
            else _tip.SetToolTip(this, null);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1)
        {
            _hover = -1;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            ShowMenu();
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        bool wasDrag = _dragging;
        _mouseDown = _dragging = false;
        if (wasDrag)
        {
            _settings.Save();
            return;
        }

        int hit = HitTest(e.Location);
        if (hit < 0) return;
        var (rect, dev) = _chips[hit];
        if (dev.Id.Length == 0)
        {
            ShowMenu();
            return;
        }
        ToggleFlyout(dev, RectangleToScreen(rect));
    }

    static string TooltipFor(DeviceStatus d)
    {
        if (d.Id.Length == 0) return "Right-click for options";
        var state = d.Connected ? "Connected" : "Not connected";
        var battery = d.Battery is int b ? $"{b}%" + (d.Charging == true ? " (charging)" : "") : "battery unknown";
        var detail = d.Detail is null ? "" : "\n" + d.Detail;
        var updated = d.Battery is null ? "" : $"\nUpdated {Theme.Ago(d.Updated)}";
        var note = d.Experimental ? "\nExperimental reading" : "";
        return $"{d.Name}\n{battery} · {d.TransportLabel} · {state}{detail}{updated}{note}";
    }

    void ToggleFlyout(DeviceStatus dev, Rectangle anchor)
    {
        if (_flyout is not null)
        {
            var same = _flyout.DeviceId == dev.Id;
            _flyout.Close();
            if (same) return;
        }
        _tip.Hide(this);
        _flyout = new FlyoutForm(dev, anchor, this);
        _flyout.FormClosed += (_, _) =>
        {
            _flyout = null;
            Kernel32.TrimWorkingSet();
        };
        _flyout.Show();
        _flyout.Activate();
    }

    internal void HideDevice(string id)
    {
        _settings.Hidden.Add(id);
        _settings.Save();
        OnDevicesChanged();
    }

    internal void RefreshSoon(int delayMs)
    {
        var t = new System.Windows.Forms.Timer { Interval = delayMs };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            _ = _monitor.RefreshAsync(true);
        };
        t.Start();
    }

    // ---------- context menu ----------

    void ShowMenu()
    {
        _tip.Hide(this);
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
            ForeColor = Theme.Text,
            ShowImageMargin = false,
            ShowCheckMargin = true,
        };

        menu.Items.Add("Refresh now", null, (_, _) => _ = _monitor.RefreshAsync(true));
        menu.Items.Add(new ToolStripSeparator());

        var devices = new ToolStripMenuItem("Show on bar");
        foreach (var d in _devices)
        {
            var item = new ToolStripMenuItem(d.Name) { Checked = !_settings.Hidden.Contains(d.Id), CheckOnClick = true, ForeColor = Theme.Text };
            item.CheckedChanged += (_, _) =>
            {
                if (item.Checked) _settings.Hidden.Remove(d.Id); else _settings.Hidden.Add(d.Id);
                _settings.Save();
                OnDevicesChanged();
            };
            devices.DropDownItems.Add(item);
        }
        if (devices.DropDownItems.Count == 0) devices.Enabled = false;
        menu.Items.Add(devices);

        var showDisconnected = new ToolStripMenuItem("Show disconnected devices") { Checked = _settings.ShowDisconnected, CheckOnClick = true };
        showDisconnected.CheckedChanged += (_, _) =>
        {
            _settings.ShowDisconnected = showDisconnected.Checked;
            _settings.Save();
            OnDevicesChanged();
        };
        menu.Items.Add(showDisconnected);

        menu.Items.Add(Choice("Low-battery alert", new[] { 0, 10, 15, 20, 30 }, v => v == 0 ? "Off" : $"Below {v}%",
            () => _settings.AlertThreshold, v => _settings.AlertThreshold = v));
        menu.Items.Add(Choice("Check USB dongles every", new[] { 1, 2, 5, 10, 15 }, v => $"{v} min",
            () => _settings.DonglePollMinutes, v => _settings.DonglePollMinutes = v));

        menu.Items.Add("Reset position", null, (_, _) =>
        {
            _settings.OffsetX = 0;
            _settings.Save();
            PositionOnTaskbar();
        });

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Bluetooth settings", null, (_, _) => OpenUri("ms-settings:bluetooth"));
        menu.Items.Add("Exit", null, (_, _) => Close());

        foreach (ToolStripItem i in menu.Items) i.ForeColor = Theme.Text;
        menu.Closed += (_, _) => BeginInvokeSafe(menu.Dispose);

        User32.SetForegroundWindow(Handle);
        menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveRight);
    }

    ToolStripMenuItem Choice(string title, int[] values, Func<int, string> label, Func<int> get, Action<int> set)
    {
        var parent = new ToolStripMenuItem(title);
        foreach (var v in values)
        {
            var item = new ToolStripMenuItem(label(v)) { Checked = get() == v, ForeColor = Theme.Text };
            item.Click += (_, _) =>
            {
                set(v);
                _settings.Save();
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("BFM") is string;
    }

    static void SetStartup(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) key.SetValue("BFM", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("BFM", false);
    }

    internal static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("open failed: " + ex.Message);
        }
    }

    // ---------- alerts ----------

    void ShowLowBattery(DeviceStatus d)
    {
        _notify ??= new NotifyIcon { Icon = SystemIcons.Warning, Text = "BFM" };
        _notify.Visible = true;
        _notify.ShowBalloonTip(8000, "Low battery", $"{d.Name} is at {d.Battery}%", ToolTipIcon.Warning);
        var t = new System.Windows.Forms.Timer { Interval = 15_000 };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            if (_notify is not null) _notify.Visible = false;
        };
        t.Start();
    }
}

using Bfm.Core;
using Bfm.Interop;

namespace Bfm.UI;

/// <summary>Small panel that opens above a chip with details and quick controls.</summary>
internal sealed class FlyoutForm : Form
{
    readonly BarForm _bar;
    readonly Rectangle _anchor;
    readonly Label _title, _level, _info;
    readonly LevelBar _levelBar;
    readonly FlowLayoutPanel _body;
    DeviceStatus _device;

    public string DeviceId => _device.Id;

    public FlyoutForm(DeviceStatus device, Rectangle anchor, BarForm bar)
    {
        _device = device;
        _anchor = anchor;
        _bar = bar;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;

        int width = Scale(288);
        _body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(Scale(14), Scale(12), Scale(14), Scale(12)),
            Location = Point.Empty,
            MaximumSize = new Size(width, 0),
            MinimumSize = new Size(width, 0),
        };
        Controls.Add(_body);

        int inner = width - Scale(28);
        _title = new Label { AutoSize = false, Width = inner, Height = Scale(24), Font = new Font("Segoe UI Semibold", 10.5f), TextAlign = ContentAlignment.MiddleLeft };
        _levelBar = new LevelBar { Width = inner - Scale(48), Height = Scale(8), Margin = new Padding(0, Scale(8), Scale(6), 0) };
        _level = new Label { AutoSize = false, Width = Scale(42), Height = Scale(22), TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI Semibold", 10f) };
        var levelRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, Scale(4), 0, 0) };
        levelRow.Controls.Add(_levelBar);
        levelRow.Controls.Add(_level);
        _info = new Label { AutoSize = false, Width = inner, Height = Scale(device.Detail is null ? 36 : 54), ForeColor = Theme.TextDim, Margin = new Padding(0, Scale(4), 0, Scale(6)) };

        _body.Controls.Add(_title);
        _body.Controls.Add(levelRow);
        _body.Controls.Add(_info);
        AddControls(inner);

        UpdateDevice(device);
        ClientSize = _body.PreferredSize;
        PlaceAboveAnchor();
    }

    int Scale(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        User32.ApplyDarkRoundedFrame(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    void PlaceAboveAnchor()
    {
        var area = Screen.FromRectangle(_anchor).WorkingArea;
        int x = Math.Clamp(_anchor.Left + _anchor.Width / 2 - Width / 2, area.Left + Scale(8), area.Right - Width - Scale(8));
        int y = Math.Min(_anchor.Top, area.Bottom) - Height - Scale(10);
        Location = new Point(x, Math.Max(area.Top, y));
    }

    public void UpdateDevice(DeviceStatus? device)
    {
        if (device is null) return;
        _device = device;
        _title.Text = device.Name;
        _level.Text = device.Battery is int b ? $"{b}%" : "--";
        _level.ForeColor = device.Charging == true ? Theme.Mid : Theme.Text;
        _levelBar.Level = device.Battery;
        _levelBar.Dim = !device.Connected;
        var state = device.Connected ? (device.Charging == true ? "Charging" : "Connected") : "Not connected";
        var updated = device.Battery is null ? "No battery reading yet" : $"Updated {Theme.Ago(device.Updated)}";
        var detail = device.Detail is null ? "" : device.Detail + "\n";
        _info.Text = $"{detail}{device.TransportLabel} · {state}\n{updated}{(device.Experimental ? " · experimental" : "")}";
    }

    void AddControls(int width)
    {
        var output = CoreAudio.FindForDevice(_device.Name, render: true);
        var mic = CoreAudio.FindForDevice(_device.Name, render: false);

        if (output is not null)
        {
            bool isDefault = CoreAudio.GetDefaultId(render: true) == output.Id;
            var setDefault = MakeButton(isDefault ? "Default output ✓" : "Set as default output", width);
            setDefault.Enabled = !isDefault;
            setDefault.Click += (_, _) =>
            {
                if (CoreAudio.SetDefault(output.Id))
                {
                    setDefault.Text = "Default output ✓";
                    setDefault.Enabled = false;
                }
            };
            _body.Controls.Add(setDefault);

            var volLabel = new Label { Text = "Volume", AutoSize = true, ForeColor = Theme.TextDim, Margin = new Padding(0, Scale(8), 0, 0) };
            var volume = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                TickStyle = TickStyle.None,
                Width = width,
                BackColor = Theme.Panel,
                Value = (int)Math.Round((CoreAudio.GetVolume(output.Id) ?? 0f) * 100),
            };
            volume.ValueChanged += (_, _) => CoreAudio.SetVolume(output.Id, volume.Value / 100f);
            _body.Controls.Add(volLabel);
            _body.Controls.Add(volume);
        }

        if (mic is not null)
        {
            bool muted = CoreAudio.GetMute(mic.Id) ?? false;
            var muteButton = MakeButton(muted ? "Unmute microphone" : "Mute microphone", width);
            muteButton.Click += (_, _) =>
            {
                muted = !muted;
                CoreAudio.SetMute(mic.Id, muted);
                muteButton.Text = muted ? "Unmute microphone" : "Mute microphone";
            };
            _body.Controls.Add(muteButton);
        }

        if (_device.Transport == Transport.Bluetooth && _device.BluetoothAddress is { } addr
            && _device.Kind is DeviceKind.Headset or DeviceKind.Earbuds && BluetoothAudioControl.HasAudioFilter(addr))
        {
            var connect = MakeButton(_device.Connected ? "Disconnect" : "Connect", width);
            connect.Click += async (_, _) =>
            {
                bool wasConnected = _device.Connected;
                connect.Enabled = false;
                connect.Text = wasConnected ? "Disconnecting…" : "Connecting…";
                bool ok = await Task.Run(() => wasConnected ? BluetoothAudioControl.Disconnect(addr) : BluetoothAudioControl.Connect(addr));
                if (IsDisposed) return;
                connect.Text = ok ? (wasConnected ? "Disconnect requested" : "Connect requested") : "Failed — try Bluetooth settings";
                _bar.RefreshSoon(4000);
            };
            _body.Controls.Add(connect);
        }

        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, Scale(8), 0, 0) };
        if (_device.Transport != Transport.Dongle)
            links.Controls.Add(MakeLink("Bluetooth settings", () => BarForm.OpenUri("ms-settings:bluetooth")));
        if (output is not null || mic is not null)
            links.Controls.Add(MakeLink("Sound settings", () => BarForm.OpenUri("ms-settings:sound")));
        links.Controls.Add(MakeLink("Hide from bar", () =>
        {
            Close();
            _bar.HideDevice(_device.Id);
        }));
        _body.Controls.Add(links);
    }

    Button MakeButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = Scale(32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Button,
            ForeColor = Theme.Text,
            Margin = new Padding(0, Scale(4), 0, 0),
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.ChipHover;
        return b;
    }

    LinkLabel MakeLink(string text, Action onClick)
    {
        var l = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            LinkColor = Color.FromArgb(118, 185, 237),
            ActiveLinkColor = Color.FromArgb(150, 205, 245),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 0, Scale(12), 0),
        };
        l.LinkClicked += (_, _) => onClick();
        return l;
    }

    sealed class LevelBar : Control
    {
        int? _level;
        bool _dim;

        public LevelBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Panel;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int? Level { get => _level; set { _level = value; Invalidate(); } }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Dim { get => _dim; set { _dim = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float radius = Height / 2f;
            using (var track = new SolidBrush(Theme.Track))
                Theme.Rounded(g, track, new RectangleF(0, 0, Width - 1, Height - 1), radius);
            if (_level is int l && l > 0)
            {
                using var fill = new SolidBrush(Theme.ForLevel(l, _dim));
                Theme.Rounded(g, fill, new RectangleF(0, 0, Math.Max(Height, (Width - 1) * l / 100f), Height - 1), radius);
            }
        }
    }
}

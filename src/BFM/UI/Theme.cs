using Bfm.Core;

namespace Bfm.UI;

internal static class Theme
{
    public static readonly Color Chip = Color.FromArgb(48, 48, 48);
    public static readonly Color ChipHover = Color.FromArgb(62, 62, 62);
    public static readonly Color Track = Color.FromArgb(75, 75, 75);
    public static readonly Color Text = Color.FromArgb(240, 240, 240);
    public static readonly Color TextDim = Color.FromArgb(135, 135, 135);
    public static readonly Color Panel = Color.FromArgb(36, 36, 36);
    public static readonly Color Button = Color.FromArgb(54, 54, 54);
    public static readonly Color Border = Color.FromArgb(72, 72, 72);
    public static readonly Color Good = Color.FromArgb(108, 203, 95);
    public static readonly Color Mid = Color.FromArgb(252, 185, 0);
    public static readonly Color Low = Color.FromArgb(255, 99, 97);
    public static readonly Color Dim = Color.FromArgb(105, 105, 105);

    public static Color ForLevel(int? level, bool dim) =>
        dim || level is null ? Dim : level > 50 ? Good : level >= 20 ? Mid : Low;

    public static readonly string IconFontName =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public const string Bolt = "\uE945";

    public static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Headset or DeviceKind.Earbuds => "\uE7F6",
        DeviceKind.Keyboard => "\uE765",
        DeviceKind.Mouse => "\uE962",
        DeviceKind.Controller => "\uE7FC",
        DeviceKind.Phone => "\uE8EA",
        _ => "\uE702",
    };

    public static string Ago(DateTime? t)
    {
        if (t is null) return "never";
        var d = DateTime.Now - t.Value;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return t.Value.ToString("MMM d, HH:mm");
    }

    public static void Rounded(Graphics g, Brush brush, RectangleF r, float radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

internal sealed class DarkMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Panel;
    public override Color ImageMarginGradientBegin => Theme.Panel;
    public override Color ImageMarginGradientMiddle => Theme.Panel;
    public override Color ImageMarginGradientEnd => Theme.Panel;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Theme.ChipHover;
    public override Color MenuItemSelected => Theme.ChipHover;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
    public override Color CheckBackground => Theme.Button;
    public override Color CheckSelectedBackground => Theme.ChipHover;
    public override Color CheckPressedBackground => Theme.ChipHover;
}

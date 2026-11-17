using Bfm.Interop;

namespace Bfm.UI;

/// <summary>Minimal dark single-line text prompt.</summary>
internal static class InputBox
{
    public static string? Show(IWin32Window? owner, string title, string prompt, string value)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 9f),
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(340, 118),
        };
        var label = new Label { Text = prompt, AutoSize = false, Bounds = new Rectangle(14, 12, 312, 20) };
        var box = new TextBox
        {
            Text = value,
            Bounds = new Rectangle(14, 36, 312, 24),
            BackColor = Theme.Button,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var ok = SettingsForm.MakeButton("OK", 90);
        ok.Location = new Point(140, 76);
        ok.DialogResult = DialogResult.OK;
        var cancel = SettingsForm.MakeButton("Cancel", 90);
        cancel.Location = new Point(236, 76);
        cancel.DialogResult = DialogResult.Cancel;
        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.HandleCreated += (_, _) => User32.ApplyDarkRoundedFrame(form.Handle);
        form.Shown += (_, _) =>
        {
            form.Activate();
            box.SelectAll();
            box.Focus();
        };
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}

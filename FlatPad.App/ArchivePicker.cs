using FlatPad.Core.Game;

namespace FlatPad.App;

/// <summary>
/// Asks which renamed-aside archive to restore, when there is more than one.
/// </summary>
/// <remarks>
/// A game update downloads a fresh <c>content.kspkg</c>, so an install accumulates archives from
/// different versions and restoring the wrong one puts stale content under a newer build. The tool
/// must not guess — but "the button is greyed out, go rename a 68 GB file in Explorer" is a dead
/// end, and doing it by hand is more error-prone than choosing from a list that shows the sizes and
/// dates. So: refuse to guess, offer to ask.
/// </remarks>
internal sealed class ArchivePicker : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    public string? Chosen => _list.SelectedIndex >= 0 ? _paths[_list.SelectedIndex] : null;

    private readonly List<string> _paths;

    public ArchivePicker(IReadOnlyList<string> archives)
    {
        _paths = [.. archives];

        Text = "Which archive should be restored?";
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 300);

        foreach (string path in _paths)
        {
            var info = new FileInfo(path);
            _list.Items.Add($"{info.Name}   —   {GameArchive.Bytes(info.Length)}, "
                            + $"{info.LastWriteTime:d MMM yyyy}");
        }

        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;      // newest first, which normally matches the current build

        var blurb = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 78,
            Padding = new Padding(0, 0, 0, 8),
            Text = "This install has more than one archive, because a game update downloads a fresh "
                   + "one. The NEWEST normally matches your current game version; restoring an older "
                   + "one puts stale content under a newer build.\r\n\r\n"
                   + "Your unpacked files are left alone either way.",
        };

        var ok = new Button { Text = "Restore", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.AddRange([cancel, ok]);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        root.Controls.Add(_list);
        root.Controls.Add(blurb);
        root.Controls.Add(buttons);
        Controls.Add(root);

        AcceptButton = ok;
        CancelButton = cancel;
        _list.DoubleClick += (_, _) => { DialogResult = DialogResult.OK; Close(); };
    }
}

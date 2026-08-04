using System.Diagnostics;

using FlatPad.Core.FlatPad;
using FlatPad.Core.Game;

namespace FlatPad.App;

/// <summary>
/// The whole UI: pick the game, see what state it is in, and change that state.
/// </summary>
/// <remarks>
/// Deliberately a thin shell. Everything it calls lives in <c>FlatPad.Core</c> and is covered by
/// tests and by a byte-for-byte comparison against the reference implementation, so there is no
/// logic here worth hiding behind a button.
///
/// Two rules the layout exists to serve: nothing expensive or irreversible happens without being
/// shown its cost first, and every long operation runs off the UI thread with a Cancel that works.
/// </remarks>
internal sealed class MainForm : Form
{
    private readonly TextBox _gamePath = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = "Browse…", AutoSize = true };
    private readonly Label _detected = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    // A two-column grid rather than one padded label: colons lined up with spaces only line up in a
    // monospace font, and the rest of the window is not monospace.
    private readonly TableLayoutPanel _statusGrid = new()
    {
        ColumnCount = 2, RowCount = 3, AutoSize = true, Dock = DockStyle.Top,
        AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 6, 0, 6),
    };

    private readonly Label _archiveValue = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _installedValue = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _diskValue = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _statusMessage = new()
    {
        AutoSize = true, Padding = new Padding(0, 6, 0, 6), Visible = false,
    };
    private readonly Label _warning = new()
    {
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        ForeColor = Color.FromArgb(150, 90, 0),
        Padding = new Padding(0, 0, 0, 6),
    };

    private readonly Button _unpack = new() { Text = "Unpack game", AutoSize = true };
    private readonly Button _revert = new() { Text = "Revert to packed", AutoSize = true };
    private readonly Button _install = new() { Text = "Install Flat Pad", AutoSize = true };
    private readonly Button _uninstall = new() { Text = "Uninstall Flat Pad", AutoSize = true };
    private readonly Button _verify = new() { Text = "Verify", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };

    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 18, Visible = false };
    private readonly Label _progressText = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    private readonly CheckBox _showLog = new() { Text = "Show log", AutoSize = true };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
        BackColor = Color.White,
        Font = new Font(FontFamily.GenericMonospace, 8.5f),
        Visible = false,
    };

    private readonly ToolTip _tips = new();

    private CancellationTokenSource? _cancellation;

    public MainForm()
    {
        Text = "Assetto Corsa EVO — Flat Pad Installer";
        Font = SystemFonts.MessageBoxFont ?? Font;
        // Wide enough that the action row never wraps — the longest labels are state-dependent
        // ("Reinstall Flat Pad" / "Uninstall Flat Pad"), so it has to fit the worst case.
        MinimumSize = new Size(680, 300);
        AutoSizeMode = AutoSizeMode.GrowOnly;
        Size = new Size(680, 380);
        StartPosition = FormStartPosition.CenterScreen;

        Controls.Add(BuildLayout());

        _browse.Click += (_, _) => Browse();
        _gamePath.TextChanged += (_, _) => RefreshState();
        _showLog.CheckedChanged += (_, _) => ToggleLog();
        _cancel.Click += (_, _) => _cancellation?.Cancel();

        _unpack.Click += async (_, _) => await UnpackAsync();
        _revert.Click += (_, _) => Revert();
        _install.Click += async (_, _) => await RunAsync("Installing Flat Pad",
            log => new Installer(GameRoot, log).Install());
        _uninstall.Click += async (_, _) => await RunAsync("Removing Flat Pad",
            log => new Installer(GameRoot, log).Uninstall());
        _verify.Click += async (_, _) => await VerifyAsync();

        string? found = GameLocator.Find();
        _gamePath.Text = found ?? "";
        _detected.Text = found is not null
            ? "auto-detected from Steam"
            : "Steam install not found — browse to the game folder";
        RefreshState();
    }

    private string GameRoot => _gamePath.Text.Trim();

    private Control BuildLayout()
    {
        var pathRow = new TableLayoutPanel
        {
            ColumnCount = 3, RowCount = 1, AutoSize = true, Dock = DockStyle.Top,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.Controls.Add(new Label { Text = "Game", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        pathRow.Controls.Add(_gamePath, 1, 0);
        pathRow.Controls.Add(_browse, 2, 0);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 4) };
        buttons.Controls.AddRange([_unpack, _revert, _install, _uninstall, _verify, _cancel]);

        var progressRow = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        progressRow.Controls.Add(_progress, 0, 0);
        progressRow.Controls.Add(_progressText, 0, 1);

        _statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        string[] captions = ["Game archive", "Flat Pad", "Disk"];
        Label[] values = [_archiveValue, _installedValue, _diskValue];
        for (int row = 0; row < captions.Length; row++)
        {
            _statusGrid.Controls.Add(new Label
            {
                Text = captions[row], AutoSize = true, Anchor = AnchorStyles.Left,
                ForeColor = SystemColors.GrayText, Margin = new Padding(0, 3, 18, 3),
            }, 0, row);
            values[row].Margin = new Padding(0, 3, 0, 3);
            _statusGrid.Controls.Add(values[row], 1, row);
        }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(12),
        };
        for (int i = 0; i < 7; i++)
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(pathRow, 0, 0);
        root.Controls.Add(_detected, 0, 1);
        root.Controls.Add(_statusMessage, 0, 2);
        root.Controls.Add(_statusGrid, 0, 3);
        root.Controls.Add(_warning, 0, 4);
        root.Controls.Add(buttons, 0, 5);
        root.Controls.Add(progressRow, 0, 6);
        root.Controls.Add(_log, 0, 7);

        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(12, 0, 12, 8) };
        bottom.Controls.Add(_showLog);

        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(root);
        host.Controls.Add(bottom);
        return host;
    }

    // ------------------------------------------------------------------ state

    private void RefreshState()
    {
        if (!GameLocator.LooksLikeGame(GameRoot))
        {
            _statusMessage.Text = GameRoot.Length == 0
                ? "No game folder selected."
                : "That folder does not contain AssettoCorsaEVO.exe.";
            _statusMessage.Visible = true;
            _statusGrid.Visible = false;
            _warning.Text = "";
            SetEnabled(unpack: false, revert: false, install: false, uninstall: false, verify: false);
            return;
        }

        _statusMessage.Visible = false;
        _statusGrid.Visible = true;

        GameArchiveState archive = GameArchive.Detect(GameRoot);
        FlatPadState flatPad = Verifier.DetectState(GameRoot);
        bool installed = flatPad == FlatPadState.Installed;

        string archiveLine = archive.Mode switch
        {
            ArchiveMode.Packed =>
                $"PACKED ({Path.GetFileName(archive.LivePackage)}, {GameArchive.Bytes(archive.ArchiveBytes)})",
            ArchiveMode.Unpacked => "UNPACKED — loose content, so modded tracks load",
            _ => "unrecognised",
        };

        string diskLine = $"{GameArchive.Bytes(archive.FreeBytes)} free";
        if (archive.Mode == ArchiveMode.Packed)
        {
            diskLine += $" — unpacking needs about {GameArchive.Bytes(archive.RequiredBytes)}"
                        + " (the archive is kept, so budget roughly twice its size)";
        }

        _archiveValue.Text = archiveLine;
        _installedValue.Text = flatPad switch
        {
            FlatPadState.Installed => "installed",
            FlatPadState.FilesPresentButNotRegistered =>
                "files present, but NOT registered — it will not appear in any track list",
            _ => "not installed",
        };
        _installedValue.ForeColor = flatPad == FlatPadState.FilesPresentButNotRegistered
            ? Color.FromArgb(150, 90, 0)
            : SystemColors.ControlText;
        _diskValue.Text = diskLine;
        _warning.Text = BuildWarning(archive, flatPad);

        // Install stays available in every state — it is also the REPAIR action. A game update
        // re-packs the game and restores stock content, and re-running install is the documented
        // fix; so is a half-broken install that still holds its registry entry. Disabling it once
        // the track is present would remove the remedy exactly when it is needed. Label it for the
        // state instead, so the button says what it will do.
        _install.Text = flatPad switch
        {
            FlatPadState.Installed => "Reinstall Flat Pad",
            FlatPadState.FilesPresentButNotRegistered => "Repair Flat Pad",
            _ => "Install Flat Pad",
        };
        _tips.SetToolTip(_install, flatPad == FlatPadState.NotInstalled
            ? "Derives the track from your game files and registers it."
            : "Rebuilds the track from scratch and re-registers it. Safe to run at any time — "
              + "it always starts from the game's own stock files.");

        bool present = flatPad != FlatPadState.NotInstalled;
        SetEnabled(
            unpack: archive.Mode == ArchiveMode.Packed,
            // Enabled whenever there is anything to restore. With several archives it asks which,
            // rather than going dark and leaving the user to work out why.
            revert: archive.Mode == ArchiveMode.Unpacked && archive.DisabledPackages.Count > 0,
            install: archive.Mode == ArchiveMode.Unpacked,
            uninstall: archive.Mode == ArchiveMode.Unpacked && present,
            verify: archive.Mode == ArchiveMode.Unpacked && present);
    }

    private static string BuildWarning(GameArchiveState archive, FlatPadState flatPad)
    {
        var lines = new List<string>();
        if (flatPad == FlatPadState.FilesPresentButNotRegistered && archive.Mode == ArchiveMode.Unpacked)
        {
            // Unpacking restores the stock registries over the registered ones. Nothing reports it,
            // and the track simply stops appearing.
            lines.Add("Flat Pad's files are on disk but its registry entries are gone — unpacking the "
                      + "game replaces those. Press \"Install Flat Pad\" to put them back.");
        }

        if (archive.Mode == ArchiveMode.Packed)
        {
            lines.Add("Tracks only load from loose folders, so the game has to be unpacked first. "
                      + "This is a big, slow, one-off operation — see the disk figure above.");
            if (!archive.HasEnoughSpace)
                lines.Add("There is not enough free space on this drive to unpack.");
        }

        if (archive.Mode == ArchiveMode.Unpacked)
        {
            lines.Add("While unpacked: do NOT use Steam's \"Verify integrity of game files\" — it "
                      + "re-downloads the whole archive. A game update also puts the game back to "
                      + "packed; re-running this tool fixes that.");
        }

        if (archive.DisabledPackages.Count > 1)
        {
            lines.Add($"{archive.DisabledPackages.Count} renamed-aside archives are present — a game "
                      + "update downloads a fresh one. \"Revert to packed\" will ask which to restore.");
        }

        return string.Join("\n\n", lines);
    }

    private void SetEnabled(bool unpack, bool revert, bool install, bool uninstall, bool verify)
    {
        _unpack.Enabled = unpack;
        _revert.Enabled = revert;
        _install.Enabled = install;
        _uninstall.Enabled = uninstall;
        _verify.Enabled = verify;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the Assetto Corsa EVO folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(GameRoot) ? GameRoot : "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _gamePath.Text = dialog.SelectedPath;
    }

    private void ToggleLog()
    {
        _log.Visible = _showLog.Checked;
        Height = Math.Max(Height, _showLog.Checked ? 620 : MinimumSize.Height);
    }

    // ------------------------------------------------------------------ running work

    private void Log(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(line));
            return;
        }

        _log.AppendText(line + Environment.NewLine);
    }

    /// <summary>Run a Core operation off the UI thread, with the buttons locked and the log live.</summary>
    private async Task RunAsync(string title, Action<Action<string>> work)
    {
        BeginWork(title, indeterminate: true);
        try
        {
            await Task.Run(() => work(Log));
            _progressText.Text = $"{title} — done.";
        }
        catch (Exception e)
        {
            Failed(title, e);
        }
        finally
        {
            EndWork();
        }
    }

    /// <summary>
    /// Verify, and offer to fix the one problem the user cannot fix by re-installing.
    /// </summary>
    /// <remarks>
    /// Everything else <c>verify</c> reports is repaired by pressing Install again. Missing
    /// BASE-GAME registry entries are not: rebuilding our own track cannot put back a catalog entry
    /// that was deleted from underneath another track. So it is offered here, in the one place where
    /// the damage has actually been demonstrated rather than guessed at — and never as a standing
    /// button, because the check has to open the game's 70 GB archive to know.
    /// </remarks>
    private async Task VerifyAsync()
    {
        VerifyReport? report = null;
        await RunAsync("Verifying", log =>
        {
            report = new Verifier(GameRoot).Run();
            log(report.Render().TrimEnd());
        });

        if (report?.Registry is not { Damaged.Count: > 0 } diff)
            return;

        string tracks = string.Join("\n  ", diff.DamagedTracks);
        DialogResult answer = MessageBox.Show(this,
            $"""
            These base-game tracks have files on disk but are missing from the game's track
            registry, so they will not appear in any menu:

              {tracks}

            Their entries are still in the game's own content archive ({diff.Source}).
            Restore just those entries? Nothing else in the registry is touched.
            """,
            "Registry entries missing", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return;

        await RunAsync("Repairing the registry", log => new Installer(GameRoot, log).RepairRegistry(diff));
    }

    private async Task UnpackAsync()
    {
        GameArchiveState archive = GameArchive.Detect(GameRoot);
        string question = $"""
            Unpack the game's content archive?

            This writes about {GameArchive.Bytes(archive.RequiredBytes)} to disk and takes a while.
            The archive itself is kept, renamed to content.kspkg.bak, so this is reversible with
            "Revert to packed".

            Nothing is renamed until every file is out, so cancelling leaves the game playable.
            """;
        if (MessageBox.Show(this, question, "Unpack game", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        BeginWork("Unpacking", indeterminate: false);
        var progress = new Progress<UnpackProgress>(p =>
        {
            _progress.Value = Math.Clamp((int)(p.Fraction * 100), 0, 100);
            _progressText.Text = $"Unpacking… {p.FilesDone:N0} / {p.FilesTotal:N0} files";
        });

        try
        {
            CancellationToken token = _cancellation!.Token;
            int files = await Task.Run(() => GameArchive.Unpack(GameRoot, progress, token), token);
            Log($"Unpacked {files:N0} files.");
            _progressText.Text = $"Unpacked {files:N0} files. The game now reads loose content.";
        }
        catch (OperationCanceledException)
        {
            _progressText.Text = "Cancelled. The archive is still in place, so the game still runs.";
            Log("Unpack cancelled — nothing was renamed, re-run to continue.");
        }
        catch (Exception e)
        {
            Failed("Unpack", e);
        }
        finally
        {
            EndWork();
        }
    }

    private void Revert()
    {
        GameArchiveState archive = GameArchive.Detect(GameRoot);
        string? chosen = archive.DisabledPackages.Count == 1 ? archive.DisabledPackages[0] : null;
        if (chosen is null)
        {
            using var picker = new ArchivePicker(GameRoot, archive.DisabledPackages);
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Chosen is null)
                return;
            chosen = picker.Chosen;
        }

        try
        {
            GameArchive.RevertToPacked(GameRoot, chosen);
            Log("Archive restored — the game reads packed content again.");
            MessageBox.Show(this,
                "The archive is back in place.\n\nThe unpacked files are still on disk and are "
                + "harmless, but you can delete the game's content folder to reclaim the space.",
                "Reverted", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            Failed("Revert", e);
        }
        finally
        {
            RefreshState();
        }
    }

    private void BeginWork(string title, bool indeterminate)
    {
        _cancellation = new CancellationTokenSource();
        _showLog.Checked = true;
        Log($"--- {title} ---");
        SetEnabled(false, false, false, false, false);
        _browse.Enabled = false;
        _gamePath.Enabled = false;
        _cancel.Enabled = !indeterminate;
        _progress.Visible = true;
        _progress.Style = indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        _progress.Value = 0;
        _progressText.Text = title + "…";
    }

    private void EndWork()
    {
        _cancellation?.Dispose();
        _cancellation = null;
        _cancel.Enabled = false;
        _progress.Visible = false;
        _browse.Enabled = true;
        _gamePath.Enabled = true;
        RefreshState();
    }

    private void Failed(string title, Exception e)
    {
        Log($"FAILED: {e.Message}");
        _progressText.Text = $"{title} failed.";

        if (e is UnauthorizedAccessException)
        {
            OfferElevation();
            return;
        }

        MessageBox.Show(this, e.Message, title + " failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// Steam's normal permissions let a user write into their own game folder, but that is not
    /// universal — a folder under Program Files with tightened ACLs, or a second Windows account,
    /// will refuse. Offer the elevated relaunch rather than dead-ending on an access error.
    /// </summary>
    private void OfferElevation()
    {
        DialogResult answer = MessageBox.Show(this,
            "Windows refused permission to write into the game folder.\n\n"
            + "Restart this installer as administrator and try again?",
            "Permission denied", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = true,
                Verb = "runas",
            });
            Close();
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Could not restart as administrator:\n\n" + e.Message,
                "Restart failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

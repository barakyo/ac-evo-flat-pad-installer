using System.Text;

using EvoMods.Core.Game;
using EvoMods.Core.Tracks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace EvoMods.App.Pages;

/// <summary>Installing custom tracks from a zip or folder, and managing the ones installed.</summary>
public sealed partial class TracksPage : Page
{
    private readonly StringBuilder _log = new();
    private string? _gameRoot;
    private bool _busy;

    public TracksPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    // ---- state

    private void Refresh()
    {
        _gameRoot = AppInfo.GameRoot;
        TrackList.Children.Clear();
        if (_gameRoot is null)
        {
            Blocked("No install found",
                "Assetto Corsa EVO could not be located. Nothing here can run without it.");
            return;
        }

        if (GameArchive.Detect(_gameRoot).Mode == ArchiveMode.Packed)
        {
            // Same reason as Flat Pad: tracks are not loaded from Saved Games\ACE\mods\, so while the
            // archive is live an installed track simply never appears.
            Blocked("The game is still packed",
                "Tracks only load from loose folders, so the game has to be unpacked first. "
                + "The Game screen does that — it is a big, slow, one-off operation.");
            return;
        }

        List<(LedgerTrack Track, CustomTrackState State)> tracks;
        try
        {
            tracks = new TrackInstaller(_gameRoot, _ => { }).List();
        }
        catch (Exception ex)
        {
            Blocked("The installed-tracks record cannot be read", ex.Message);
            return;
        }

        EmptyText.Visibility = tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach ((LedgerTrack track, CustomTrackState state) in tracks)
            TrackList.Children.Add(Card(track, state));

        SetEnabled(!_busy);
    }

    private Border Card(LedgerTrack track, CustomTrackState state)
    {
        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = track.DisplayName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        info.Children.Add(Caption(string.Join("  ·  ", new[]
        {
            track.TrackId,
            track.Version,
            $"{track.Files.Count:N0} files",
            $"from {Path.GetFileName(track.Source)}",
        }.Where(s => !string.IsNullOrEmpty(s)))));
        foreach (LedgerLayout l in track.Layouts)
            info.Children.Add(Caption($"{l.Code}: {string.Join(", ", l.Sessions)}"));
        info.Children.Add(new TextBlock { Text = Describe(state), TextWrapping = TextWrapping.Wrap });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (state == CustomTrackState.FilesPresentButNotRegistered)
            buttons.Children.Add(Action("Repair", true, () => Repair(track)));
        if (state == CustomTrackState.Installed)
            buttons.Children.Add(Action("Verify", false, () => Verify(track)));
        buttons.Children.Add(Action("Uninstall", false, () => Uninstall(track)));

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(info);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = grid,
        };
    }

    private static string Describe(CustomTrackState state) => state switch
    {
        CustomTrackState.Installed => "Installed — it appears in the track list.",
        CustomTrackState.FilesPresentButNotRegistered =>
            "Files are on disk but the registry entries are gone — a game update or file verification "
            + "does this. Repair puts them back.",
        CustomTrackState.RegisteredButFilesMissing =>
            "Registered, but its folder is gone, so the menus offer a track that cannot load. Uninstall it, "
            + "or install it again.",
        _ => "Not present any more — neither its files nor its registry entries. Uninstall clears the record.",
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private Button Action(string label, bool accent, Func<Task> onClick)
    {
        var button = new Button { Content = label, IsEnabled = !_busy };
        if (accent)
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += async (_, _) => await onClick();
        return button;
    }

    private void Blocked(string title, string why)
    {
        EmptyText.Visibility = Visibility.Collapsed;
        Warn(title, why, InfoBarSeverity.Warning);
        PickZipButton.IsEnabled = PickFolderButton.IsEnabled = false;
        AllowDrop = false;
    }

    private void Warn(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    private void SetEnabled(bool enabled)
    {
        PickZipButton.IsEnabled = PickFolderButton.IsEnabled = enabled;
        foreach (Button b in TrackList.Children.OfType<Border>()
                     .SelectMany(c => ((Grid)c.Child).Children.OfType<StackPanel>())
                     .SelectMany(p => p.Children.OfType<Button>()))
        {
            b.IsEnabled = enabled;
        }
    }

    private void Log(string line)
    {
        _log.AppendLine(line);
        LogText.Text = _log.ToString();
        LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
    }

    // ---- choosing a package

    /// <remarks>
    /// ⚠️ The Windows App SDK pickers, not <c>Windows.Storage.Pickers</c> — the UWP ones hang in this
    /// unpackaged app without ever showing a window. See <c>GamePage.PickPackage</c>.
    /// </remarks>
    private async void OnPickZip(object sender, RoutedEventArgs e)
    {
        if (App.Window is not { } window)
            return;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id);
        picker.FileTypeFilter.Add(".zip");
        if (await picker.PickSingleFileAsync() is { } file)
            await Offer(file.Path);
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        if (App.Window is not { } window)
            return;
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(window.AppWindow.Id);
        if (await picker.PickSingleFolderAsync() is { } folder)
            await Offer(folder.Path);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_busy || _gameRoot is null)
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Install track";
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_busy || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        switch (items.FirstOrDefault())
        {
            case StorageFolder folder:
                await Offer(folder.Path);
                break;
            case StorageFile file when file.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase):
                await Offer(file.Path);
                break;
            case StorageFile file:
                Warn("Not a track", $"{file.Name} is not a .zip or a folder.", InfoBarSeverity.Informational);
                break;
        }
    }

    // ---- installing

    /// <summary>Read a package, show what it holds, and install it if the person agrees.</summary>
    /// <remarks>
    /// The package stays open across the dialog — it is only a zip directory — and is re-planned
    /// with whatever was edited, because a new name can collide where the old one did not.
    /// </remarks>
    private async Task Offer(string path)
    {
        if (_gameRoot is not { } gameRoot || _busy)
            return;

        Notice.IsOpen = false;
        TrackPackage? package = null;
        try
        {
            TrackInstallPlan? plan = null;
            await Busy($"Reading {Path.GetFileName(path)}", async () =>
            {
                package = await Task.Run(() => TrackPackage.Open(path));
                plan = await Task.Run(() => new TrackInstaller(gameRoot, _ => { }).Plan(package));
            });
            if (plan is null)
                return;

            while (true)
            {
                TrackInstallOptions? options = await AskToInstall(plan);
                if (options is null)
                    return;

                plan = await Task.Run(() => new TrackInstaller(gameRoot, _ => { }).Plan(package!, options));
                if (plan.CanInstall)
                    break;
            }

            TrackInstallPlan approved = plan;
            await Run($"Installing {approved.DisplayName}", (log, progress) =>
                new TrackInstaller(gameRoot, log).Install(approved, progress));
        }
        catch (Exception ex)
        {
            Log($"{Path.GetFileName(path)}: {ex.Message}");
            Warn("That cannot be installed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            package?.Dispose();
        }
    }

    /// <summary>The install dialog: what the package holds, the names to register, and anything wrong.</summary>
    /// <returns>The chosen options, or null if the person backed out.</returns>
    private async Task<TrackInstallOptions?> AskToInstall(TrackInstallPlan plan)
    {
        TrackPackage p = plan.Package;
        var body = new StackPanel { Spacing = 12, MinWidth = 440 };

        body.Children.Add(Caption(
            $"{p.TrackId}{(p.Version is null ? "" : $"  ·  version {p.Version}")}  ·  {p.Files.Count:N0} files, "
            + $"{GameArchive.Bytes(p.TotalBytes)}{(p.FullyChecksummed ? ", every one checksummed" : "")}"
            + (plan.IsReinstall ? "\nAlready installed — this replaces it." : "")));

        var name = new TextBox { Header = "Name in the track list", Text = plan.DisplayName };
        body.Children.Add(name);

        var layoutBoxes = new Dictionary<string, TextBox>();
        foreach (LayoutRegistration l in plan.Layouts)
        {
            var box = new TextBox
            {
                Header = plan.Layouts.Count == 1 ? "Layout name" : $"Layout name (layout_{l.Layout.Id})",
                Text = l.Code,
            };
            layoutBoxes[l.Layout.Id] = box;
            body.Children.Add(box);

            // The layout name prefixes every session name, so show them as they will actually read.
            TextBlock sessions = Caption("");
            void ShowSessions() => sessions.Text =
                $"Sessions: {string.Join(", ", TrackRegistrar.SessionsFor(l with { Code = box.Text.Trim() }))}"
                + $"  ·  up to {l.GridSize} car(s)";
            ShowSessions();
            box.TextChanged += (_, _) => ShowSessions();
            body.Children.Add(sessions);
        }

        foreach (string w in plan.Warnings)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = w });
        if (plan.MissingReferences.Count > 0)
        {
            body.Children.Add(Caption("Missing: " + string.Join(", ", plan.MissingReferences.Take(5))
                                      + (plan.MissingReferences.Count > 5 ? $" and {plan.MissingReferences.Count - 5} more" : "")));
        }

        CheckBox? replace = null;
        if (plan.OtherToolRegistered)
        {
            replace = new CheckBox
            {
                Content = "Replace the registration another tool made for this track",
            };
            body.Children.Add(replace);
        }

        foreach (string problem in plan.Problems)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = problem });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Install {plan.DisplayName}?",
            Content = new ScrollViewer { Content = body, MaxHeight = 520 },
            PrimaryButtonText = plan.IsReinstall ? "Reinstall" : "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        return new TrackInstallOptions(
            name.Text,
            layoutBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text),
            replace?.IsChecked == true);
    }

    // ---- managing installed tracks

    private async Task Repair(LedgerTrack track) =>
        await Run($"Repairing {track.DisplayName}", (log, _) => new TrackInstaller(_gameRoot!, log).Repair(track.TrackId));

    private async Task Verify(LedgerTrack track) =>
        await Run($"Verifying {track.DisplayName}", (log, _) =>
        {
            List<string> problems = new TrackInstaller(_gameRoot!, log).Verify(track);
            foreach (string problem in problems)
                log($"  FAIL {problem}");
            log(problems.Count == 0
                ? $"  PASS registry rows, numbers, containers and {track.Files.Count:N0} file hashes"
                : $"  {problems.Count} problem(s) — installing it again fixes all of them");
        });

    private async Task Uninstall(LedgerTrack track)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove {track.DisplayName}?",
            Content = new TextBlock
            {
                Text = $"Its folder (content\\tracks\\{track.TrackId}) and the registry entries this tool made "
                       + "go. Nothing else in the registry is touched.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Run($"Removing {track.DisplayName}", (log, _) => new TrackInstaller(_gameRoot!, log).Uninstall(track.TrackId));
    }

    // ---- plumbing

    /// <summary>Lock the page and show an indeterminate bar while <paramref name="work"/> runs.</summary>
    private async Task Busy(string title, Func<Task> work)
    {
        _busy = true;
        SetEnabled(false);
        ProgressPanel.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        ProgressText.Text = $"{title}…";
        try
        {
            await work();
        }
        finally
        {
            Bar.IsIndeterminate = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            _busy = false;
            SetEnabled(true);
        }
    }

    /// <summary>Run a Core operation off the UI thread, with the log live and a file-count bar.</summary>
    private async Task Run(string title, Action<Action<string>, IProgress<(int Done, int Total)>> work)
    {
        if (_gameRoot is null)
            return;

        _busy = true;
        SetEnabled(false);
        ProgressPanel.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        ProgressText.Text = $"{title}…";
        Notice.IsOpen = false;

        void Emit(string line) => DispatcherQueue.TryEnqueue(() => Log(line));

        // Created here, on the UI thread, so its callbacks come back to it.
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Bar.IsIndeterminate = false;
            Bar.Value = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            ProgressText.Text = $"{title}… {p.Done:N0} / {p.Total:N0} files";
        });

        try
        {
            await Task.Run(() => work(Emit, progress));
            Log($"{title} — done.");
        }
        catch (Exception ex)
        {
            Log($"{title} failed: {ex.Message}");
            Warn($"{title} failed", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            _busy = false;
            Refresh();
        }
    }
}

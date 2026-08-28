using EvoMods.Core.Camera;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EvoMods.App.Pages;

/// <summary>
/// Tuning the camera settings the game actually honours.
/// </summary>
/// <remarks>
/// Sliders write nothing until Apply. Every change here goes to a file the game reads once at
/// startup, so there is no live preview to be had — a slider that wrote on every tick would just be
/// a slower way to reach the same file, with more chances to leave it half-set.
/// <para>
/// Two files, deliberately shown as one screen. Framing (where the camera sits) lives in
/// <c>CarCameras.carcamerausersettings</c>; behaviour and the driver views live in
/// <c>camerasettings.camerasettings</c>. Which file a setting is in is an implementation detail
/// nobody tuning a camera should have to hold, so Apply writes whichever of them changed.
/// </para>
/// </remarks>
public sealed partial class CameraPage : Page
{
    private readonly Dictionary<CameraField, Slider> _sliders = [];
    private readonly Dictionary<CameraField, TextBlock> _readouts = [];
    private readonly Dictionary<CameraField, TextBlock> _feels = [];
    private CameraReading _onDisk = new(new Dictionary<int, float>(), Exists: false);

    private readonly Dictionary<(int Camera, ChaseCamAxis Axis), Slider> _framing = [];
    private readonly Dictionary<(int Camera, ChaseCamAxis Axis), TextBlock> _framingReadouts = [];
    private readonly Dictionary<int, TextBlock> _framingFeels = [];
    private ChaseCamReading _framingOnDisk = ChaseCamReading.Absent;

    /// <summary>The entry that is not a preset, and the only one that reveals the sliders.</summary>
    private const string Custom = "Custom";

    /// <summary>
    /// The running game's PID as of the last <see cref="Refresh"/>, or null if it was not up.
    /// </summary>
    /// <remarks>
    /// ⚠️ Asked once per refresh rather than per button update, because the answer costs ~8 ms:
    /// <see cref="CameraSettings.RunningGame"/> enumerates every process on the machine, and
    /// <see cref="UpdateButtons"/> runs from all fourteen sliders' ValueChanged. Filling them in on
    /// load was ~16 process sweeps for ~130 ms of visible delay, against half a millisecond to read
    /// both settings files.
    /// <para>
    /// Safe to cache because it only gates whether the buttons are offered. Both writers re-check it
    /// for real at the moment they write, which is the check that actually protects the file.
    /// </para>
    /// </remarks>
    private int? _gamePid;

    /// <summary>
    /// True while values are being pushed INTO the sliders rather than pulled out of them.
    /// </summary>
    /// <remarks>
    /// Without it, seeding the sliders from a preset would trip the same handler that flips the
    /// dropdown to Custom, so choosing "Wide" would immediately stop saying Wide.
    /// </remarks>
    private bool _seeding;

    public CameraPage()
    {
        InitializeComponent();
        Build();
        Loaded += (_, _) => Refresh();
    }

    // ---- the controls

    private void Build()
    {
        foreach (ChaseCamPreset preset in ChaseCamSpec.Presets)
            PresetBox.Items.Add(preset.Name);
        PresetBox.Items.Add(Custom);

        foreach (int camera in new[] { ChaseCamSpec.NearChase, ChaseCamSpec.FarChase })
            CustomRows.Children.Add(FramingGroup(camera));

        CameraEffect? section = null;
        foreach (CameraField field in CameraSpec.Fields)
        {
            if (field.Effect != section)
            {
                section = field.Effect;
                Rows.Children.Add(Header(field.Effect));
            }

            Rows.Children.Add(Row(field));
        }
    }

    /// <summary>
    /// Group by how much is actually known, because that is the thing a reader most needs.
    /// </summary>
    /// <remarks>
    /// Two of these were measured as dramatic and chase-only, so they can go to any extreme. The
    /// rest change the view you drive from, which is a different kind of decision — and burying that
    /// distinction in per-control small print would be the wrong place for it.
    /// </remarks>
    private static UIElement Header(CameraEffect effect) =>
        Section(effect == CameraEffect.Works ? "Chase camera behaviour" : "Driver view settings");

    /// <summary>
    /// A section heading with a rule above it.
    /// </summary>
    /// <remarks>
    /// Every block on this page is a stack of labelled sliders, so without a rule they run together
    /// and a label four rows down could belong to the section above it. Kept identical across all of
    /// them — a heading that is a different size in each section reads as a hierarchy that is not
    /// there. The width matches the rows: 210 label, 300 slider, 96 readout, plus the two gaps.
    /// </remarks>
    private static UIElement Section(string text)
    {
        var panel = new StackPanel();
        panel.Children.Add(new Border
        {
            Height = 1,
            Width = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        });

        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 19,
            Margin = new Thickness(0, 0, 0, 6),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });

        return panel;
    }

    // ---- framing

    /// <summary>One chase camera's four numbers, and what they add up to.</summary>
    /// <remarks>
    /// The per-value description sits under the GROUP rather than under every row, the way the
    /// behaviour sliders below do it. Four of these carry one look between them — height on its own
    /// says nothing about the frame — and eight wrapped captions would bury the sliders they explain.
    /// Each knob's own note is on the label as a tooltip instead.
    /// </remarks>
    private UIElement FramingGroup(int camera)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Section(ChaseCamSpec.CameraNames[camera]));

        foreach (ChaseCamKnob knob in ChaseCamSpec.Knobs)
            panel.Children.Add(FramingRow(camera, knob));

        var feel = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
            Margin = new Thickness(0, 4, 0, 0),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        _framingFeels[camera] = feel;
        panel.Children.Add(feel);
        return panel;
    }

    private UIElement FramingRow(int camera, ChaseCamKnob knob)
    {
        var value = new TextBlock
        {
            MinWidth = 96,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 17,
            FontFamily = (FontFamily)Application.Current.Resources["BrandNumeralFont"],
            Foreground = (Brush)Application.Current.Resources["BrandBlue"],
        };

        var slider = new Slider
        {
            Minimum = knob.Min,
            Maximum = knob.Max,
            StepFrequency = knob.Step,
            Width = 300,
            Margin = new Thickness(0, -4, 0, -4),
        };

        slider.ValueChanged += (_, _) =>
        {
            // Moving anything means this is no longer the preset it was seeded from. Flipping the
            // dropdown rather than silently lying is the whole reason Custom exists.
            if (!_seeding)
                PresetBox.SelectedItem = Custom;

            DescribeFraming();
            UpdateButtons();
        };

        var label = new TextBlock
        {
            Text = knob.Label,
            MinWidth = 210,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        ToolTipService.SetToolTip(label, new TextBlock { Text = knob.Note, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });

        // Eight sliders whose labels are separate TextBlocks read as eight unnamed sliders to a
        // screen reader, and "Height" alone would not say which of the two cameras it belongs to.
        AutomationProperties.SetName(slider, $"{ChaseCamSpec.CameraNames[camera]} {knob.Label}");

        _framing[(camera, knob.Axis)] = slider;
        _framingReadouts[(camera, knob.Axis)] = value;

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(label);
        head.Children.Add(slider);
        head.Children.Add(value);
        return head;
    }

    /// <summary>What the framing sliders currently say.</summary>
    private ChaseCamView ViewOf(int camera) => new(
        (float)_framing[(camera, ChaseCamAxis.Height)].Value,
        (float)_framing[(camera, ChaseCamAxis.Distance)].Value,
        (float)_framing[(camera, ChaseCamAxis.Pitch)].Value,
        (float)_framing[(camera, ChaseCamAxis.Fov)].Value);

    private void Seed(int camera, ChaseCamView view)
    {
        foreach (ChaseCamKnob knob in ChaseCamSpec.Knobs)
            _framing[(camera, knob.Axis)].Value = view[knob.Axis];
    }

    /// <summary>Push a preset into the sliders without the dropdown treating it as a hand edit.</summary>
    private void Seed(ChaseCamView near, ChaseCamView far)
    {
        _seeding = true;
        Seed(ChaseCamSpec.NearChase, near);
        Seed(ChaseCamSpec.FarChase, far);
        _seeding = false;
    }

    private void DescribeFraming()
    {
        foreach (int camera in new[] { ChaseCamSpec.NearChase, ChaseCamSpec.FarChase })
        {
            ChaseCamView view = ViewOf(camera);
            foreach (ChaseCamKnob knob in ChaseCamSpec.Knobs)
                _framingReadouts[(camera, knob.Axis)].Text = knob.Format(view[knob.Axis]);

            _framingFeels[camera].Text = ChaseCamSpec.Feel(view);
        }

        // A named preset has to describe itself here, because its sliders are collapsed. Custom's are
        // not, and each group already carries its own description — saying it twice on one screen
        // reads as two different facts until you have compared them word for word.
        if (Selected() is { } preset)
        {
            PresetBlurb.Text = preset.Blurb;
            PresetBlurb.Visibility = Visibility.Visible;
        }
        else
        {
            PresetBlurb.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The preset the dropdown names, or null when it says Custom.</summary>
    private ChaseCamPreset? Selected() =>
        PresetBox.SelectedItem is string name
            ? ChaseCamSpec.Presets.FirstOrDefault(p => p.Name == name)
            : null;

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        bool custom = (PresetBox.SelectedItem as string) == Custom;
        CustomRows.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

        // Custom keeps whatever is on the sliders — it is where you land after moving one, and
        // resetting them at that moment would throw away the edit that got you here.
        if (Selected() is { } preset)
            Seed(preset.Near, preset.Far);

        DescribeFraming();
        UpdateButtons();
    }

    // ---- behaviour and driver view

    private UIElement Row(CameraField field)
    {
        // Rajdhani, which is what the site uses for lap times and telemetry numerals. This is the one
        // place in the app that is genuinely a readout rather than prose.
        var value = new TextBlock
        {
            MinWidth = 96,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 17,
            FontFamily = (FontFamily)Application.Current.Resources["BrandNumeralFont"],
            Foreground = (Brush)Application.Current.Resources["BrandBlue"],
        };

        // The description changes as the slider moves, which is the point of it: a number like 1.5
        // says nothing on its own, and "lags visibly, but recovers within a corner" does.
        var feel = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var slider = new Slider
        {
            Minimum = field.Min,
            Maximum = field.Max,
            StepFrequency = Step(field),
            Width = 300,
            Margin = new Thickness(0, -4, 0, -4),
        };
        slider.ValueChanged += (_, _) =>
        {
            Describe(field, slider, value, feel);
            UpdateButtons();
        };

        _sliders[field] = slider;
        _readouts[field] = value;
        _feels[field] = feel;

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(new TextBlock
        {
            Text = $"{field.Label} (default: {field.Stock:0.##})",
            MinWidth = 210,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        head.Children.Add(slider);
        head.Children.Add(value);

        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(head);
        row.Children.Add(feel);
        return row;
    }

    /// <summary>Put the current value, and what it feels like, next to the slider.</summary>
    private static void Describe(CameraField field, Slider slider, TextBlock value, TextBlock feel)
    {
        float v = (float)slider.Value;
        string? readout = CameraSpec.Readout(field, v);
        value.Text = readout is null ? v.ToString("0.##") : $"{v:0.##} ({readout})";
        feel.Text = CameraSpec.Feel(field, v);
    }

    /// <summary>Fine enough to tune with, coarse enough that the number stops jittering.</summary>
    private static double Step(CameraField field) => (field.Max - field.Min) <= 1.5 ? 0.01 : 0.05;

    // ---- state

    /// <param name="keepError">
    /// A failure to show instead of the standing notices. Without it a caller that reports an error
    /// and then refreshes has that error overwritten before it is ever seen.
    /// </param>
    private void Refresh(string? keepError = null)
    {
        // First, because filling the sliders below trips UpdateButtons on every one of them.
        _gamePid = CameraSettings.RunningGame()?.Id;

        // Neither read is guaranteed to succeed — a structurally unreadable file throws rather than
        // guessing — and this runs from Loaded, where an exception has nowhere to go.
        string? unreadable = null;
        try
        {
            _onDisk = CameraSettings.Read();
            _framingOnDisk = CarCameras.Read();
        }
        catch (Exception ex)
        {
            _onDisk = new CameraReading(new Dictionary<int, float>(), Exists: false);
            _framingOnDisk = ChaseCamReading.Absent;
            unreadable = ex.Message;
        }

        foreach ((CameraField field, Slider slider) in _sliders)
        {
            slider.Value = _onDisk.ValueOf(field);

            // Describe here rather than leaning on ValueChanged: assigning a value the slider
            // already holds raises no event, so a setting that happens to sit at the slider's
            // starting value would show a blank number. Horizon lock at 0 does exactly that.
            Describe(field, slider, _readouts[field], _feels[field]);
        }

        Seed(_framingOnDisk.Near, _framingOnDisk.Far);

        // Selecting a preset re-seeds from it, which is a no-op when the file already matches. A file
        // that matches nothing lands on Custom with its own values, which is what it actually is.
        PresetBox.SelectedItem = _framingOnDisk.Preset?.Name ?? Custom;
        DescribeFraming();

        if (Drift() is { } drift)
        {
            FramingNotice.Title = drift.Title;
            FramingNotice.Message = drift.Message;
            FramingNotice.IsOpen = true;
        }
        else
        {
            FramingNotice.IsOpen = false;
        }

        if (keepError is not null)
        {
            // The caller already failed at something the user asked for, and that beats every
            // standing message below — none of which is news by comparison.
            Warn("That didn't work", keepError, InfoBarSeverity.Error);
        }
        else if (unreadable is not null)
        {
            Warn("That file could not be read", unreadable, InfoBarSeverity.Error);
        }
        else if (!_onDisk.Exists || !_framingOnDisk.Exists)
        {
            Warn("No settings file yet",
                Missing() + " Launch the game once so it writes one. This edits the files the game "
                + "already has rather than inventing them.", InfoBarSeverity.Warning);
        }
        else if (_gamePid is { } pid)
        {
            Warn("The game is running",
                $"Assetto Corsa EVO (PID {pid}) reads these files at startup and rewrites them "
                + "on exit, so nothing can be saved while it is up. Close it and come back.",
                InfoBarSeverity.Warning);
        }
        else
        {
            // The one way to lose these values without doing anything obviously wrong.
            Warn("After saving",
                "Do not open the in-game camera settings screen — the game rewrites these files and "
                + "discards these values. Changes take effect the next time the game starts.",
                InfoBarSeverity.Informational);
        }

        UpdateButtons();
    }

    /// <summary>
    /// Which cars are not on the framing shown, and why that happened.
    /// </summary>
    /// <remarks>
    /// Two explanations, because either one is a lie about the other file. A clear majority with a
    /// small minority is a content update: the game appends an entry at the car's own default the
    /// first time you drive it, so the new cars arrive on the shipped framing. A file split many ways
    /// was tuned car by car — which is exactly what the reference script's output looks like — and
    /// blaming the game for that one would be false.
    /// <para>
    /// ⚠️ Never worded as though the majority is authoritative. It usually is, but a file holding two
    /// hand-tuned cars and fourteen the game appended has a majority that is exactly backwards, and
    /// this text plus the confirmation on Apply are the only things between that file and a one-click
    /// flatten.
    /// </para>
    /// </remarks>
    private (string Title, string Message)? Drift()
    {
        if (!_framingOnDisk.Exists || _framingOnDisk.Drifted.Count == 0)
            return null;

        string shown = _framingOnDisk.Preset?.Name ?? "the framing shown";

        // What they are ON is what says whether Apply is safe to press. Three opaque keys do not.
        string cause = _framingOnDisk.Framings.Count == 2
            ? $"They are on {Framing(_framingOnDisk.Framings[1])}. The game adds a car at its own "
                + "default the first time you drive it, so a game update leaves the new ones behind."
            : $"This file is split {_framingOnDisk.Framings.Count} ways — it was tuned car by car "
                + "rather than all at once.";

        return ($"{_framingOnDisk.Drifted.Count} of {_framingOnDisk.Cars.Count} cars are not on {shown}",
            $"{Listed(_framingOnDisk.Drifted)}. {cause} Apply puts every car in the file on {shown}. "
                + "A car you have not driven yet is not in the file at all, so this is worth checking "
                + "before a capture session rather than after an update.");
    }

    /// <summary>A framing by its preset name, or by the numbers when it is not one.</summary>
    /// <remarks>
    /// Written the way the preset table writes them — 1.80 / 5.19 / -5.0 — so a framing with no name
    /// can still be compared against one by eye.
    /// </remarks>
    private static string Framing(ChaseCamFraming framing) =>
        framing.Preset?.Name
        ?? $"{framing.Near.Height:0.00} / {framing.Near.Distance:0.00} / {framing.Near.Pitch:0.0}°";

    /// <summary>A few car keys and then a count. Sixteen of them is a wall, not a list.</summary>
    private static string Listed(IReadOnlyList<string> cars, int cap = 3) =>
        cars.Count <= cap
            ? string.Join(", ", cars)
            : $"{string.Join(", ", cars.Take(cap))} and {cars.Count - cap} more";

    /// <summary>Name the file that is missing, because the two are fixed by the same thing.</summary>
    private string Missing() => (_onDisk.Exists, _framingOnDisk.Exists) switch
    {
        (false, false) => "Neither camera file is there yet.",
        (true, false) => "The chase camera framing file is not there yet.",
        _ => "The camera settings file is not there yet.",
    };

    /// <summary>
    /// Do the sliders say something other than the file they were filled from?
    /// </summary>
    /// <remarks>
    /// ⚠️ The tolerance has to be the one <see cref="ChaseCamSpec.Match"/> uses, not a tighter one.
    /// Naming a preset re-seeds the sliders from that preset's canonical values, so the sliders can
    /// hold 5.19 while the file holds the 5.190157 the R34 actually shipped with — and at 1e-4 a
    /// freshly loaded, untouched file reports an edit nobody made, with Discard lit to undo it.
    /// Nothing is lost by widening: the coarsest slider step is 0.05, fifty times this.
    /// </remarks>
    private bool FramingEdited() =>
        !ViewOf(ChaseCamSpec.NearChase).Matches(_framingOnDisk.Near, 1e-3f)
        || !ViewOf(ChaseCamSpec.FarChase).Matches(_framingOnDisk.Far, 1e-3f);

    /// <summary>
    /// Is there anything for Apply to do?
    /// </summary>
    /// <remarks>
    /// ⚠️ Not the same question as <see cref="FramingEdited"/>. The sliders are filled from ONE car,
    /// so a file whose cars disagree always has work left even when they match it exactly — which is
    /// precisely the file you get after tuning one car with the reference script. Comparing against
    /// the representative alone left Apply greyed out with eleven cars still unset.
    /// </remarks>
    private bool FramingWorthWriting() =>
        FramingEdited() || (_framingOnDisk.Exists && !_framingOnDisk.Uniform);

    private void UpdateButtons()
    {
        bool behaviourDirty = _sliders.Any(s => Math.Abs(s.Value.Value - _onDisk.ValueOf(s.Key)) > 1e-4);
        bool gameUp = _gamePid is not null;

        // Each file stands on its own: one being absent is no reason to refuse the other.
        bool behaviour = _onDisk.Exists && !gameUp;
        bool framing = _framingOnDisk.Exists && !gameUp;

        ApplyButton.IsEnabled = (behaviourDirty && behaviour) || (FramingWorthWriting() && framing);

        // Discard is about the controls, not the file — there is nothing to put back when the
        // sliders already agree with what they were filled from.
        RevertButton.IsEnabled = behaviourDirty || FramingEdited();
        RestoreButton.IsEnabled = behaviour || framing;
    }

    private void Warn(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    // ---- actions

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        Refresh();
        StatusText.Text = "Back to what the files say.";
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        // Read the controls here rather than inside the worker: everything below runs off the UI
        // thread, and a slider cannot be touched from there.
        ChaseCamView near = ViewOf(ChaseCamSpec.NearChase);
        ChaseCamView far = ViewOf(ChaseCamSpec.FarChase);
        Dictionary<int, float> behaviour = _sliders.ToDictionary(s => s.Key.Number, s => (float)s.Value.Value);
        bool writeFraming = _framingOnDisk.Exists;
        bool writeBehaviour = _onDisk.Exists;

        // The file disagreeing with itself is the one case where Apply is not obviously the right
        // thing: the framing on screen is the one MOST cars are on, and "most" is not "intended".
        // A file holding two hand-tuned cars and fourteen the game appended reads as the fourteen,
        // and one click would flatten the two. Name both sides and let the choice be made.
        if (writeFraming && _framingOnDisk.Drifted.Count > 0)
        {
            int settled = _framingOnDisk.Cars.Count - _framingOnDisk.Drifted.Count;
            ChaseCamFraming[] rest = _framingOnDisk.Framings.Skip(1).ToArray();
            string others = string.Join(", ", rest.Select(f => $"{f.Cars.Count} on {Framing(f)}"));

            string shown = Framing(_framingOnDisk.Framings[0]);

            if (!await Confirm("Put every car on this framing?",
                    $"{settled} car(s) in this file are on {shown} and {others}. Apply puts all "
                    + $"{_framingOnDisk.Cars.Count} of them on {shown}, which is what the sliders are "
                    + "showing. A copy of the file is kept alongside it first.", "Apply to all"))
            {
                return;
            }
        }

        await Run(log =>
        {
            int changed = 0;
            if (writeFraming)
                changed += CarCameras.Write(near, far, log);
            if (writeBehaviour)
                changed += CameraSettings.Write(behaviour, log);
            return changed;
        });
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        bool writeFraming = _framingOnDisk.Exists;
        bool writeBehaviour = _onDisk.Exists;

        if (await Confirm("Restore the game's defaults?",
                "Every setting here goes back to the value the game ships, and the framing goes back "
                + "to the convention every Kunos car uses — which is not the same as undoing your own "
                + "edits, because some cars ship with values that were never deliberate. A copy of "
                + "each file is kept alongside it first.", "Restore"))
        {
            await Run(log =>
            {
                int changed = 0;
                if (writeFraming)
                    changed += CarCameras.Restore(log);
                if (writeBehaviour)
                    changed += CameraSettings.Restore(log);
                return changed;
            });
        }
    }

    private async Task Run(Func<Action<string>, int> work)
    {
        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        StatusText.Text = "Saving…";

        var lines = new List<string>();
        string? failure = null;
        try
        {
            int changed = await Task.Run(() => work(lines.Add));
            StatusText.Text = changed == 0
                ? "Nothing to change — the files already say that."
                : $"Saved {changed} setting(s). They apply next time the game starts.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "";
            failure = ex.Message;
        }

        // Handed to Refresh rather than shown before it: Refresh sets the same InfoBar, so reporting
        // the failure here would have it overwritten by a standing message on the very next line.
        Refresh(failure);
    }

    private async Task<bool> Confirm(string title, string body, string action)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

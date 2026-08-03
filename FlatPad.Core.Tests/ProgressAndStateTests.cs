using FlatPad.Core.FlatPad;
using FlatPad.Core.Game;
using FlatPad.Core.Refs;

using static FlatPad.Core.Tests.PbFixture;

namespace FlatPad.Core.Tests;

/// <summary>
/// The two bugs a full-scale unpack exposed that nothing smaller did.
/// </summary>
public class ProgressAndStateTests
{
    private sealed class Recorder<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
    }

    // ------------------------------------------------------------------ progress flooding

    [Fact]
    public void A_fast_loop_cannot_flood_its_consumer()
    {
        // Reporting once per file is fine for 650 files and fatal for 119,443: every report is
        // posted to the UI thread, the pump backs up, the window stops responding — and Cancel
        // stops working with it.
        var sink = new Recorder<int>();
        TimeSpan now = TimeSpan.Zero;
        var throttle = new ThrottledProgress<int>(sink, TimeSpan.FromMilliseconds(100), () => now);

        for (int i = 0; i < 119_443; i++)
            throttle.Report(i);

        Assert.Single(sink.Reports);       // no time passed, so only the first got through
        Assert.Equal(0, sink.Reports[0]);
    }

    [Fact]
    public void Reports_resume_once_the_interval_has_passed()
    {
        var sink = new Recorder<int>();
        TimeSpan now = TimeSpan.Zero;
        var throttle = new ThrottledProgress<int>(sink, TimeSpan.FromMilliseconds(100), () => now);

        throttle.Report(1);
        now = TimeSpan.FromMilliseconds(50);
        throttle.Report(2);                            // too soon
        now = TimeSpan.FromMilliseconds(150);
        throttle.Report(3);
        now = TimeSpan.FromMilliseconds(260);
        throttle.Report(4);

        Assert.Equal([1, 3, 4], sink.Reports);
    }

    [Fact]
    public void The_final_report_is_never_dropped()
    {
        // Otherwise a run that finishes inside the throttle window ends showing 99 %.
        var sink = new Recorder<int>();
        TimeSpan now = TimeSpan.Zero;
        var throttle = new ThrottledProgress<int>(sink, TimeSpan.FromMilliseconds(100), () => now);

        throttle.Report(1);
        now = TimeSpan.FromMilliseconds(5);
        throttle.ReportFinal(999);

        Assert.Equal([1, 999], sink.Reports);
    }

    [Fact]
    public void A_null_consumer_is_not_an_error()
    {
        var throttle = new ThrottledProgress<int>(null, TimeSpan.FromMilliseconds(100));

        throttle.Report(1);
        throttle.ReportFinal(2);
    }

    // ------------------------------------------------------------------ "installed" really meaning it

    private static string Root() => Directory.CreateTempSubdirectory("flatpad-state-").FullName;

    private static void WriteTracksTable(string root, bool withFlatPad)
    {
        var entries = new List<byte[]>
        {
            MessageField(3, MessageField(2, StringField(1, "Sebring International Raceway"))),
        };
        if (withFlatPad)
            entries.Add(MessageField(3, MessageField(2, StringField(1, "Flat Pad"))));

        string path = RefPath.RealPath(root, "system/tracks.table");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, MessageField(2, Cat([.. entries])));
    }

    [Fact]
    public void Track_files_without_a_registry_entry_are_not_installed()
    {
        // Unpacking the game restores the stock registries over the registered ones. The folder
        // survives, so a "does the folder exist" check reports a healthy install and sends the user
        // to a track list that does not contain it.
        string root = Root();
        try
        {
            Directory.CreateDirectory(RefPath.RealPath(root, "content/tracks/flatpad"));
            WriteTracksTable(root, withFlatPad: false);

            Assert.Equal(FlatPadState.FilesPresentButNotRegistered, Verifier.DetectState(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Files_plus_a_registry_entry_is_installed()
    {
        string root = Root();
        try
        {
            Directory.CreateDirectory(RefPath.RealPath(root, "content/tracks/flatpad"));
            WriteTracksTable(root, withFlatPad: true);

            Assert.Equal(FlatPadState.Installed, Verifier.DetectState(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void No_track_folder_is_simply_not_installed()
    {
        string root = Root();
        try
        {
            WriteTracksTable(root, withFlatPad: true);   // a stale entry is still "not installed"

            Assert.Equal(FlatPadState.NotInstalled, Verifier.DetectState(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_unreadable_registry_does_not_throw_out_of_a_status_check()
    {
        string root = Root();
        try
        {
            Directory.CreateDirectory(RefPath.RealPath(root, "content/tracks/flatpad"));
            // No tracks.table at all — a status check must still answer.
            Assert.Equal(FlatPadState.FilesPresentButNotRegistered, Verifier.DetectState(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

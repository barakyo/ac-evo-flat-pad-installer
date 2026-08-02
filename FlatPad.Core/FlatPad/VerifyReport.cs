using System.Text;

namespace FlatPad.Core.FlatPad;

/// <summary>
/// The outcome of a verification run: the per-check narrative, and the problems found.
/// </summary>
/// <remarks>
/// Every check contributes a line reporting its COUNT, not just a verdict — a check that finds
/// nothing to check would otherwise report a cheerful, meaningless PASS.
/// </remarks>
public sealed class VerifyReport
{
    public List<string> Lines { get; } = [];
    public List<string> Problems { get; } = [];

    public bool Passed => Problems.Count == 0;

    /// <summary>Exit code: 0 on pass, 1 on fail.</summary>
    public int ExitCode => Passed ? 0 : 1;

    internal void Line(string text) => Lines.Add(text);

    internal void Problem(string text) => Problems.Add(text);

    /// <summary>The full console report, verdict included.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        foreach (string line in Lines)
            sb.AppendLine(line);
        if (Problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"FAIL — {Problems.Count} problem(s):");
            foreach (string p in Problems)
                sb.AppendLine($"  * {p}");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("PASS");
        }

        return sb.ToString();
    }
}

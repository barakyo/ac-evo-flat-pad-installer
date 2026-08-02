using System.Globalization;

namespace FlatPad.Core.FlatPad;

/// <summary>
/// Number and collection formatting that matches the Python reference implementation's output.
/// </summary>
/// <remarks>
/// The acceptance test for this port is that its <c>verify</c> output diffs clean against the
/// Python script's, so every formatted value has to render the same way — including Python's
/// <c>repr</c> of a list, which uses single quotes.
/// </remarks>
public static class PyFormat
{
    /// <summary>Python's <c>f"{x:.0f}"</c>.</summary>
    public static string F0(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>Python's <c>f"{x:.1f}"</c>.</summary>
    public static string F1(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>Python's <c>f"{x:g}"</c> for the values we format this way (plain magnitudes).</summary>
    public static string G(double value) => value.ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>Python's <c>str(True)</c>.</summary>
    public static string Bool(bool value) => value ? "True" : "False";

    /// <summary>Python's <c>repr()</c> of a float — a whole number still shows its <c>.0</c>.</summary>
    public static string ReprFloat(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e16
            ? value.ToString("F1", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Python's <c>repr()</c> of a 2-tuple of floats: <c>(-219.0, 164.0)</c>.</summary>
    public static string ReprTuple(double a, double b) => $"({ReprFloat(a)}, {ReprFloat(b)})";

    /// <summary>Python's <c>repr()</c> of a list of strings: <c>['a', 'b']</c>.</summary>
    public static string Repr(IEnumerable<string?> items) =>
        "[" + string.Join(", ", items.Select(s => s is null ? "None" : $"'{s}'")) + "]";

    /// <summary>Python's <c>repr()</c> of a list of integers: <c>[26001]</c>.</summary>
    public static string Repr(IEnumerable<ulong> items) =>
        "[" + string.Join(", ", items.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";
}

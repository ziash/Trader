using System.Globalization;
using System.Text;

namespace Trader.Core.Csv;

/// <summary>
/// Appends rows to one CSV file per UTC day, in the format of shared.md §5: UTF-8, comma-separated,
/// header row, one record per line, text quoted only when needed. Thread-safe; every row is flushed
/// to disk at once, so a crash loses at most the row being written.
/// </summary>
public sealed class DailyCsvWriter(Func<DateOnly, string> pathForDay, IReadOnlyList<string> header) : IDisposable
{
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public IReadOnlyList<string> Header { get; } = header;

    /// <summary>Writes one row into the file of <paramref name="dayUtc"/>'s date. Values must already be formatted (see <see cref="CsvFormat"/>).</summary>
    public void Write(DateTime dayUtc, IReadOnlyList<string> values)
    {
        if (values.Count != Header.Count)
            throw new ArgumentException($"Expected {Header.Count} values, got {values.Count}");

        lock (_lock)
        {
            var day = DateOnly.FromDateTime(dayUtc);
            if (_writer is null || day != _day)
                Open(day);
            _writer!.Write(CsvFormat.Line(values));
            _writer.Flush();
        }
    }

    private void Open(DateOnly day)
    {
        _writer?.Dispose();
        var path = pathForDay(day);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        if (isNew)
            _writer.Write(CsvFormat.Line(Header));
        _day = day;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>Formatting rules shared by all CSV files (shared.md §5).</summary>
public static class CsvFormat
{
    /// <summary>"2026-10-18 08:00:00" — Google Sheets reads this as a date-time. Always UTC.</summary>
    public static string Time(DateTime utc) =>
        (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Fixed decimals, '.' as decimal point, no thousands separator; empty for null/NaN.</summary>
    public static string Num(double? value, int decimals) =>
        value is { } v && double.IsFinite(v) ? (Math.Round(v, decimals) + 0.0).ToString("F" + decimals, CultureInfo.InvariantCulture) : ""; // + 0.0 turns -0 into 0

    public static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Line(IEnumerable<string> values) => string.Join(",", values.Select(Escape)) + "\n";

    /// <summary>Quotes a field only when it contains a comma, quote or line break.</summary>
    public static string Escape(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
}

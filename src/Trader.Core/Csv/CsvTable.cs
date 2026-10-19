using System.Globalization;
using System.Text;

namespace Trader.Core.Csv;

/// <summary>
/// Reads a CSV file written in the format of plan 6.3.1 (header row, fields quoted only when needed).
/// Columns are found by name, so files with extra or reordered columns still read. Lines whose field
/// count does not match the header (e.g. a line cut off by a crash) are skipped and counted.
/// </summary>
public sealed class CsvTable
{
    private readonly Dictionary<string, int> _columns;

    private CsvTable(IReadOnlyList<string> header, List<string[]> rows, int badLines)
    {
        Header = header;
        Rows = rows.Select(r => new CsvRow(this, r)).ToList();
        BadLines = badLines;
        _columns = header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Header { get; }
    public IReadOnlyList<CsvRow> Rows { get; }
    public int BadLines { get; }

    public bool Has(string column) => _columns.ContainsKey(column);

    internal int Index(string column) =>
        _columns.TryGetValue(column, out var i) ? i : throw new KeyNotFoundException($"CSV has no column '{column}'");

    public static CsvTable Read(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static CsvTable Parse(string text)
    {
        var lines = SplitRecords(text).ToList();
        if (lines.Count == 0)
            return new CsvTable([], [], 0);

        var header = lines[0];
        var rows = new List<string[]>();
        var bad = 0;
        foreach (var fields in lines.Skip(1))
        {
            if (fields.Length == header.Length)
                rows.Add(fields);
            else if (!(fields.Length == 1 && fields[0].Length == 0))
                bad++;
        }
        return new CsvTable(header, rows, bad);
    }

    /// <summary>Splits text into records and fields; a quoted field may contain commas, quotes ("") and line breaks.</summary>
    private static IEnumerable<string[]> SplitRecords(string text)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                yield return fields.ToArray();
                fields.Clear();
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }
}

/// <summary>One data row; values by column name.</summary>
public sealed class CsvRow(CsvTable table, string[] fields)
{
    public string this[string column] => fields[table.Index(column)];

    /// <summary>The number in <paramref name="column"/>, or null when the cell is empty or not a number.</summary>
    public double? Num(string column) =>
        double.TryParse(this[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>The time in <paramref name="column"/> (yyyy-MM-dd HH:mm:ss, UTC).</summary>
    public DateTime? Time(string column) =>
        DateTime.TryParseExact(this[column], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

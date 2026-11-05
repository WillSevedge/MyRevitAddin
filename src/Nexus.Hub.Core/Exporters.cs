using System.Text;
using System.Text.Json;
using Nexus.Contracts;

namespace Nexus.Hub.Core;

public static class Exporters
{
    private static readonly string[] FixedColumns = { "Host", "Document", "Reader", "Item Path", "Item Type", "Item", "Key", "Item Id" };

    /// <summary>One row per item, one column per selected property.</summary>
    public static void WriteWideCsv(ResultTable table, IReadOnlyCollection<string>? columnIds, string path)
    {
        var cols = table.Columns.Where(c => columnIds is null || columnIds.Contains(c.Id)).ToList();
        using var w = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        WriteRow(w, FixedColumns.Concat(cols.Select(c => c.Id)));
        foreach (var r in table.Rows)
        {
            var cells = new List<string?> { r.Host, r.Document, r.Reader, r.Path, r.ItemType, r.Item, r.Key, r.ItemId };
            cells.AddRange(cols.Select(c => r.Values.TryGetValue(c.Id, out var v) ? v.Value : null));
            WriteRow(w, cells);
        }
    }

    /// <summary>One row per property value, with its metadata (read-only, source, units...).</summary>
    public static void WriteLongCsv(ResultTable table, IReadOnlyCollection<string>? columnIds, string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        WriteRow(w, FixedColumns.Concat(new[]
        {
            "Group", "Property", "Value", "Raw Value", "Read Only", "Read Only Reason", "Source",
            "Storage Type", "Data Type", "Units", "Property Id",
        }));
        foreach (var r in table.Rows)
        {
            foreach (var (id, v) in r.Values)
            {
                if (columnIds is not null && !columnIds.Contains(id)) continue;
                int sep = id.IndexOf(" › ", StringComparison.Ordinal);
                WriteRow(w, new[]
                {
                    r.Host, r.Document, r.Reader, r.Path, r.ItemType, r.Item, r.Key, r.ItemId,
                    sep >= 0 ? id[..sep] : "", sep >= 0 ? id[(sep + 3)..] : id,
                    v.Value, v.RawValue, v.IsReadOnly ? "Yes" : "No", v.ReadOnlyReason, v.Source.ToString(),
                    v.StorageType, v.DataType, v.Units, v.Id,
                });
            }
        }
    }

    /// <summary>The raw results, exactly as the agents returned them.</summary>
    public static void WriteJson(IEnumerable<ResultSource> sources, string path)
    {
        var export = new
        {
            ExportedUtc = DateTime.UtcNow,
            Results = sources.Select(s => new { s.Host, s.DocumentTitle, s.Result }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(export, Json.Indented), new UTF8Encoding(false));
    }

    private static void WriteRow(TextWriter w, IEnumerable<string?> cells)
    {
        bool first = true;
        foreach (var cell in cells)
        {
            if (!first) w.Write(',');
            first = false;
            w.Write(Escape(cell));
        }
        w.Write("\r\n");
    }

    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Stop Excel from treating values as formulas.
        if (s[0] is '=' or '+' or '-' or '@' && !double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

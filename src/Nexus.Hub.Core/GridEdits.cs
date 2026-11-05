using System.Data;
namespace Nexus.Hub.Core;

/// <summary>
/// Whether a grid cell differs from the value read from the model. The grid edits rows through
/// IEditableObject, so a just-typed value sits in the row's "proposed" version until the row is
/// committed; compare the value the user sees (proposed if present, else current) with the
/// original from the last load.
/// </summary>
public static class GridEdits
{
    /// <summary>Cheap pre-check: rows never touched since loading cannot hold edits.</summary>
    public static bool MayBeEdited(DataRow row) =>
        row.RowState == DataRowState.Modified || row.HasVersion(DataRowVersion.Proposed);

    /// <summary>The value the user sees in the cell.</summary>
    public static string? Value(DataRow row, string column) =>
        (row.HasVersion(DataRowVersion.Proposed) ? row[column, DataRowVersion.Proposed] : row[column, DataRowVersion.Current]) as string;

    public static bool IsEdited(DataRow row, string column, out string? original)
    {
        original = null;
        if (row.RowState is DataRowState.Detached or DataRowState.Deleted || !row.HasVersion(DataRowVersion.Original)) return false;
        if (!MayBeEdited(row)) return false;
        original = row[column, DataRowVersion.Original] as string;
        return !Editing.SameValue(original, Value(row, column));
    }
}

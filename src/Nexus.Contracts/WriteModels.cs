namespace Nexus.Contracts;

/// <summary>Optional agent capabilities advertised in <see cref="HostInfo.Features"/>.</summary>
public static class AgentFeatures
{
    /// <summary>The agent accepts <see cref="MessageTypes.Write"/> requests.</summary>
    public const string Write = "write";
    /// <summary>The agent accepts <see cref="MessageTypes.Select"/> requests (show items in the host).</summary>
    public const string Select = "select";
    /// <summary>The agent accepts <see cref="MessageTypes.ExportPdf"/> requests.</summary>
    public const string ExportPdf = "exportPdf";
    /// <summary>The agent accepts <see cref="MessageTypes.CreateSheets"/> requests.</summary>
    public const string CreateSheets = "createSheets";
    /// <summary>
    /// <see cref="MessageTypes.CreateSheets"/> also updates existing sheets (<see cref="NewSheet.Update"/>), turns
    /// placeholders into sheets, shows sheets in the sheet list and puts a revision on them.
    /// </summary>
    public const string SyncSheets = "syncSheets";
    /// <summary>The agent offers tools for AI assistants (<see cref="MessageTypes.ListTools"/>, <see cref="MessageTypes.CallTool"/>).</summary>
    public const string Tools = "tools";
}

/// <summary>Show items in the host: open the sheet/layout, or select and zoom to objects.</summary>
public sealed class SelectRequest
{
    public string DocumentId { get; set; } = "";
    /// <summary><see cref="DataItem.Id"/> values (Revit UniqueId, AutoCAD handle).</summary>
    public List<string> ItemIds { get; set; } = new();
}

public sealed class SelectResult
{
    /// <summary>How many of the items were found and shown.</summary>
    public int Shown { get; set; }
    /// <summary>What the host did, for the status bar.</summary>
    public string Message { get; set; } = "";
}

/// <summary>A batch of property edits for one document. Applied as one undoable host operation.</summary>
public sealed class WriteRequest
{
    /// <summary>A <see cref="DocumentInfo.Id"/> (as returned in <see cref="ReadResult.DocumentId"/>).</summary>
    public string DocumentId { get; set; } = "";
    public List<PropertyChange> Changes { get; set; } = new();
}

/// <summary>Set one property of one object to a new value.</summary>
public sealed class PropertyChange
{
    /// <summary>Host id of the object that owns the property (<see cref="PropertyValue.OwnerId"/>, else the item's id).</summary>
    public string OwnerId { get; set; } = "";
    /// <summary><see cref="PropertyValue.Id"/> of the property.</summary>
    public string? PropertyId { get; set; }
    /// <summary><see cref="PropertyValue.Name"/>, used for messages and as a fallback lookup.</summary>
    public string PropertyName { get; set; } = "";
    /// <summary><see cref="PropertyValue.Source"/> as read.</summary>
    public PropertySource Source { get; set; }
    /// <summary>The new value as the user typed it (display units, e.g. 10' 6").</summary>
    public string Value { get; set; } = "";
    /// <summary>
    /// <see cref="PropertyValue.RawValue"/> when it was read. If the property has changed
    /// in the host since, the change is skipped rather than overwriting someone else's edit.
    /// </summary>
    public string? ExpectedRawValue { get; set; }
}

public enum ChangeStatus
{
    /// <summary>The value was set.</summary>
    Applied,
    /// <summary>The property already had this value.</summary>
    Unchanged,
    /// <summary>Not attempted (object not editable, changed since read, ...).</summary>
    Skipped,
    /// <summary>Attempted and rejected (bad value, host error, whole batch rolled back).</summary>
    Failed,
}

public sealed class ChangeResult
{
    /// <summary>Index into <see cref="WriteRequest.Changes"/>.</summary>
    public int Index { get; set; }
    public ChangeStatus Status { get; set; }
    public string? Message { get; set; }
    /// <summary>The value as the host now shows it.</summary>
    public string? NewValue { get; set; }
}

public sealed class WriteResult
{
    public string DocumentId { get; set; } = "";
    public string DocumentTitle { get; set; } = "";
    public long ElapsedMs { get; set; }
    /// <summary>False when the host rejected the batch and nothing was changed.</summary>
    public bool Committed { get; set; }
    /// <summary>Name of the undo entry in the host, when committed.</summary>
    public string? UndoName { get; set; }
    public List<ChangeResult> Results { get; set; } = new();
    /// <summary>Host warnings raised while committing (e.g. duplicate values).</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>Create sheets (e.g. drawings on a deliverables list that the model does not have yet).</summary>
public sealed class CreateSheetsRequest
{
    public string DocumentId { get; set; } = "";
    public List<NewSheet> Sheets { get; set; } = new();
    /// <summary>New sheets get no title block (dummy sheets for drawings made in another program).</summary>
    public bool NoTitleBlock { get; set; }
}

public sealed class NewSheet
{
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>
    /// Other sheet parameters to fill, by name (e.g. "Sheet Status" → "IFR"). A key may list alternative names
    /// separated by '|' ("Sheet Status|Status"): the first one the sheet has is set. Missing ones are reported.
    /// </summary>
    public Dictionary<string, string> Values { get; set; } = new();

    // Sync (agents with AgentFeatures.SyncSheets; others ignore these and skip existing numbers):
    /// <summary>A sheet with this number already there is updated (name, values, list, revision) instead of skipped.</summary>
    public bool Update { get; set; }
    /// <summary>A placeholder sheet with this number becomes a real sheet (same number, name and parameters), so it can carry revisions.</summary>
    public bool ReplacePlaceholder { get; set; }
    /// <summary>Set "Appears In Sheet List" (true: shown in the drawing index).</summary>
    public bool? ShowInSheetList { get; set; }
    /// <summary>Show the project revision with this number (e.g. "B", "0") on the sheet. It is never created: a missing one is reported.</summary>
    public string? Revision { get; set; }
}

public sealed class CreateSheetsResult
{
    public string DocumentTitle { get; set; } = "";
    /// <summary>Name of the undo entry in the host, when anything was created.</summary>
    public string? UndoName { get; set; }
    /// <summary>The title block used for the new sheets ("none" when the model has none).</summary>
    public string TitleBlock { get; set; } = "";
    public List<NewSheetResult> Sheets { get; set; } = new();
}

public sealed class NewSheetResult
{
    public string Number { get; set; } = "";
    public bool Created { get; set; }
    /// <summary>An existing sheet was changed (<see cref="NewSheet.Update"/>).</summary>
    public bool Updated { get; set; }
    /// <summary>What was changed on the sheet ("placeholder made a sheet", "renamed", "revision B shown"...).</summary>
    public List<string> Changes { get; set; } = new();
    /// <summary>The new sheet's id (UniqueId), when created.</summary>
    public string? Id { get; set; }
    /// <summary>Why it was not created, or what was left out (a parameter the sheet does not have).</summary>
    public string? Message { get; set; }
}

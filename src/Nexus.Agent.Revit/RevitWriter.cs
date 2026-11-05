using System.Globalization;
using Nexus.Agent;
using Nexus.Agent.Revit.Fabrication;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit;

/// <summary>
/// Applies hub edits to parameters. All changes to one document go into one
/// transaction, so a single Undo in Revit reverts the whole batch. If Revit
/// reports an error when committing, the batch is rolled back and nothing changes.
/// </summary>
internal sealed class RevitWriter : IHostDataWriter<Document>
{
    public WriteResult Write(Document doc, WriteRequest request, AgentLog log, CancellationToken ct)
    {
        var result = new WriteResult();
        for (int i = 0; i < request.Changes.Count; i++)
            result.Results.Add(new ChangeResult { Index = i, Status = ChangeStatus.Skipped });

        string? docBlocker = doc.IsLinked ? "Linked document (read-only)"
            : doc.IsReadOnlyFile ? "The file is open read-only"
            : doc.IsReadOnly ? "Revit is busy (for example showing a warning). Close any dialog in Revit and apply again"
            : doc.IsModifiable ? "Revit is in the middle of another edit" : null;
        if (docBlocker is not null)
        {
            foreach (var r in result.Results) r.Message = docBlocker;
            return result;
        }
        ct.ThrowIfCancellationRequested();

        string undoName = request.Changes.Count == 1
            ? $"Nexus: edit {request.Changes[0].PropertyName}"
            : $"Nexus: edit {request.Changes.Count} values";

        var failures = new FailureCollector();
        // Each applied change with a way to read its value again after the commit.
        var applied = new List<(ChangeResult Result, Func<string?> Current)>();

        using var transaction = new Transaction(doc, undoName);
        var handling = transaction.GetFailureHandlingOptions();
        handling.SetFailuresPreprocessor(failures);
        handling.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(handling);

        if (transaction.Start() != TransactionStatus.Started)
            throw new AgentException(ErrorCodes.HostBusy, "Revit could not start a transaction. Finish the current command or edit mode and try again.");

        try
        {
            // Several sheet numbers in one go (renumbering, swaps): park them on temporary numbers first,
            // because Revit refuses a number another sheet still has.
            var parked = ParkSheetNumbers(doc, request, result, log);

            for (int i = 0; i < request.Changes.Count; i++)
            {
                var change = request.Changes[i];
                var r = result.Results[i];
                if (parked.Skipped.Contains(i)) continue;
                try
                {
                    var current = Apply(doc, change, r, parked.Before.TryGetValue(i, out var was) ? was : null);
                    if (current is not null && r.Status == ChangeStatus.Applied) applied.Add((r, current));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    r.Status = ChangeStatus.Failed;
                    r.Message = ex.Message;
                    log.Warn($"Setting '{change.PropertyName}' on {change.OwnerId} failed: {ex.Message}");
                }
            }

            // A parked sheet that did not get its new number would keep the temporary one: undo the whole batch.
            var stuck = parked.Before.Keys.Where(i => result.Results[i].Status is not (ChangeStatus.Applied or ChangeStatus.Unchanged)).ToList();
            if (stuck.Count > 0)
            {
                transaction.RollBack();
                string why = string.Join("; ", stuck.Select(i => $"{request.Changes[i].ExpectedRawValue}: {result.Results[i].Message}"));
                foreach (var res in result.Results.Where(x => x.Status is ChangeStatus.Applied or ChangeStatus.Unchanged))
                {
                    res.Status = ChangeStatus.Failed;
                    res.Message = "Rolled back: the sheet renumbering could not finish (" + why + ")";
                    res.NewValue = null;
                }
                return result;
            }

            if (applied.Count == 0)
            {
                transaction.RollBack();
                return result;
            }

            var status = transaction.Commit();
            result.Warnings.AddRange(failures.Warnings);
            if (status != TransactionStatus.Committed)
            {
                string why = failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : $"Revit did not commit the changes ({status}).";
                foreach (var (r, _) in applied)
                {
                    r.Status = ChangeStatus.Failed;
                    r.Message = "Rolled back: " + why;
                    r.NewValue = null;
                }
                return result;
            }
        }
        catch
        {
            if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
            throw;
        }

        result.Committed = true;
        result.UndoName = undoName;
        // Report values as Revit shows them after the commit (formulas, formatting, rounding).
        foreach (var (r, current) in applied)
        {
            try { r.NewValue = current(); } catch { /* keep the pre-commit value */ }
        }
        return result;
    }

    private sealed record Parked(Dictionary<int, string> Before, HashSet<int> Skipped);

    /// <summary>
    /// When a batch sets two or more sheet numbers, gives each of those sheets a unique temporary number,
    /// so the final numbers can be set in any order (A101↔A102 swaps, shifting a range). Returns each
    /// parked change's number before the batch, and the changes skipped because they changed in Revit.
    /// </summary>
    private static Parked ParkSheetNumbers(Document doc, WriteRequest request, WriteResult result, AgentLog log)
    {
        var before = new Dictionary<int, string>();
        var skipped = new HashSet<int>();
        var numbers = Enumerable.Range(0, request.Changes.Count)
            .Where(i => request.Changes[i].Source == PropertySource.BuiltIn && request.Changes[i].PropertyId == nameof(BuiltInParameter.SHEET_NUMBER))
            .ToList();
        if (numbers.Count < 2) return new Parked(before, skipped);

        string tag = Guid.NewGuid().ToString("N")[..6];
        foreach (int i in numbers)
        {
            var change = request.Changes[i];
            var r = result.Results[i];
            if (doc.GetElement(change.OwnerId) is not ViewSheet sheet) continue; // Apply reports it
            if (Editability.Blocker(doc, sheet, askCentral: true) is { } blocker)
            {
                r.Message = blocker;
                skipped.Add(i);
                continue;
            }
            var p = sheet.get_Parameter(BuiltInParameter.SHEET_NUMBER);
            string current = ParameterReader.CurrentValue(doc, p).RawValue ?? "";
            if (change.ExpectedRawValue is not null && !string.Equals(current, change.ExpectedRawValue, StringComparison.Ordinal))
            {
                r.Message = $"Changed in Revit since it was read (now '{current}'). Run the reader again.";
                skipped.Add(i);
                continue;
            }
            try
            {
                if (p.Set($"~nexus-{tag}-{i}")) before[i] = current;
            }
            catch (Exception ex)
            {
                log.Warn($"Could not give sheet {current} a temporary number: {ex.Message}");
            }
        }
        return new Parked(before, skipped);
    }

    /// <summary>
    /// Sets one value inside the open transaction. Returns how to read the value back when it was set.
    /// <paramref name="parkedBefore"/>: the value before the batch, for a sheet number parked by <see cref="ParkSheetNumbers"/>.
    /// </summary>
    private static Func<string?>? Apply(Document doc, PropertyChange change, ChangeResult r, string? parkedBefore = null)
    {
        var element = string.IsNullOrEmpty(change.OwnerId) ? null : doc.GetElement(change.OwnerId);
        if (element is null)
        {
            r.Message = "The element no longer exists.";
            return null;
        }

        string? blocker = Editability.Blocker(doc, element, askCentral: true);
        if (blocker is not null)
        {
            r.Message = blocker;
            return null;
        }

        if (change.PropertyId?.StartsWith(RevisionsOnSheets.IdPrefix, StringComparison.Ordinal) == true)
            return ApplyRevisionOnSheet(doc, element, change, r);
        if (FabricationParts.Handles(change.PropertyId))
            return ApplyFabrication(doc, element, change, r);

        var p = FindParameter(element, change);
        if (p is null)
        {
            r.Status = ChangeStatus.Failed;
            r.Message = $"Parameter '{change.PropertyName}' was not found on the element.";
            return null;
        }
        if (p.IsReadOnly)
        {
            r.Message = "Read-only parameter (calculated or controlled by Revit).";
            return null;
        }

        var before = ParameterReader.CurrentValue(doc, p);
        if (parkedBefore is not null) before.RawValue = parkedBefore; // already checked when it was parked
        else if (change.ExpectedRawValue is not null && !string.Equals(before.RawValue ?? "", change.ExpectedRawValue, StringComparison.Ordinal))
        {
            r.Message = $"Changed in Revit since it was read (now '{before.Value}'). Run the reader again.";
            return null;
        }

        string? error = SetValue(p, change.Value);
        if (error is not null)
        {
            r.Status = ChangeStatus.Failed;
            r.Message = error;
            return null;
        }

        var after = ParameterReader.CurrentValue(doc, p);
        r.NewValue = after.Value;
        r.Status = string.Equals(after.RawValue, before.RawValue, StringComparison.Ordinal) ? ChangeStatus.Unchanged : ChangeStatus.Applied;
        return () => ParameterReader.CurrentValue(doc, p).Value;
    }

    /// <summary>Shows or hides a revision on a sheet ("Revisions on Sheet").</summary>
    private static Func<string?>? ApplyRevisionOnSheet(Document doc, Element element, PropertyChange change, ChangeResult r)
    {
        if (element is not ViewSheet sheet)
        {
            r.Status = ChangeStatus.Failed;
            r.Message = "Revisions can only be shown on sheets.";
            return null;
        }
        if (doc.GetElement(change.PropertyId![RevisionsOnSheets.IdPrefix.Length..]) is not Revision revision)
        {
            r.Message = "The revision no longer exists.";
            return null;
        }

        string before = RevisionsOnSheets.State(sheet, revision);
        if (change.ExpectedRawValue is not null && !string.Equals(before, change.ExpectedRawValue, StringComparison.Ordinal))
        {
            r.Message = $"Changed in Revit since it was read (now '{before}'). Run the reader again.";
            return null;
        }
        bool? show = ParseYesNo(change.Value);
        if (show is null)
        {
            r.Status = ChangeStatus.Failed;
            r.Message = $"'{change.Value}' is not Yes or No.";
            return null;
        }

        string? error = RevisionsOnSheets.Set(sheet, revision, show.Value, out bool changed);
        if (error is not null)
        {
            r.Status = ChangeStatus.Failed;
            r.Message = error;
            return null;
        }
        // The sheet's revision list updates when Revit regenerates (at commit); report from what was set.
        r.NewValue = show.Value ? "Yes" : "No";
        r.Status = changed ? ChangeStatus.Applied : ChangeStatus.Unchanged;
        return () => RevisionsOnSheets.State(sheet, revision);
    }

    /// <summary>Fabrication data of an MEP Fabrication part (item number, spool, status, custom data...).</summary>
    private static Func<string?>? ApplyFabrication(Document doc, Element element, PropertyChange change, ChangeResult r)
    {
        if (element is not FabricationPart part)
        {
            r.Status = ChangeStatus.Failed;
            r.Message = "This is not a fabrication part.";
            return null;
        }
        var config = FabricationDatabase.Configured(doc);
        if (config is null)
        {
            r.Message = FabricationDatabase.NotConfigured;
            return null;
        }

        string id = change.PropertyId!;
        string before = FabricationParts.Current(part, config, id);
        if (change.ExpectedRawValue is not null && !string.Equals(before, change.ExpectedRawValue, StringComparison.Ordinal))
        {
            r.Message = $"Changed in Revit since it was read (now '{before}'). Run the reader again.";
            return null;
        }

        string? error = FabricationParts.Set(part, config, id, change.Value);
        if (error is not null)
        {
            r.Status = ChangeStatus.Failed;
            r.Message = error;
            return null;
        }
        string after = FabricationParts.Current(part, config, id);
        r.NewValue = after;
        r.Status = string.Equals(after, before, StringComparison.Ordinal) ? ChangeStatus.Unchanged : ChangeStatus.Applied;
        return () => FabricationParts.Current(part, config, id);
    }

    /// <summary>Returns null on success, else why the value was not accepted.</summary>
    private static string? SetValue(Parameter p, string value)
    {
        value ??= "";
        switch (p.StorageType)
        {
            case StorageType.String:
                return p.Set(value) ? null : "Revit rejected the value.";

            case StorageType.Integer:
                if (IsYesNo(p))
                {
                    bool? b = ParseYesNo(value);
                    if (b is null) return $"'{value}' is not Yes or No.";
                    return p.Set(b.Value ? 1 : 0) ? null : "Revit rejected the value.";
                }
                if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int i)
                    || int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                    return p.Set(i) ? null : "Revit rejected the value.";
                return TrySetValueString(p, value) ? null : $"'{value}' is not a whole number.";

            case StorageType.Double:
                if (string.IsNullOrWhiteSpace(value)) return "A number is required.";
                // Parses in the project's display units, e.g. 10' 6" or 3200 mm.
                return TrySetValueString(p, value) ? null : $"'{value}' is not a valid {DataTypeLabel(p)} value.";

            case StorageType.ElementId:
                return "Editing values that refer to other elements (materials, types, levels...) is not supported yet.";

            default:
                return "This parameter has no editable value.";
        }
    }

    private static bool TrySetValueString(Parameter p, string value)
    {
        try { return p.SetValueString(value.Trim()); }
        catch { return false; }
    }

    private static bool IsYesNo(Parameter p)
    {
        try { return p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo; }
        catch { return false; }
    }

    private static bool? ParseYesNo(string value) => value.Trim().ToLowerInvariant() switch
    {
        "yes" or "y" or "true" or "1" or "on" or "x" => true,
        "no" or "n" or "false" or "0" or "off" or "" => false,
        _ => null,
    };

    private static string DataTypeLabel(Parameter p)
    {
        try { return LabelUtils.GetLabelForSpec(p.Definition.GetDataType()).ToLowerInvariant(); }
        catch { return "number"; }
    }

    private static Parameter? FindParameter(Element element, PropertyChange change)
    {
        string id = change.PropertyId ?? "";
        switch (change.Source)
        {
            case PropertySource.BuiltIn when Enum.TryParse<BuiltInParameter>(id, out var bip):
                return element.get_Parameter(bip);
            case PropertySource.Shared when Guid.TryParse(id, out var guid):
                return element.get_Parameter(guid);
        }

        if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long paramId))
            foreach (Parameter p in element.Parameters)
                if (p.Id.Value == paramId) return p;

        var byName = element.GetParameters(change.PropertyName);
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Turns commit-time warnings into messages for the hub instead of Revit dialogs; errors roll back.</summary>
    internal sealed class FailureCollector : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = new();
        public List<string> Errors { get; } = new();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool error = false;
            foreach (var f in accessor.GetFailureMessages())
            {
                string text = f.GetDescriptionText();
                if (f.GetSeverity() == FailureSeverity.Warning)
                {
                    if (!Warnings.Contains(text)) Warnings.Add(text);
                    accessor.DeleteWarning(f);
                }
                else
                {
                    if (!Errors.Contains(text)) Errors.Add(text);
                    error = true;
                }
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}

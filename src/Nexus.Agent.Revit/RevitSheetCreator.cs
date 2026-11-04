using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit;

/// <summary>
/// Creates sheets from the hub (drawings on the deliverables list that the model does not have yet), and keeps dummy
/// sheets in step with drawings made in other programs (sync: <see cref="NewSheet.Update"/>). All changes go into one
/// transaction, so one Undo in Revit removes them. New sheets get the title block the model's sheets use most (or none
/// for dummy sheets); a number that already exists is skipped unless the request asks to update it.
/// </summary>
internal sealed class RevitSheetCreator : ISheetCreator<Document>
{
    public CreateSheetsResult Create(Document doc, CreateSheetsRequest request, AgentLog log, CancellationToken ct)
    {
        var result = new CreateSheetsResult();
        string? blocker = doc.IsLinked ? "Linked document (read-only)"
            : doc.IsReadOnlyFile ? "The file is open read-only"
            : doc.IsReadOnly ? "Revit is busy (for example showing a warning). Close any dialog in Revit and try again"
            : doc.IsModifiable ? "Revit is in the middle of another edit" : null;
        if (blocker is not null)
        {
            result.Sheets.AddRange(request.Sheets.Select(s => new NewSheetResult { Number = s.Number, Message = blocker }));
            return result;
        }

        var sheets = new Dictionary<string, ViewSheet>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            if (!sheets.ContainsKey(s.SheetNumber)) sheets[s.SheetNumber] = s;
        var titleBlock = request.NoTitleBlock ? null : TitleBlock(doc);
        var revisions = request.Sheets.Any(s => !string.IsNullOrWhiteSpace(s.Revision)) ? RevisionsOnSheets.ProjectRevisions(doc) : new List<Revision>();
        result.TitleBlock = titleBlock is null ? "none" : $"{titleBlock.FamilyName}: {titleBlock.Name}";

        bool sync = request.Sheets.Any(s => s.Update);
        string undoName = sync ? $"Nexus: sync {request.Sheets.Count} sheet(s)"
            : request.Sheets.Count == 1 ? $"Nexus: create sheet {request.Sheets[0].Number}" : $"Nexus: create {request.Sheets.Count} sheets";
        var failures = new RevitWriter.FailureCollector();
        using var transaction = new Transaction(doc, undoName);
        var handling = transaction.GetFailureHandlingOptions();
        handling.SetFailuresPreprocessor(failures);
        handling.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(handling);
        if (transaction.Start() != TransactionStatus.Started)
            throw new AgentException(ErrorCodes.HostBusy, "Revit could not start a transaction. Finish the current command or edit mode and try again.");

        var created = new List<NewSheetResult>();
        try
        {
            if (titleBlock is not null && !titleBlock.IsActive) titleBlock.Activate();
            foreach (var wanted in request.Sheets)
            {
                ct.ThrowIfCancellationRequested();
                var r = new NewSheetResult { Number = wanted.Number };
                result.Sheets.Add(r);
                string number = wanted.Number.Trim();
                if (number.Length == 0)
                {
                    r.Message = "No sheet number";
                    continue;
                }
                // Each sheet in its own sub-transaction: a failure rolls back that sheet only (a placeholder that was
                // already deleted comes back).
                sheets.TryGetValue(number, out var original);
                if (original is not null && !wanted.Update)
                {
                    r.Message = "A sheet with this number already exists";
                    continue;
                }
                using var step = new SubTransaction(doc);
                step.Start();
                try
                {
                    var sheet = original;
                    if (sheet is null)
                    {
                        sheet = NewSheet(doc, titleBlock, number);
                        sheets[number] = sheet;
                        r.Created = true;
                    }
                    else if (sheet.IsPlaceholder && wanted.ReplacePlaceholder)
                    {
                        sheet = Replace(doc, sheet, titleBlock);
                        sheets[number] = sheet;
                        r.Changes.Add("placeholder made a sheet");
                    }

                    string name = wanted.Name.Trim();
                    if (name.Length > 0 && !string.Equals(sheet.Name, name, StringComparison.Ordinal))
                    {
                        sheet.Name = name;
                        if (!r.Created) r.Changes.Add("renamed");
                    }
                    var missing = new List<string>();
                    foreach (var (names, value) in wanted.Values)
                    {
                        if (value.Trim().Length == 0) continue;
                        var p = names.Split('|').Select(n => sheet.LookupParameter(n.Trim()))
                            .FirstOrDefault(x => x is not null && !x.IsReadOnly && x.StorageType == StorageType.String);
                        if (p is null) missing.Add(names.Split('|')[0]);
                        else if (!string.Equals(p.AsString() ?? "", value.Trim(), StringComparison.Ordinal))
                        {
                            p.Set(value.Trim());
                            if (!r.Created) r.Changes.Add(p.Definition.Name);
                        }
                    }
                    if (wanted.ShowInSheetList is bool show && sheet.get_Parameter(BuiltInParameter.SHEET_SCHEDULED) is { IsReadOnly: false } listed
                        && (listed.AsInteger() == 1) != show)
                    {
                        listed.Set(show ? 1 : 0);
                        if (!r.Created) r.Changes.Add(show ? "shown in sheet list" : "hidden from sheet list");
                    }
                    var notes = new List<string>();
                    if (missing.Count > 0) notes.Add("Not set (the sheet has no such text parameter): " + string.Join(", ", missing));
                    if (!string.IsNullOrWhiteSpace(wanted.Revision) && ShowRevision(sheet, wanted.Revision!.Trim(), revisions, r) is { } why) notes.Add(why);
                    if (notes.Count > 0) r.Message = string.Join(". ", notes);

                    r.Id = sheet.UniqueId;
                    r.Updated = !r.Created && r.Changes.Count > 0;
                    if (r.Created || r.Updated) created.Add(r);
                    step.Commit();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (step.HasStarted() && !step.HasEnded()) step.RollBack();
                    if (original is not null) sheets[number] = original;
                    else sheets.Remove(number);
                    r.Created = false;
                    r.Updated = false;
                    r.Changes.Clear();
                    r.Id = null;
                    r.Message = ex.Message;
                    log.Warn($"Sheet {number}: {ex.Message}");
                }
            }

            if (created.Count == 0)
            {
                transaction.RollBack();
                return result;
            }
            if (transaction.Commit() != TransactionStatus.Committed)
            {
                string why = failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : "Revit did not commit the new sheets.";
                foreach (var r in created)
                {
                    r.Created = false;
                    r.Updated = false;
                    r.Changes.Clear();
                    r.Id = null;
                    r.Message = "Rolled back: " + why;
                }
                return result;
            }
        }
        catch
        {
            if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
            throw;
        }
        result.UndoName = undoName;
        return result;
    }

    private static ViewSheet NewSheet(Document doc, FamilySymbol? titleBlock, string number)
    {
        var sheet = ViewSheet.Create(doc, titleBlock?.Id ?? ElementId.InvalidElementId);
        sheet.SheetNumber = number;
        return sheet;
    }

    /// <summary>
    /// A placeholder becomes a real sheet: the placeholder is deleted and a sheet with the same number, name, text
    /// parameters and sheet-list setting takes its place (Revit has no "convert" in its API). Placeholders cannot show
    /// revisions; sheets can.
    /// </summary>
    private static ViewSheet Replace(Document doc, ViewSheet placeholder, FamilySymbol? titleBlock)
    {
        string number = placeholder.SheetNumber, name = placeholder.Name;
        var texts = new List<(string Name, string Value)>();
        foreach (Parameter p in placeholder.Parameters)
        {
            if (p.IsReadOnly || p.StorageType != StorageType.String || !p.HasValue || p.Definition is null) continue;
            if (p.Id.Value == (long)BuiltInParameter.SHEET_NUMBER || p.Id.Value == (long)BuiltInParameter.SHEET_NAME) continue;
            texts.Add((p.Definition.Name, p.AsString() ?? ""));
        }
        int listed = placeholder.get_Parameter(BuiltInParameter.SHEET_SCHEDULED)?.AsInteger() ?? 1;
        doc.Delete(placeholder.Id);

        var sheet = NewSheet(doc, titleBlock, number);
        sheet.Name = name;
        foreach (var (pname, value) in texts)
            if (sheet.LookupParameter(pname) is { IsReadOnly: false, StorageType: StorageType.String } p) p.Set(value);
        if (sheet.get_Parameter(BuiltInParameter.SHEET_SCHEDULED) is { IsReadOnly: false } s) s.Set(listed);
        return sheet;
    }

    /// <summary>
    /// Shows the project revision numbered <paramref name="number"/> on the sheet (added to its revisions, like Revit's
    /// Revisions on Sheet). Returns why not, or null. A sheet already showing a later revision is left alone.
    /// </summary>
    private static string? ShowRevision(ViewSheet sheet, string number, IReadOnlyList<Revision> revisions, NewSheetResult r)
    {
        if (sheet.IsPlaceholder) return $"Revision {number} not shown: placeholder sheets cannot show revisions";
        var revision = revisions.LastOrDefault(x => string.Equals((x.RevisionNumber ?? "").Trim(), number, StringComparison.OrdinalIgnoreCase));
        if (revision is null) return $"No revision numbered {number} in this model (add it in Revit's Sheet Issues/Revisions)";
        var on = sheet.GetAllRevisionIds();
        if (on.Contains(revision.Id)) return null;
        var later = on.Select(id => sheet.Document.GetElement(id)).OfType<Revision>()
            .Where(x => x.SequenceNumber > revision.SequenceNumber).OrderByDescending(x => x.SequenceNumber).FirstOrDefault();
        if (later is not null) return $"Revision {number} not added: the sheet already shows a later revision ({later.RevisionNumber})";
        try
        {
            var added = sheet.GetAdditionalRevisionIds().ToList();
            added.Add(revision.Id);
            sheet.SetAdditionalRevisionIds(added);
            r.Changes.Add($"revision {number} shown");
            return null;
        }
        catch (Exception ex)
        {
            return $"Revision {number} not shown: {ex.Message}" + (revision.Issued ? " (it is marked Issued in Revit)" : "");
        }
    }

    /// <summary>The title block type placed on the most sheets; else the first one loaded; null when there is none.</summary>
    private static FamilySymbol? TitleBlock(Document doc)
    {
        var used = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(i => doc.GetElement(i.OwnerViewId) is ViewSheet)
            .GroupBy(i => i.Symbol.Id)
            .OrderByDescending(g => g.Count())
            .Select(g => doc.GetElement(g.Key) as FamilySymbol)
            .FirstOrDefault(s => s is not null);
        return used ?? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>().FirstOrDefault();
    }
}

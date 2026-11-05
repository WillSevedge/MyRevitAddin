using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Nexus.Agent;
using Nexus.Agent.Acad.Modules;
using Nexus.Agent.Acad.PropertyEngine;
using Nexus.Agent.Acad.Readers;
using Nexus.Contracts;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace Nexus.Agent.Acad;

/// <summary>
/// Applies hub edits to a drawing: block attributes, table cells, dynamic block
/// properties, .NET API properties, COM (Properties palette) properties, drawing
/// settings and system variables. Runs on AutoCAD's main thread with the document
/// locked, so the changes go into AutoCAD's undo history.
/// </summary>
internal sealed class AcadWriter : IHostDataWriter<Document>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Writers added by vertical modules (Plant 3D...); filled after AutoCAD finished starting.</summary>
    private readonly IReadOnlyList<IAcadPropertyWriter> _moduleWriters;

    public AcadWriter(IReadOnlyList<IAcadPropertyWriter>? moduleWriters = null) =>
        _moduleWriters = moduleWriters ?? Array.Empty<IAcadPropertyWriter>();

    public WriteResult Write(Document doc, WriteRequest request, AgentLog log, CancellationToken ct)
    {
        var result = new WriteResult();
        for (int i = 0; i < request.Changes.Count; i++)
            result.Results.Add(new ChangeResult { Index = i, Status = ChangeStatus.Skipped });

        if (doc.IsReadOnly)
        {
            foreach (var r in result.Results) r.Message = "Drawing is read-only.";
            return result;
        }
        ct.ThrowIfCancellationRequested();

        DocumentLock docLock;
        try { docLock = doc.LockDocument(); }
        catch (Exception ex)
        {
            throw new AgentException(ErrorCodes.HostBusy, $"'{Path.GetFileName(doc.Name)}' is busy (is a command running?). Try again when AutoCAD is idle.", ex);
        }

        using (docLock)
        {
            var db = doc.Database;
            string docId = AcadDocumentProvider.Id(doc);
            bool isActive = ReferenceEquals(doc, AcApp.DocumentManager.MdiActiveDocument);
            var comChanges = new List<int>();
            var moduleChanges = new List<(int Index, IAcadPropertyWriter Writer)>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                // Title blocks usually sit on locked layers: unlock them for the edit, lock them again before committing.
                _layers = new LockedLayers();
                for (int i = 0; i < request.Changes.Count; i++)
                {
                    var change = request.Changes[i];
                    var moduleWriter = _moduleWriters.FirstOrDefault(w => w.Handles(change));
                    if (moduleWriter is not null)
                    {
                        moduleChanges.Add((i, moduleWriter));
                        continue;
                    }
                    if (change.Source == PropertySource.Com)
                    {
                        comChanges.Add(i);
                        continue;
                    }
                    Run(result.Results[i], change, log, () => Apply(db, tr, docId, isActive, change, result.Results[i]));
                }
                _layers.RelockAll(tr);
                tr.Commit();
                _layers = null;
            }

            // COM setters open the object themselves, so they run after the transaction has closed everything.
            foreach (int i in comChanges)
            {
                var change = request.Changes[i];
                Run(result.Results[i], change, log, () => ApplyCom(db, change, result.Results[i]));
            }

            // Module data (e.g. Plant 3D properties) is stored by the module's own API, outside our transaction.
            foreach (var (i, writer) in moduleChanges)
            {
                var change = request.Changes[i];
                var r = result.Results[i];
                Run(r, change, log, () =>
                {
                    if (!long.TryParse(change.OwnerId, NumberStyles.HexNumber, Inv, out long h) || !db.TryGetObjectId(new Handle(h), out var id) || id.IsErased)
                    {
                        r.Message = "The object no longer exists.";
                        return;
                    }
                    writer.Apply(doc, id, change, r);
                });
            }
        }

        result.Committed = result.Results.Any(r => r.Status == ChangeStatus.Applied);
        if (result.Committed) result.UndoName = "Nexus edit (use U in AutoCAD to undo)";
        return result;
    }

    private static void Run(ChangeResult r, PropertyChange change, AgentLog log, Action apply)
    {
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            r.Status = ChangeStatus.Failed;
            r.Message = inner is FormatException ? inner.Message : $"AutoCAD rejected the value: {inner.Message}";
            log.Warn($"Setting '{change.PropertyName}' on {change.OwnerId} failed: {inner.Message}");
        }
    }

    // ------------------------------------------------------------------ transaction changes

    private static void Apply(Database db, Transaction tr, string docId, bool isActive, PropertyChange change, ChangeResult r)
    {
        // Drawing-level item: system variables and database settings.
        if (change.OwnerId == docId)
        {
            if (change.PropertyId?.StartsWith(SheetsReader.DwgPropsPrefix, StringComparison.Ordinal) == true)
            {
                string propId = change.PropertyId;
                SetChecked(r, change, () => SheetsReader.CurrentDwgProp(db, propId), () => SheetsReader.SetDwgProp(db, propId, change.Value));
                return;
            }
            if (change.Source == PropertySource.Setting && change.PropertyId is { } id && !id.Contains('.'))
            {
                if (!isActive)
                {
                    r.Message = "System variables can only be changed in the active drawing.";
                    return;
                }
                ApplySystemVariable(id, change, r);
                return;
            }
            ApplyManaged(db, tr, change, r);
            return;
        }

        var obj = Open(db, tr, change.OwnerId, r);
        if (obj is null) return;

        switch (obj)
        {
            case Layout layout when change.PropertyId == SheetsReader.LayoutNameId:
                SetChecked(r, change, () => layout.LayoutName, () =>
                {
                    string name = change.Value.Trim();
                    if (name.Length == 0) throw new FormatException("A layout name is required.");
                    LayoutManager.Current.RenameLayout(layout.LayoutName, name);
                });
                return;

            case AttributeReference ar:
                SetChecked(r, change, () => ar.IsMTextAttribute ? ar.MTextAttribute?.Contents ?? ar.TextString : ar.TextString,
                    () =>
                    {
                        if (ar.IsConstant) throw new FormatException("Constant attributes cannot be changed.");
                        ar.UpgradeOpen();
                        if (ar.IsMTextAttribute)
                        {
                            var mt = ar.MTextAttribute;
                            mt.Contents = change.Value;
                            ar.MTextAttribute = mt;
                            ar.UpdateMTextAttribute();
                        }
                        else
                        {
                            ar.TextString = change.Value;
                        }
                    });
                return;

            case Table table when change.PropertyId?.StartsWith("Cell:", StringComparison.Ordinal) == true:
                var rc = change.PropertyId[5..].Split(',');
                int row = int.Parse(rc[0], Inv), col = int.Parse(rc[1], Inv);
                SetChecked(r, change, () => table.Cells[row, col].TextString ?? "", () =>
                {
                    table.UpgradeOpen();
                    table.Cells[row, col].TextString = change.Value;
                });
                return;

            case BlockReference br when change.Source == PropertySource.DynamicBlock:
                var dyn = br.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>()
                    .FirstOrDefault(p => p.PropertyName == change.PropertyName);
                if (dyn is null) throw new FormatException($"Dynamic property '{change.PropertyName}' was not found.");
                if (dyn.ReadOnly) throw new FormatException("Read-only dynamic property.");
                SetChecked(r, change, () => ValueFormatter.Format(dyn.Value, tr).Raw, () =>
                {
                    br.UpgradeOpen();
                    object? current = dyn.Value;
                    var allowed = dyn.GetAllowedValues();
                    object value = ValueParser.Parse(change.Value, current?.GetType() ?? typeof(string))!;
                    if (allowed is { Length: > 0 } && !allowed.Any(a => Equals(a, value) || string.Equals(Convert.ToString(a, Inv), change.Value.Trim(), StringComparison.OrdinalIgnoreCase)))
                        throw new FormatException($"'{change.Value}' is not allowed. Choose one of: {string.Join(", ", allowed.Select(a => Convert.ToString(a, Inv)))}.");
                    dyn.Value = value;
                }, () => ValueFormatter.Format(dyn.Value, tr).Display);
                return;

            default:
                ApplyManaged(obj, tr, change, r);
                return;
        }
    }

    private static void ApplyManaged(object target, Transaction tr, PropertyChange change, ChangeResult r)
    {
        string name = change.PropertyId is { } id && id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : change.PropertyName;
        var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || prop.GetIndexParameters().Length > 0)
            throw new FormatException($"Property '{change.PropertyName}' was not found.");
        if (prop.SetMethod is not { IsPublic: true })
        {
            r.Message = "Read-only in the API.";
            return;
        }

        SetChecked(r, change, () => ValueFormatter.Format(prop.GetValue(target), tr).Raw, () =>
        {
            if (target is DBObject { IsWriteEnabled: false } dbo) dbo.UpgradeOpen();
            prop.SetValue(target, ValueParser.Parse(change.Value, prop.PropertyType));
        }, () => ValueFormatter.Format(prop.GetValue(target), tr).Display);
    }

    private static void ApplySystemVariable(string name, PropertyChange change, ChangeResult r)
    {
        SetChecked(r, change, () => ValueFormatter.Format(AcApp.GetSystemVariable(name), null).Raw, () =>
        {
            object? current = AcApp.GetSystemVariable(name);
            AcApp.SetSystemVariable(name, ValueParser.Parse(change.Value, current?.GetType() ?? typeof(string))!);
        }, () => ValueFormatter.Format(AcApp.GetSystemVariable(name), null).Display);
    }

    // ------------------------------------------------------------------ COM changes

    private static void ApplyCom(Database db, PropertyChange change, ChangeResult r)
    {
        object? com;
        var layers = new LockedLayers();
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var obj = Open(db, tr, change.OwnerId, r, layers);
            if (obj is null) return;
            com = obj.AcadObject;
            tr.Commit();
        }
        try
        {
            ApplyComValue(com, change, r);
        }
        finally
        {
            if (layers.Any)
            {
                using var tr = db.TransactionManager.StartTransaction();
                layers.RelockAll(tr);
                tr.Commit();
            }
        }
    }

    private static void ApplyComValue(object? com, PropertyChange change, ChangeResult r)
    {
        if (com is null || !Marshal.IsComObject(com)) throw new FormatException("This object has no ActiveX properties.");

        // Id looks like "IAcadLine.Layer (dispid 1234)".
        string name = change.PropertyName;
        if (change.PropertyId is { } id)
        {
            int dot = id.IndexOf('.'), paren = id.IndexOf(" (", StringComparison.Ordinal);
            if (dot >= 0) name = paren > dot ? id[(dot + 1)..paren] : id[(dot + 1)..];
        }

        object? Get() => com.GetType().InvokeMember(name, BindingFlags.GetProperty, null, com, null, Inv);
        SetChecked(r, change, () => ValueFormatter.Format(Get(), null).Raw, () =>
        {
            object? current = Get();
            object? value = ValueParser.Parse(change.Value, current?.GetType() ?? typeof(string));
            com.GetType().InvokeMember(name, BindingFlags.SetProperty, null, com, new[] { value }, Inv);
        }, () => ValueFormatter.Format(Get(), null).Display);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Layers unlocked during the current transaction write.</summary>
    [ThreadStatic] private static LockedLayers? _layers;

    /// <summary>
    /// Opens the object for read; null (with a reason) if it is gone. If it (or, for an attribute, its
    /// block) is on a locked layer, the layer is unlocked so the edit can be made; it is locked again afterwards.
    /// </summary>
    private static DBObject? Open(Database db, Transaction tr, string handle, ChangeResult r, LockedLayers? layers = null)
    {
        if (!long.TryParse(handle, NumberStyles.HexNumber, Inv, out long h) || !db.TryGetObjectId(new Handle(h), out var id) || id.IsErased)
        {
            r.Message = "The object no longer exists.";
            return null;
        }
        var obj = tr.GetObject(id, OpenMode.ForRead, false, true);
        layers ??= _layers;
        if (layers is not null)
        {
            layers.Unlock(tr, obj);
            if (obj is AttributeReference ar && !ar.OwnerId.IsNull)
                layers.Unlock(tr, tr.GetObject(ar.OwnerId, OpenMode.ForRead));
        }
        return obj;
    }

    private sealed class LockedLayers
    {
        private readonly HashSet<ObjectId> _unlocked = new();

        public bool Any => _unlocked.Count > 0;

        public void Unlock(Transaction tr, DBObject obj)
        {
            if (obj is not Entity e || _unlocked.Contains(e.LayerId)) return;
            if (tr.GetObject(e.LayerId, OpenMode.ForRead) is not LayerTableRecord layer || !layer.IsLocked) return;
            layer.UpgradeOpen();
            layer.IsLocked = false;
            _unlocked.Add(e.LayerId);
        }

        public void RelockAll(Transaction tr)
        {
            foreach (var id in _unlocked)
            {
                if (tr.GetObject(id, OpenMode.ForWrite) is LayerTableRecord layer) layer.IsLocked = true;
            }
            _unlocked.Clear();
        }
    }

    /// <summary>Skips if the value changed since it was read, sets it, and reports Applied/Unchanged.</summary>
    private static void SetChecked(ChangeResult r, PropertyChange change, Func<string?> currentRaw, Action set, Func<string?>? display = null)
    {
        string? before = currentRaw();
        if (change.ExpectedRawValue is not null && !string.Equals(before ?? "", change.ExpectedRawValue, StringComparison.Ordinal))
        {
            r.Message = $"Changed in the drawing since it was read (now '{before}'). Run the reader again.";
            return;
        }
        set();
        string? after = currentRaw();
        r.NewValue = display is null ? after : display();
        r.Status = string.Equals(after, before, StringComparison.Ordinal) ? ChangeStatus.Unchanged : ChangeStatus.Applied;
    }
}

using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit;

/// <summary>PDFs of sheets of an open model (Revit's own PDF export, each sheet at its title block size).</summary>
internal sealed class RevitPdfExporter : IPdfExporter<Document>
{
    public ExportPdfResult Export(Document doc, ExportPdfRequest request, AgentLog log, CancellationToken ct)
    {
        var result = new ExportPdfResult();
        var sheets = new List<ViewSheet>();
        if (request.ItemIds.Count == 0)
        {
            sheets.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && !s.IsTemplate).OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            foreach (var id in request.ItemIds)
            {
                if (doc.GetElement(id) is ViewSheet s) sheets.Add(s);
                else result.Sheets.Add(new ExportedSheet { ItemId = id, Error = "Sheet not found in this model (it may have been deleted)." });
            }
        }

        string file = System.IO.Path.GetFileNameWithoutExtension(doc.PathName is { Length: > 0 } p ? p : doc.Title);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();
            var entry = new ExportedSheet { ItemId = sheet.UniqueId, Number = sheet.SheetNumber, Name = sheet.Name };
            result.Sheets.Add(entry);
            if (sheet.IsPlaceholder)
            {
                entry.Error = "Placeholder sheet (nothing to print).";
                continue;
            }
            try
            {
                string target = PdfNames.Unique(request.OutputFolder, PdfNames.For(request.FileNamePattern, sheet.SheetNumber, sheet.Name, file), taken);
                if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
                var options = new PDFExportOptions
                {
                    Combine = true,
                    FileName = System.IO.Path.GetFileNameWithoutExtension(target),
                    PaperFormat = ExportPaperFormat.Default, // the title block's size
                    HideCropBoundaries = true,
                    HideReferencePlane = true,
                    HideScopeBoxes = true,
                    HideUnreferencedViewTags = true,
                };
                bool ok = doc.Export(request.OutputFolder, new List<ElementId> { sheet.Id }, options);
                if (ok && System.IO.File.Exists(target)) entry.PdfPath = target;
                else entry.Error = "Revit did not create the PDF.";
            }
            catch (Exception ex)
            {
                entry.Error = ex.Message;
                log.Warn($"PDF of sheet {sheet.SheetNumber} failed.", ex);
            }
        }
        return result;
    }
}

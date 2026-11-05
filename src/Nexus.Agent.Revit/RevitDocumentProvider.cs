using System.Security.Cryptography;
using System.Text;
using Nexus.Agent;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit;

/// <summary>Lists and resolves open Revit documents. Host thread only.</summary>
internal sealed class RevitDocumentProvider : IDocumentProvider<Document>
{
    private readonly RevitDispatcher _dispatcher;

    public RevitDocumentProvider(RevitDispatcher dispatcher) => _dispatcher = dispatcher;

    private Autodesk.Revit.UI.UIApplication App =>
        _dispatcher.Current ?? throw new InvalidOperationException("Revit documents can only be read from the Revit API thread.");

    public IReadOnlyList<DocumentInfo> ListDocuments()
    {
        var app = App;
        var active = app.ActiveUIDocument?.Document;
        var list = new List<DocumentInfo>();
        foreach (Document doc in app.Application.Documents)
        {
            var info = Describe(doc);
            info.IsActive = active is not null && Id(active) == info.Id;
            list.Add(info);
        }
        // Active first, then host documents, then links/families.
        return list
            .OrderByDescending(d => d.IsActive)
            .ThenBy(d => d.Extra.GetValueOrDefault("IsLinked") == "True")
            .ThenBy(d => d.Extra.GetValueOrDefault("IsFamily") == "True")
            .ThenBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Document? Find(string documentId)
    {
        var app = App;
        if (string.Equals(documentId, "active", StringComparison.OrdinalIgnoreCase))
            return app.ActiveUIDocument?.Document;

        foreach (Document doc in app.Application.Documents)
            if (Id(doc) == documentId) return doc;
        return null;
    }

    public DocumentInfo Describe(Document doc)
    {
        var info = new DocumentInfo
        {
            Id = Id(doc),
            Title = doc.Title,
            Path = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
            IsReadOnly = doc.IsReadOnlyFile,
            IsModified = doc.IsModified,
        };
        info.Extra["IsWorkshared"] = doc.IsWorkshared.ToString();
        info.Extra["IsLinked"] = doc.IsLinked.ToString();
        info.Extra["IsFamily"] = doc.IsFamilyDocument.ToString();
        if (doc.IsWorkshared)
        {
            try
            {
                var central = doc.GetWorksharingCentralModelPath();
                if (central is not null)
                    info.Extra["CentralModel"] = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
            }
            catch { /* not available for detached models */ }
        }
        return info;
    }

    /// <summary>Stable id for the life of the session: hash of path/title/link state.</summary>
    public static string Id(Document doc)
    {
        string key = $"{doc.PathName}|{doc.Title}|{doc.IsLinked}".ToLowerInvariant();
        return "rvt-" + Compat.Sha1Hex(key, 6).ToLowerInvariant();
    }
}

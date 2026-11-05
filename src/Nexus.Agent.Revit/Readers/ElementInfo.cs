using System.Globalization;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit.Readers;

internal static class ElementInfo
{
    /// <summary>Identity and editability of an element, as a property group.</summary>
    public static PropertyGroup Group(Document doc, Element e, string name, string? blocker)
    {
        var g = new PropertyGroup(name);
        g.Properties.Add(Derived("Element Id", e.Id.Value.ToString(CultureInfo.InvariantCulture)));
        g.Properties.Add(Derived("Unique Id", e.UniqueId));
        g.Properties.Add(Derived("Category", e.Category?.Name ?? ""));
        if (doc.IsWorkshared) g.Properties.Add(Derived("Workset", Editability.WorksetName(doc, e)));
        g.Properties.Add(Derived("Editable", blocker is null ? "Yes" : "No: " + blocker));
        return g;
    }

    public static PropertyValue Derived(string name, string? value) => new()
    {
        Name = name,
        Value = value,
        RawValue = value,
        Source = PropertySource.Derived,
        StorageType = "String",
        IsReadOnly = true,
        ReadOnlyReason = "Computed by Nexus",
    };
}

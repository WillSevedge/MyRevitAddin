using Nexus.Contracts;

namespace Nexus.Hub.ViewModels;

/// <summary>Which Autodesk product a host is, and how readers are labelled in the hub.</summary>
internal static class Products
{
    /// <summary>"Revit", "AutoCAD", "Civil 3D" or "Plant 3D" (verticals run the AutoCAD agent with a module).</summary>
    public static string Of(HostInfo host)
    {
        if (host.HostKind == HostKinds.Revit) return "Revit";
        if (host.Product.Contains("Civil", StringComparison.OrdinalIgnoreCase) || host.Modules.Contains("Civil3D")) return "Civil 3D";
        if (host.Product.Contains("Plant", StringComparison.OrdinalIgnoreCase) || host.Modules.Contains("Plant3D")) return "Plant 3D";
        return "AutoCAD";
    }

    /// <summary>
    /// Title Case for names of views and groups ("Fabrication parts" → "Fabrication Parts"), so names read
    /// the same even from an older add-in. Short joining words stay lower case; capitals already there stay.
    /// </summary>
    public static string TitleCase(string text)
    {
        var small = new HashSet<string>(StringComparer.Ordinal) { "a", "an", "and", "as", "at", "by", "for", "in", "of", "on", "or", "the", "to", "with" };
        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i];
            int first = 0;
            while (first < w.Length && !char.IsLetter(w[first])) first++; // "(sheets)" → "(Sheets)"
            if (first >= w.Length || char.IsUpper(w[first])) continue;
            if (i > 0 && first == 0 && small.Contains(w)) continue;
            words[i] = w[..first] + char.ToUpperInvariant(w[first]) + w[(first + 1)..];
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// Category of a reader in the hub's view list. AutoCAD readers run in every AutoCAD-based product;
    /// a vertical module's readers (civil3d.*, plant.*) only in that product.
    /// </summary>
    public static string ReaderLabel(HostInfo host, string readerId)
    {
        if (host.HostKind == HostKinds.Revit) return "Revit";
        if (readerId.StartsWith("civil3d.", StringComparison.OrdinalIgnoreCase)) return "Civil 3D";
        if (readerId.StartsWith("plant.", StringComparison.OrdinalIgnoreCase)) return "Plant 3D";
        return "AutoCAD";
    }
}

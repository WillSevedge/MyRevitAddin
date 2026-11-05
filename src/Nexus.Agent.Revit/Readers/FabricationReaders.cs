using System.Globalization;
using Nexus.Agent;
using Nexus.Agent.Revit.Fabrication;
using Nexus.Contracts;
using Autodesk.Revit.DB;

namespace Nexus.Agent.Revit.Readers;

/// <summary>
/// Every MEP Fabrication part in the model with its fabrication data (item number, notes, spool,
/// status, service, specification, material, insulation, product data, custom data). The editable
/// fields are written back to the parts, the same fields Fabrication CADmep reads from a MAJ job.
/// </summary>
internal sealed class FabricationPartsReader : IHostDataReader<Document>
{
    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "revit.fabrication.parts",
        DisplayName = "Fabrication Parts",
        Domain = "Fabrication",
        Description = "MEP Fabrication parts (ductwork, pipework, hangers): item number, notes, spool, status, service, specification, " +
                      "material, insulation, product list data and custom data from the fabrication database. Item number, notes, spool, " +
                      "status, specification, material, insulation and custom data can be edited.",
        Options =
        {
            new ReaderOption { Name = "service", DisplayName = "Only services containing", Type = "string", Default = "",
                Description = "Part of a service name, e.g. Supply. Leave empty for every service." },
            ReaderOption.Bool("parameters", "Include Revit parameters", false),
            ReaderOption.Bool("hiddenParameters", "Include parameters not shown in Properties", false),
            ReaderOption.Int("maxParts", "Max parts", 20000),
        },
    };

    public void Read(Document doc, ReadContext ctx)
    {
        var config = FabricationDatabase.Configured(doc);
        if (config is null)
        {
            ctx.Warn(FabricationDatabase.NotConfigured);
            return;
        }

        string service = ctx.GetString("service")?.Trim() ?? "";
        bool parameters = ctx.GetBool("parameters");
        bool hidden = ctx.GetBool("hiddenParameters");
        int max = ctx.GetInt("maxParts", 20000);

        var parts = new FilteredElementCollector(doc).OfClass(typeof(FabricationPart)).Cast<FabricationPart>();
        if (service.Length > 0)
            parts = parts.Where(p => (p.ServiceName ?? "").Contains(service, StringComparison.OrdinalIgnoreCase));

        int count = 0;
        var cache = new ParameterCache();
        foreach (var part in parts.OrderBy(p => p.ServiceName).ThenBy(p => p.ItemNumber, StringComparer.OrdinalIgnoreCase))
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            if (++count > max)
            {
                ctx.Truncated = true;
                ctx.Warn($"Only the first {max} parts were read (option 'Max parts').");
                break;
            }

            string? blocker = Editability.Blocker(doc, part);
            string name = FirstNonEmpty(part.ProductName, part.Alias, part.Name);
            string size = Try(() => part.Size);
            var item = new DataItem
            {
                Id = part.UniqueId,
                ItemType = FabricationParts.PartType(part),
                Name = size.Length > 0 ? $"{name} {size}" : name,
                Key = Try(() => part.ItemNumber) is { Length: > 0 } number ? number : null,
            };
            item.Groups.AddRange(FabricationParts.Read(part, config, blocker));
            item.Groups.Add(ElementInfo.Group(doc, part, "Element", blocker));
            if (parameters) item.Groups.AddRange(ParameterReader.ReadGroups(doc, part, "", blocker, hidden, cache));
            ctx.Items.Add(item);
        }

        if (count == 0)
            ctx.Warn(service.Length > 0 ? $"No fabrication parts in services containing '{service}'." : "This model has no fabrication parts.");
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string Try(Func<string?> get)
    {
        try { return get() ?? ""; }
        catch { return ""; }
    }
}

/// <summary>
/// The fabrication configuration loaded in the model (the database authored in Fabrication CADmep/ESTmep):
/// services, materials, specifications, insulation, custom data, part statuses, ancillaries, connectors, dampers.
/// </summary>
internal sealed class FabricationDatabaseReader : IHostDataReader<Document>
{
    public ReaderDescriptor Descriptor { get; } = new()
    {
        Id = "revit.fabrication.database",
        DisplayName = "Fabrication Database",
        Domain = "Fabrication",
        Description = "The fabrication configuration loaded in the model (from Fabrication CADmep): services (loaded/used, palettes, buttons), " +
                      "materials, specifications, insulation specifications, custom data, part statuses, ancillaries, connectors and dampers.",
        Options = FabricationDatabase.Kinds.All
            .Select(k => ReaderOption.Bool(OptionName(k), Plural(k), k != FabricationDatabase.Kinds.Connector))
            .ToList(),
    };

    private static string Plural(string kind) => kind switch
    {
        FabricationDatabase.Kinds.CustomData => "Custom data",
        FabricationDatabase.Kinds.PartStatus => "Part statuses",
        FabricationDatabase.Kinds.Ancillary => "Ancillaries",
        _ => kind + "s",
    };

    private static string OptionName(string kind) => "kind." + kind.Replace(" ", "", StringComparison.Ordinal);

    public void Read(Document doc, ReadContext ctx)
    {
        var db = FabricationDatabase.Read(doc, kind => ctx.GetBool(OptionName(kind)));
        if (db is null)
        {
            ctx.Warn(FabricationDatabase.NotConfigured);
            return;
        }
        foreach (var w in db.Warnings) ctx.Warn(w);

        var config = new DataItem { Id = "fabrication-configuration", ItemType = "Configuration", Name = db.Name, Key = db.Name };
        var summary = new PropertyGroup("Database Entry");
        summary.Properties.Add(Value("Kind", "Configuration"));
        summary.Properties.Add(Value("Name", db.Name));
        var details = new PropertyGroup("Details");
        foreach (var (k, v) in db.Summary()) details.Properties.Add(Value(k, v));
        config.Groups.Add(summary);
        config.Groups.Add(details);
        ctx.Items.Add(config);

        foreach (var e in db.Entries)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var item = new DataItem
            {
                Id = $"fabrication-{e.Kind}-{e.Id.ToString(CultureInfo.InvariantCulture)}",
                ItemType = e.Kind,
                Name = e.Group.Length > 0 ? $"{e.Group}: {e.Name}" : e.Name,
                Key = e.Name,
            };
            var entry = new PropertyGroup("Database Entry");
            entry.Properties.Add(Value("Kind", e.Kind));
            entry.Properties.Add(Value("Id", e.Id.ToString(CultureInfo.InvariantCulture)));
            entry.Properties.Add(Value("Name", e.Name));
            entry.Properties.Add(Value("Group", e.Group));
            entry.Properties.Add(Value("Abbreviation", e.Abbreviation));
            item.Groups.Add(entry);
            if (e.Details.Count > 0)
            {
                var g = new PropertyGroup("Details");
                foreach (var (k, v) in e.Details) g.Properties.Add(Value(k, v));
                item.Groups.Add(g);
            }
            ctx.Items.Add(item);
        }
    }

    private static PropertyValue Value(string name, string? value) => new()
    {
        Name = name,
        Value = value ?? "",
        RawValue = value ?? "",
        Source = PropertySource.Managed,
        StorageType = "String",
        IsReadOnly = true,
        ReadOnlyReason = "Defined in the fabrication database. Change it in Fabrication CADmep, then reload the configuration in Revit (Nexus tab › Fabrication › Reload).",
    };
}

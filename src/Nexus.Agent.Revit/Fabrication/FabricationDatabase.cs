using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace Nexus.Agent.Revit.Fabrication;

/// <summary>One entry of the fabrication database (a service, material, specification, custom data field...).</summary>
internal sealed class FabricationEntry
{
    public required string Kind { get; init; }
    public required int Id { get; init; }
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public string Abbreviation { get; init; } = "";
    /// <summary>Kind-specific values, in display order.</summary>
    public List<KeyValuePair<string, string>> Details { get; } = new();

    public void Add(string name, string? value) => Details.Add(new(name, value ?? ""));
}

/// <summary>
/// What the model's fabrication configuration (the database authored in Fabrication CADmep/ESTmep)
/// contains: services, materials, specifications, insulation, custom data, part statuses, ancillaries,
/// connectors and dampers. Read-only; reading needs no transaction.
/// </summary>
internal sealed class FabricationDatabase
{
    public static class Kinds
    {
        public const string Service = "Service";
        public const string Material = "Material";
        public const string Specification = "Specification";
        public const string Insulation = "Insulation Specification";
        public const string CustomData = "Custom Data";
        public const string PartStatus = "Part Status";
        public const string Ancillary = "Ancillary";
        public const string Connector = "Connector";
        public const string Damper = "Damper";

        public static readonly string[] All =
            { Service, Material, Specification, Insulation, CustomData, PartStatus, Ancillary, Connector, Damper };
    }

    private FabricationDatabase(FabricationConfiguration config) => Configuration = config;

    public FabricationConfiguration Configuration { get; }
    public string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Path { get; private set; } = "";
    public string Profile { get; private set; } = "";
    public string UnitSystem { get; private set; } = "";
    public bool IsCloud { get; private set; }
    public bool IsLocked { get; private set; }
    public bool HasValidConfiguration { get; private set; }
    public int LoadedItemFiles { get; private set; }
    public int UsedItemFiles { get; private set; }
    public List<FabricationEntry> Entries { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>The loaded configuration, or null when the model has none (Revit: Manage › MEP Settings › Fabrication Settings).</summary>
    public static FabricationConfiguration? Configured(Document doc)
    {
        if (doc.IsFamilyDocument) return null;
        try { return FabricationConfiguration.GetFabricationConfiguration(doc); }
        catch { return null; }
    }

    public const string NotConfigured =
        "This model has no fabrication configuration. In Revit: Manage › MEP Settings › Fabrication Settings, pick the configuration (the Fabrication CADmep database) and load services.";

    public static FabricationDatabase? Read(Document doc, Func<string, bool>? includeKind = null)
    {
        var config = Configured(doc);
        if (config is null) return null;
        var db = new FabricationDatabase(config);
        db.ReadInfo();
        bool Want(string kind) => includeKind?.Invoke(kind) ?? true;

        if (Want(Kinds.Service)) db.Try(Kinds.Service, db.ReadServices);
        if (Want(Kinds.Material)) db.Try(Kinds.Material, db.ReadMaterials);
        if (Want(Kinds.Specification)) db.Try(Kinds.Specification, db.ReadSpecifications);
        if (Want(Kinds.Insulation)) db.Try(Kinds.Insulation, db.ReadInsulation);
        if (Want(Kinds.CustomData)) db.Try(Kinds.CustomData, db.ReadCustomData);
        if (Want(Kinds.PartStatus)) db.Try(Kinds.PartStatus, db.ReadStatuses);
        if (Want(Kinds.Ancillary)) db.Try(Kinds.Ancillary, db.ReadAncillaries);
        if (Want(Kinds.Connector)) db.Try(Kinds.Connector, db.ReadConnectors);
        if (Want(Kinds.Damper)) db.Try(Kinds.Damper, db.ReadDampers);
        return db;
    }

    public int Count(string kind) => Entries.Count(e => e.Kind == kind);

    /// <summary>Configuration summary as name/value pairs.</summary>
    public List<KeyValuePair<string, string>> Summary() => new()
    {
        new("Configuration", Name),
        new("Description", Description),
        new("Version", Version),
        new("Location", Path),
        new("Profile", Profile.Length == 0 ? "Global" : Profile),
        new("Units", UnitSystem),
        new("Cloud configuration", IsCloud ? "Yes" : "No"),
        new("Locked", IsLocked ? "Yes" : "No"),
        new("Valid", HasValidConfiguration ? "Yes" : "No"),
        new("Services (loaded / total)", $"{Entries.Count(e => e.Kind == Kinds.Service && e.Details.Any(d => d.Key == "Loaded" && d.Value == "Yes"))} / {Count(Kinds.Service)}"),
        new("Item files (used / loaded)", $"{UsedItemFiles} / {LoadedItemFiles}"),
    };

    private void ReadInfo()
    {
        var c = Configuration;
        Try("configuration", () =>
        {
            var info = c.GetFabricationConfigurationInfo();
            Name = info.Name ?? "";
            Description = info.Description ?? "";
            Version = info.Version.ToString(CultureInfo.InvariantCulture);
            Path = info.Path ?? "";
            UnitSystem = info.UnitSystem.ToString();
            IsCloud = info.IsCloudConfiguration;
            IsLocked = info.IsLocked;
        });
        Try("profile", () => Profile = c.GetProfile() ?? "");
        Try("validity", () => HasValidConfiguration = c.HasValidConfiguration());
        Try("item files", () =>
        {
            LoadedItemFiles = c.GetAllLoadedItemFiles().Count;
            UsedItemFiles = c.GetAllUsedItemFiles().Count;
        });
    }

    private void ReadServices()
    {
        var c = Configuration;
        var loaded = SafeIds(() => c.GetAllLoadedServices().Select(s => s.ServiceId));
        var used = SafeIds(() => c.GetAllUsedServices().Select(s => s.ServiceId));
        foreach (var s in c.GetAllServices().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var e = new FabricationEntry { Kind = Kinds.Service, Id = s.ServiceId, Name = s.Name ?? "", Abbreviation = s.Abbreviation ?? "" };
            e.Add("Loaded", loaded.Contains(s.ServiceId) ? "Yes" : "No");
            e.Add("Used in model", used.Contains(s.ServiceId) ? "Yes" : "No");
            e.Add("Fabrication system", Safe(() => s.FabricationSystemName));
            int palettes = SafeInt(() => s.PaletteCount);
            e.Add("Palettes", palettes.ToString(CultureInfo.InvariantCulture));
            if (loaded.Contains(s.ServiceId))
            {
                var names = new List<string>();
                int buttons = 0;
                for (int p = 0; p < palettes; p++)
                {
                    names.Add(Safe(() => s.GetPaletteName(p)));
                    buttons += SafeInt(() => s.GetButtonCount(p));
                }
                e.Add("Buttons", buttons.ToString(CultureInfo.InvariantCulture));
                e.Add("Palette names", string.Join(", ", names.Where(n => n.Length > 0)));
            }
            Entries.Add(e);
        }
    }

    private void ReadMaterials()
    {
        var c = Configuration;
        foreach (int id in c.GetAllMaterials(null))
        {
            var e = new FabricationEntry
            {
                Kind = Kinds.Material, Id = id,
                Name = Safe(() => c.GetMaterialName(id)),
                Group = Safe(() => c.GetMaterialGroup(id)),
                Abbreviation = Safe(() => c.GetMaterialAbbreviation(id)),
            };
            e.Add("GUID", Safe(() => c.GetMaterialGUID(id).ToString()));
            Entries.Add(e);
        }
    }

    private void ReadSpecifications()
    {
        var c = Configuration;
        foreach (int id in c.GetAllSpecifications(null))
            Entries.Add(new FabricationEntry
            {
                Kind = Kinds.Specification, Id = id,
                Name = Safe(() => c.GetSpecificationName(id)),
                Group = Safe(() => c.GetSpecificationGroup(id)),
                Abbreviation = Safe(() => c.GetSpecificationAbbreviation(id)),
            });
    }

    private void ReadInsulation()
    {
        var c = Configuration;
        foreach (int id in c.GetAllInsulationSpecifications(null))
            Entries.Add(new FabricationEntry
            {
                Kind = Kinds.Insulation, Id = id,
                Name = Safe(() => c.GetInsulationSpecificationName(id)),
                Group = Safe(() => c.GetInsulationSpecificationGroup(id)),
                Abbreviation = Safe(() => c.GetInsulationSpecificationAbbreviation(id)),
            });
    }

    private void ReadCustomData()
    {
        var c = Configuration;
        foreach (int id in c.GetAllPartCustomData())
        {
            var e = new FabricationEntry { Kind = Kinds.CustomData, Id = id, Name = Safe(() => c.GetPartCustomDataName(id)) };
            e.Add("Type", Safe(() => c.GetPartCustomDataType(id).ToString()));
            Entries.Add(e);
        }
    }

    private void ReadStatuses()
    {
        var c = Configuration;
        foreach (int id in c.GetAllPartStatuses())
            Entries.Add(new FabricationEntry { Kind = Kinds.PartStatus, Id = id, Name = Safe(() => c.GetPartStatusDescription(id)) });
    }

    private void ReadAncillaries()
    {
        var c = Configuration;
        var seen = new HashSet<int>();
        foreach (var type in Compat.EnumValues<FabricationAncillaryType>())
        {
            if (type == FabricationAncillaryType.Unknown) continue;
            IList<int> ids;
            try { ids = c.GetAncillaries(type, true, true); }
            catch { continue; }
            foreach (int id in ids)
            {
                if (!seen.Add(id)) continue;
                var e = new FabricationEntry
                {
                    Kind = Kinds.Ancillary, Id = id,
                    Name = Safe(() => c.GetAncillaryName(id)),
                    Group = Safe(() => c.GetAncillaryGroup(id)),
                };
                e.Add("Ancillary type", type.ToString());
                e.Add("Kit", SafeBool(() => c.IsAncillaryKit(id)) ? "Yes" : "No");
                Entries.Add(e);
            }
        }
    }

    private void ReadConnectors()
    {
        var c = Configuration;
        foreach (int id in c.GetAllFabricationConnectorDefinitions(ConnectorDomainType.Undefined, ConnectorProfileType.Invalid))
        {
            var e = new FabricationEntry
            {
                Kind = Kinds.Connector, Id = id,
                Name = Safe(() => c.GetFabricationConnectorName(id)),
                Group = Safe(() => c.GetFabricationConnectorGroup(id)),
            };
            e.Add("Domain", Safe(() => c.GetFabricationConnectorDomain(id).ToString()));
            e.Add("Shape", Safe(() => c.GetFabricationConnectorShape(id).ToString()));
            Entries.Add(e);
        }
    }

    private void ReadDampers()
    {
        var c = Configuration;
        foreach (int id in c.GetAllDampers())
            Entries.Add(new FabricationEntry { Kind = Kinds.Damper, Id = id, Name = Safe(() => c.GetDamperName(id)) });
    }

    // ------------------------------------------------------------------ helpers

    private void Try(string what, Action read)
    {
        try { read(); }
        catch (Exception ex) { Warnings.Add($"Could not read the fabrication {what.ToLowerInvariant()}: {ex.Message}"); }
    }

    internal static string Safe(Func<string?> get)
    {
        try { return get() ?? ""; }
        catch { return ""; }
    }

    private static int SafeInt(Func<int> get)
    {
        try { return get(); }
        catch { return 0; }
    }

    private static bool SafeBool(Func<bool> get)
    {
        try { return get(); }
        catch { return false; }
    }

    private static HashSet<int> SafeIds(Func<IEnumerable<int>> get)
    {
        try { return get().ToHashSet(); }
        catch { return new HashSet<int>(); }
    }
}

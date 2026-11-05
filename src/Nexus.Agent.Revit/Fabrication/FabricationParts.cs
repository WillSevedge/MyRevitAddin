using System.Globalization;
using Nexus.Contracts;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;

namespace Nexus.Agent.Revit.Fabrication;

/// <summary>
/// The fabrication data of a part (MEP Fabrication duct, pipe, hanger...) as Fabrication CADmep sees it:
/// item number, notes, spool, status, service, specification, material, insulation, product list data
/// and the configuration's custom data. Property ids start with <see cref="IdPrefix"/> (or
/// <see cref="CustomDataPrefix"/> + custom data id) so <see cref="RevitWriter"/> can set them back.
/// </summary>
internal static class FabricationParts
{
    public const string IdPrefix = "Fab:";
    public const string CustomDataPrefix = "FabCD:";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Editable fields: id → (label, group).
    private const string ItemNumber = "ItemNumber";
    private const string Notes = "Notes";
    private const string SpoolName = "SpoolName";
    private const string Status = "PartStatus";
    private const string Specification = "Specification";
    private const string Material = "Material";
    private const string Insulation = "InsulationSpecification";

    public static bool Handles(string? propertyId) =>
        propertyId is not null && (propertyId.StartsWith(IdPrefix, StringComparison.Ordinal) || propertyId.StartsWith(CustomDataPrefix, StringComparison.Ordinal));

    /// <summary>Property groups for one part. <paramref name="blocker"/>: why the element cannot be edited, if so.</summary>
    public static List<PropertyGroup> Read(FabricationPart part, FabricationConfiguration config, string? blocker)
    {
        var fab = new PropertyGroup("Fabrication");
        fab.Properties.Add(Editable(ItemNumber, "Item Number", Safe(() => part.ItemNumber), blocker));
        fab.Properties.Add(Editable(Notes, "Notes", Safe(() => part.Notes), blocker));
        fab.Properties.Add(Editable(SpoolName, "Spool Name", Safe(() => part.SpoolName), blocker));
        fab.Properties.Add(Editable(Status, "Part Status", Current(part, config, IdPrefix + Status), blocker));
        fab.Properties.Add(Info("Alias", Safe(() => part.Alias)));
        fab.Properties.Add(Info("Item Custom Id", Safe(() => part.ItemCustomId.ToString(Inv))));
        fab.Properties.Add(Info("Part Type", PartType(part)));
        fab.Properties.Add(Info("Bought Out", SafeBool(() => part.IsBoughtOut)));
        fab.Properties.Add(Info("Validation Status", Safe(() => part.ValidationStatus.ToString())));

        var service = new PropertyGroup("Fabrication Service");
        service.Properties.Add(Info("Service", Safe(() => part.ServiceName)));
        service.Properties.Add(Info("Service Abbreviation", Safe(() => part.ServiceAbbreviation)));
        service.Properties.Add(Info("Service Type", Safe(() => config.GetServiceTypeName(part.ServiceType))));
        service.Properties.Add(Editable(Specification, "Specification", Current(part, config, IdPrefix + Specification), blocker,
            "Specification name from the fabrication database (\"Group: Name\" or the name)."));
        service.Properties.Add(Editable(Material, "Material", Current(part, config, IdPrefix + Material), blocker,
            "Material name from the fabrication database (\"Group: Name\" or the name)."));
        service.Properties.Add(Info("Gauge", Safe(() => part.MaterialGauge.ToString(CultureInfo.CurrentCulture))));
        service.Properties.Add(Editable(Insulation, "Insulation Specification", Current(part, config, IdPrefix + Insulation), blocker,
            "Insulation specification name, or Off."));
        service.Properties.Add(Info("Has Insulation", SafeBool(() => part.HasInsulation)));
        service.Properties.Add(Info("Insulation Type", Safe(() => part.InsulationType)));
        service.Properties.Add(Info("Has Lining", SafeBool(() => part.HasLining)));
        service.Properties.Add(Info("Lining Type", Safe(() => part.LiningType)));
        service.Properties.Add(Info("Has Double Wall", SafeBool(() => part.HasDoubleWall)));

        var product = new PropertyGroup("Fabrication Product");
        product.Properties.Add(Info("Product List Entry", Safe(() => part.IsProductList() && part.ProductListEntry >= 0
            ? part.GetProductListEntryName(part.ProductListEntry) : "")));
        product.Properties.Add(Info("Product Code", Safe(() => part.ProductCode)));
        product.Properties.Add(Info("Product Name", Safe(() => part.ProductName)));
        product.Properties.Add(Info("Long Description", Safe(() => part.ProductLongDescription)));
        product.Properties.Add(Info("Short Description", Safe(() => part.ProductShortDescription)));
        product.Properties.Add(Info("Size Description", Safe(() => part.ProductSizeDescription)));
        product.Properties.Add(Info("Material Description", Safe(() => part.ProductMaterialDescription)));
        product.Properties.Add(Info("Specification Description", Safe(() => part.ProductSpecificationDescription)));
        product.Properties.Add(Info("Finish", Safe(() => part.ProductFinishDescription)));
        product.Properties.Add(Info("Install Type", Safe(() => part.ProductInstallType)));
        product.Properties.Add(Info("Manufacturer", Safe(() => part.ProductOriginalEquipmentManufacture)));
        product.Properties.Add(Info("Data Range", Safe(() => part.ProductDataRange)));
        product.Properties.Add(Info("Vendor", Safe(() => part.Vendor)));
        product.Properties.Add(Info("Vendor Code", Safe(() => part.VendorCode)));

        var size = new PropertyGroup("Fabrication Size");
        size.Properties.Add(Info("Size", Safe(() => part.Size)));
        size.Properties.Add(Info("Overall Size", Safe(() => part.OverallSize)));
        size.Properties.Add(Info("Free Size", Safe(() => part.FreeSize)));
        size.Properties.Add(Info("Centerline Length", Length(part.Document, () => part.CenterlineLength)));
        size.Properties.Add(Info("Weight", Safe(() => part.Weight.ToString("0.###", CultureInfo.CurrentCulture))));
        size.Properties.Add(Info("Sheet Metal Area", Safe(() => part.SheetMetalArea.ToString("0.###", CultureInfo.CurrentCulture))));

        var groups = new List<PropertyGroup> { fab, service, product, size };

        var custom = new PropertyGroup("Fabrication Custom Data");
        foreach (int id in SafeList(() => config.GetAllPartCustomData()))
        {
            string name = Safe(() => config.GetPartCustomDataName(id));
            if (name.Length == 0) name = "Custom Data " + id.ToString(Inv);
            bool has = SafeBoolValue(() => part.HasCustomData(id));
            string value = has ? Safe(() => part.GetPartCustomDataText(id)) : "";
            custom.Properties.Add(new PropertyValue
            {
                Name = name,
                Id = CustomDataPrefix + id.ToString(Inv),
                Source = PropertySource.Managed,
                StorageType = "String",
                DataType = Safe(() => config.GetPartCustomDataType(id).ToString()),
                Value = value,
                RawValue = value,
                IsReadOnly = blocker is not null,
                ReadOnlyReason = blocker,
            });
        }
        if (custom.Properties.Count > 0) groups.Add(custom);
        return groups;
    }

    /// <summary>Current value of an editable field, as shown in the hub.</summary>
    public static string Current(FabricationPart part, FabricationConfiguration config, string propertyId)
    {
        if (propertyId.StartsWith(CustomDataPrefix, StringComparison.Ordinal))
        {
            int cd = int.Parse(propertyId[CustomDataPrefix.Length..], Inv);
            return SafeBoolValue(() => part.HasCustomData(cd)) ? Safe(() => part.GetPartCustomDataText(cd)) : "";
        }
        return propertyId[IdPrefix.Length..] switch
        {
            ItemNumber => Safe(() => part.ItemNumber),
            Notes => Safe(() => part.Notes),
            SpoolName => Safe(() => part.SpoolName),
            Status => Safe(() => config.GetPartStatusDescription(part.PartStatus)),
            Specification => Safe(() => part.Specification <= 0 ? "" : Named(config.GetSpecificationGroup(part.Specification), config.GetSpecificationName(part.Specification))),
            Material => Safe(() => part.Material <= 0 ? "" : Named(config.GetMaterialGroup(part.Material), config.GetMaterialName(part.Material))),
            Insulation => Safe(() => part.InsulationSpecification <= 0 ? "Off" : Named(config.GetInsulationSpecificationGroup(part.InsulationSpecification), config.GetInsulationSpecificationName(part.InsulationSpecification))),
            _ => "",
        };
    }

    /// <summary>Sets a field (inside an open transaction). Returns null on success, else why not.</summary>
    public static string? Set(FabricationPart part, FabricationConfiguration config, string propertyId, string value)
    {
        value ??= "";
        if (propertyId.StartsWith(CustomDataPrefix, StringComparison.Ordinal))
        {
            int cd = int.Parse(propertyId[CustomDataPrefix.Length..], Inv);
            if (!part.HasCustomData(cd))
            {
                if (value.Length == 0) return null;
                part.AddPartCustomData(cd);
            }
            // Numbers are parsed by the fabrication configuration's own rules.
            part.SetPartCustomDataText(cd, value);
            return null;
        }

        switch (propertyId[IdPrefix.Length..])
        {
            case ItemNumber:
                part.ItemNumber = value;
                return null;
            case Notes:
                part.Notes = value;
                return null;
            case SpoolName:
                part.SpoolName = value;
                return null;
            case Status:
            {
                var match = Find(config.GetAllPartStatuses(), id => ("", config.GetPartStatusDescription(id)), value);
                if (match is null) return $"'{value}' is not a part status in the fabrication database.";
                part.PartStatus = match.Value;
                return null;
            }
            case Specification:
            {
                var match = Find(config.GetAllSpecifications(part), id => (config.GetSpecificationGroup(id), config.GetSpecificationName(id)), value);
                if (match is null) return $"'{value}' is not a specification that can be used for this part.";
                part.Specification = match.Value;
                return null;
            }
            case Material:
            {
                var match = Find(config.GetAllMaterials(part), id => (config.GetMaterialGroup(id), config.GetMaterialName(id)), value);
                if (match is null) return $"'{value}' is not a material that can be used for this part.";
                part.Material = match.Value;
                return null;
            }
            case Insulation:
            {
                if (value.Trim().Length == 0 || value.Trim().Equals("Off", StringComparison.OrdinalIgnoreCase))
                {
                    part.InsulationSpecification = 0;
                    return null;
                }
                var match = Find(config.GetAllInsulationSpecifications(part), id => (config.GetInsulationSpecificationGroup(id), config.GetInsulationSpecificationName(id)), value);
                if (match is null) return $"'{value}' is not an insulation specification that can be used for this part.";
                part.InsulationSpecification = match.Value;
                return null;
            }
            default:
                return "This fabrication value cannot be edited.";
        }
    }

    public static string PartType(FabricationPart part)
    {
        if (SafeBoolValue(part.IsAHanger)) return "Hanger";
        if (SafeBoolValue(part.IsAStraight)) return "Straight";
        if (SafeBoolValue(part.IsATap)) return "Tap";
        return "Fitting";
    }

    /// <summary>"Group: Name", or the name when there is no group.</summary>
    private static string Named(string? group, string? name) =>
        string.IsNullOrWhiteSpace(group) ? name ?? "" : $"{group}: {name}";

    /// <summary>Looks up a database id by "Group: Name" or by name alone (case-insensitive).</summary>
    private static int? Find(IEnumerable<int> ids, Func<int, (string? Group, string? Name)> describe, string value)
    {
        string wanted = value.Trim();
        var list = ids.Select(id =>
        {
            try { var (g, n) = describe(id); return (Id: id, Full: Named(g, n), Name: n ?? ""); }
            catch { return (Id: id, Full: "", Name: ""); }
        }).ToList();
        var exact = list.FirstOrDefault(x => x.Full.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (exact.Full.Length > 0) return exact.Id;
        var byName = list.Where(x => x.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0].Id : null;
    }

    private static PropertyValue Editable(string id, string name, string value, string? blocker, string? note = null) => new()
    {
        Name = name,
        Id = IdPrefix + id,
        Source = PropertySource.Managed,
        StorageType = "String",
        DataType = note,
        Value = value,
        RawValue = value,
        IsReadOnly = blocker is not null,
        ReadOnlyReason = blocker,
    };

    private static PropertyValue Info(string name, string value) => new()
    {
        Name = name,
        Source = PropertySource.Managed,
        StorageType = "String",
        Value = value,
        RawValue = value,
        IsReadOnly = true,
        ReadOnlyReason = "Set by the fabrication database (change it in Fabrication CADmep or with the Revit fabrication tools).",
    };

    private static string Length(Document doc, Func<double> get)
    {
        try { return UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, get(), false); }
        catch { return ""; }
    }

    private static string Safe(Func<string?> get)
    {
        try { return get() ?? ""; }
        catch { return ""; }
    }

    private static string SafeBool(Func<bool> get)
    {
        try { return get() ? "Yes" : "No"; }
        catch { return ""; }
    }

    private static bool SafeBoolValue(Func<bool> get)
    {
        try { return get(); }
        catch { return false; }
    }

    private static IList<int> SafeList(Func<IList<int>> get)
    {
        try { return get(); }
        catch { return Array.Empty<int>(); }
    }
}

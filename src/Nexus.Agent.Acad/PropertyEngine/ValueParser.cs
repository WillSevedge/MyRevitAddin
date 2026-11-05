using System.Globalization;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.Geometry;

namespace Nexus.Agent.Acad.PropertyEngine;

/// <summary>Turns text typed in the hub into a value of the property's API type. The reverse of <see cref="ValueFormatter"/>.</summary>
public static class ValueParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Returns the parsed value, or throws <see cref="FormatException"/> with a message for the user.</summary>
    public static object? Parse(string text, Type type)
    {
        text ??= "";
        var target = Nullable.GetUnderlyingType(type) ?? type;
        string t = text.Trim();

        if (target == typeof(string) || target == typeof(object)) return text;
        if (target == typeof(bool))
            return ParseBool(t) ?? throw new FormatException($"'{text}' is not Yes or No.");
        if (target.IsEnum)
        {
            if (TryParseEnum(target, t.Replace(" ", ""), out var e)) return e;
            throw new FormatException($"'{text}' is not one of: {string.Join(", ", Enum.GetNames(target).Take(12))}{(Enum.GetNames(target).Length > 12 ? ", ..." : "")}.");
        }
        if (target == typeof(double)) return Number(t, text);
        if (target == typeof(float)) return (float)Number(t, text);
        if (target == typeof(int) || target == typeof(short) || target == typeof(long) || target == typeof(byte)
            || target == typeof(uint) || target == typeof(ushort) || target == typeof(ulong) || target == typeof(sbyte))
        {
            double d = Number(t, text);
            if (Math.Abs(d - Math.Round(d)) > 1e-9) throw new FormatException($"'{text}' is not a whole number.");
            try { return Convert.ChangeType(Math.Round(d), target, Inv); }
            catch (OverflowException) { throw new FormatException($"'{text}' is out of range."); }
        }
        if (target == typeof(Point3d)) { var v = Numbers(t, text, 2, 3); return new Point3d(v[0], v[1], v.Length > 2 ? v[2] : 0); }
        if (target == typeof(Point2d)) { var v = Numbers(t, text, 2, 2); return new Point2d(v[0], v[1]); }
        if (target == typeof(Vector3d)) { var v = Numbers(t, text, 2, 3); return new Vector3d(v[0], v[1], v.Length > 2 ? v[2] : 0); }
        if (target == typeof(Vector2d)) { var v = Numbers(t, text, 2, 2); return new Vector2d(v[0], v[1]); }
        if (target == typeof(Scale3d)) { var v = Numbers(t, text, 1, 3); return v.Length == 1 ? new Scale3d(v[0]) : new Scale3d(v[0], v[1], v.Length > 2 ? v[2] : 1); }
        if (target == typeof(double[])) return Numbers(t, text, 1, 16);
        if (target == typeof(Color)) return ParseColor(t, text);

        throw new FormatException($"Values of type {target.Name} cannot be edited from Nexus yet.");
    }

    public static bool? ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "yes" or "y" or "true" or "1" or "on" => true,
        "no" or "n" or "false" or "0" or "off" => false,
        _ => null,
    };

    private static double Number(string t, string original)
    {
        if (double.TryParse(t, NumberStyles.Float, Inv, out var d) || double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
            return d;
        throw new FormatException($"'{original}' is not a number.");
    }

    private static double[] Numbers(string t, string original, int min, int max)
    {
        var parts = t.Trim('(', ')', '[', ']').Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < min || parts.Length > max)
            throw new FormatException($"'{original}' needs {(min == max ? min.ToString(Inv) : $"{min} to {max}")} numbers separated by commas, e.g. 10, 20{(max > 2 ? ", 0" : "")}.");
        return parts.Select(p => Number(p, original)).ToArray();
    }

    private static Color ParseColor(string t, string original)
    {
        switch (t.Replace(" ", "").ToLowerInvariant())
        {
            case "bylayer": return Color.FromColorIndex(ColorMethod.ByLayer, 256);
            case "byblock": return Color.FromColorIndex(ColorMethod.ByBlock, 0);
            case "red": return Color.FromColorIndex(ColorMethod.ByAci, 1);
            case "yellow": return Color.FromColorIndex(ColorMethod.ByAci, 2);
            case "green": return Color.FromColorIndex(ColorMethod.ByAci, 3);
            case "cyan": return Color.FromColorIndex(ColorMethod.ByAci, 4);
            case "blue": return Color.FromColorIndex(ColorMethod.ByAci, 5);
            case "magenta": return Color.FromColorIndex(ColorMethod.ByAci, 6);
            case "white": return Color.FromColorIndex(ColorMethod.ByAci, 7);
        }
        if (short.TryParse(t, NumberStyles.Integer, Inv, out var aci) && aci is >= 1 and <= 255)
            return Color.FromColorIndex(ColorMethod.ByAci, aci);
        var rgb = t.Split(',').Select(p => p.Trim()).ToArray();
        if (rgb.Length == 3 && rgb.All(p => byte.TryParse(p, NumberStyles.Integer, Inv, out _)))
            return Color.FromRgb(byte.Parse(rgb[0], Inv), byte.Parse(rgb[1], Inv), byte.Parse(rgb[2], Inv));
        throw new FormatException($"'{original}' is not a color. Use ByLayer, ByBlock, a color number 1-255, a name like Red, or R,G,B.");
    }

    /// <summary>Enum by name, ignoring case (works on .NET Framework too, which lacks Enum.TryParse(Type...)).</summary>
    private static bool TryParseEnum(Type type, string text, out object value)
    {
        value = null!;
        if (text.Length == 0 || char.IsDigit(text[0]) || text[0] == '-' || !Enum.GetNames(type).Any(n => n.Equals(text, StringComparison.OrdinalIgnoreCase)))
            return false;
        value = Enum.Parse(type, text, ignoreCase: true);
        return true;
    }
}


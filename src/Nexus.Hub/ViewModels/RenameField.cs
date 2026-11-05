namespace Nexus.Hub.ViewModels;

/// <summary>A column that Rename &amp; Renumber can change (Sheet Number is unique in each file).</summary>
public sealed record RenameField(string ColumnId, string Label, bool Unique)
{
    public override string ToString() => Label;
}

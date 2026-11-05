namespace Nexus.Rename;

/// <summary>
/// Everything the Bulk Rename window can do to a name, as panels that are applied in this order (like
/// Bulk Rename Utility): RegEx, Name, Replace, Case, Remove, Add, Numbering. The Filter decides which items
/// take part. Each panel is off until enabled. Saved as a preset (JSON).
/// </summary>
public sealed class RenameRules
{
    public RegexRule Regex { get; set; } = new();
    public NameRule Name { get; set; } = new();
    public ReplaceRule Replace { get; set; } = new();
    public CaseRule Case { get; set; } = new();
    public RemoveRule Remove { get; set; } = new();
    public AddRule Add { get; set; } = new();
    public NumberingRule Numbering { get; set; } = new();
    public FilterRule Filter { get; set; } = new();

    /// <summary>True when no panel changes anything.</summary>
    public bool IsEmpty => !Regex.IsActive && !Name.IsActive && !Replace.IsActive && !Case.IsActive
                           && !Remove.IsActive && !Add.IsActive && !Numbering.IsActive;
}

/// <summary>(1) Regular expression: Match is replaced with Replace ($1, $2... for groups).</summary>
public sealed class RegexRule
{
    public bool Enabled { get; set; }
    public string Match { get; set; } = "";
    public string Replace { get; set; } = "";
    public bool MatchCase { get; set; }
    public bool IsActive => Enabled && Match.Length > 0;
}

public enum NameMode
{
    /// <summary>Keep the name (the other panels change it).</summary>
    Keep,
    /// <summary>Start from nothing (Add and Numbering build the name).</summary>
    Remove,
    /// <summary>A fixed text, with tokens such as {Level} or {Sheet Number}.</summary>
    Fixed,
    /// <summary>The name backwards.</summary>
    Reverse,
}

/// <summary>(2) What to start from.</summary>
public sealed class NameRule
{
    public bool Enabled { get; set; }
    public NameMode Mode { get; set; } = NameMode.Keep;
    public string Fixed { get; set; } = "";
    public bool IsActive => Enabled && Mode != NameMode.Keep;
}

/// <summary>(3) Find and replace text. Several pairs can be given separated by | (Find "A|B", With "1|2").</summary>
public sealed class ReplaceRule
{
    public bool Enabled { get; set; }
    public string Find { get; set; } = "";
    public string With { get; set; } = "";
    public bool MatchCase { get; set; }
    /// <summary>Only the first occurrence.</summary>
    public bool FirstOnly { get; set; }
    /// <summary>Find must be a whole word.</summary>
    public bool WholeWord { get; set; }
    public bool IsActive => Enabled && Find.Length > 0;
}

public enum CaseMode
{
    Same,
    /// <summary>UPPER CASE</summary>
    Upper,
    /// <summary>lower case</summary>
    Lower,
    /// <summary>Title Case (Every Word)</summary>
    Title,
    /// <summary>Sentence case (first letter only)</summary>
    Sentence,
}

/// <summary>(4) Capitals. Exceptions are words kept exactly as typed (HVAC, MEP, 3D...).</summary>
public sealed class CaseRule
{
    public bool Enabled { get; set; }
    public CaseMode Mode { get; set; } = CaseMode.Same;
    /// <summary>Words kept as typed, separated by commas or spaces.</summary>
    public string Exceptions { get; set; } = "";
    public bool IsActive => Enabled && Mode != CaseMode.Same;
}

public enum CropMode { None, Before, After }

/// <summary>(5) Remove characters and words.</summary>
public sealed class RemoveRule
{
    public bool Enabled { get; set; }
    /// <summary>Remove this many characters from the start.</summary>
    public int First { get; set; }
    /// <summary>Remove this many characters from the end.</summary>
    public int Last { get; set; }
    /// <summary>Remove characters From..To (1-based, inclusive); 0 = off.</summary>
    public int From { get; set; }
    public int To { get; set; }
    /// <summary>Every one of these characters is removed.</summary>
    public string Chars { get; set; } = "";
    /// <summary>These words are removed (separated by spaces or commas).</summary>
    public string Words { get; set; } = "";
    /// <summary>Remove everything before (or after) the first occurrence of <see cref="CropText"/>.</summary>
    public CropMode Crop { get; set; } = CropMode.None;
    public string CropText { get; set; } = "";
    public bool Digits { get; set; }
    /// <summary>Anything that is not a letter, digit or space.</summary>
    public bool Symbols { get; set; }
    /// <summary>é → e, ü → u...</summary>
    public bool Accents { get; set; }
    /// <summary>Spaces at the start and end.</summary>
    public bool Trim { get; set; } = true;
    /// <summary>Two or more spaces become one.</summary>
    public bool DoubleSpaces { get; set; }

    public bool IsActive => Enabled && (First > 0 || Last > 0 || From > 0 || Chars.Length > 0 || Words.Trim().Length > 0
                                        || (Crop != CropMode.None && CropText.Length > 0) || Digits || Symbols || Accents || Trim || DoubleSpaces);
}

/// <summary>(6) Add text. Prefix, Insert and Suffix may use tokens such as {Level}, {Sheet Number}, {Type}.</summary>
public sealed class AddRule
{
    public bool Enabled { get; set; }
    public string Prefix { get; set; } = "";
    public string Insert { get; set; } = "";
    /// <summary>Where Insert goes: 1 = before the first character, 2 = after it...; negative counts from the end (-1 = at the end).</summary>
    public int AtPosition { get; set; } = 1;
    public string Suffix { get; set; } = "";
    /// <summary>A space before each capital that follows a lower-case letter: "FloorPlan" → "Floor Plan".</summary>
    public bool WordSpace { get; set; }
    public bool IsActive => Enabled && (Prefix.Length > 0 || Insert.Length > 0 || Suffix.Length > 0 || WordSpace);
}

public enum NumberingMode { None, Prefix, Suffix, Insert, Replace }

public enum NumberingStyle
{
    /// <summary>1, 2, 3</summary>
    Decimal,
    /// <summary>A, B, ... Z, AA, AB</summary>
    UpperLetters,
    /// <summary>a, b, ... z, aa, ab</summary>
    LowerLetters,
}

/// <summary>(7) Numbering, in the order the items are listed.</summary>
public sealed class NumberingRule
{
    public bool Enabled { get; set; }
    public NumberingMode Mode { get; set; } = NumberingMode.Suffix;
    public int Start { get; set; } = 1;
    public int Increment { get; set; } = 1;
    /// <summary>Minimum digits (1 → 01 with 2).</summary>
    public int Pad { get; set; } = 2;
    /// <summary>Text between the name and the number.</summary>
    public string Separator { get; set; } = " ";
    /// <summary>Insert mode: position as in <see cref="AddRule.AtPosition"/>.</summary>
    public int AtPosition { get; set; } = 1;
    public NumberingStyle Style { get; set; } = NumberingStyle.Decimal;
    /// <summary>Start again for each different value of this token (e.g. {Level}); empty = one sequence.</summary>
    public string RestartFor { get; set; } = "";
    public bool IsActive => Enabled && Mode != NumberingMode.None;
}

/// <summary>(8) Which items take part: those whose current name matches.</summary>
public sealed class FilterRule
{
    public bool Enabled { get; set; }
    /// <summary>Wildcards (* and ?), or a regular expression with <see cref="UseRegex"/>; several masks separated by ;.</summary>
    public string Mask { get; set; } = "*";
    public bool UseRegex { get; set; }
    public bool MatchCase { get; set; }
    /// <summary>Take the items that do NOT match.</summary>
    public bool Invert { get; set; }
}

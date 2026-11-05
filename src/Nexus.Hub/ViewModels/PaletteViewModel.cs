using System.Collections.ObjectModel;
using Nexus.Hub.Core.Search;

namespace Nexus.Hub.ViewModels;

/// <summary>Something the command palette can do: an action, a view to open, or a sheet to go to.</summary>
public sealed class PaletteItem
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; init; } = "";
    /// <summary>"Action", "View" or "Sheet" (ranking and the label on the right).</summary>
    public required string Category { get; init; }
    public required Action Run { get; init; }
    /// <summary>Shift+Enter (e.g. show the sheet in its program).</summary>
    public Action? RunAlternate { get; init; }
    /// <summary>Extra words to match (synonyms).</summary>
    public string Keywords { get; init; } = "";

    public string Hint => Category switch
    {
        "Sheet" => RunAlternate is null ? "Jump to" : "Jump to  ·  Shift+Enter: show in model",
        "View" => "Open view",
        _ => "",
    };
}

/// <summary>Ctrl+K: type to find any action, view or sheet; Enter runs it.</summary>
public sealed class PaletteViewModel : Observable
{
    private const int MaxResults = 60;
    private readonly IReadOnlyList<PaletteItem> _items;
    private string _query = "";
    private PaletteItem? _selected;

    public PaletteViewModel(IReadOnlyList<PaletteItem> items)
    {
        _items = items;
        Filter();
    }

    public ObservableCollection<PaletteItem> Results { get; } = new();

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? "")) Filter();
        }
    }

    public PaletteItem? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string Footer => Results.Count == 0 ? "Nothing matches." : "↑↓ to choose  ·  Enter to run  ·  Esc to close";

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        int i = _selected is null ? -1 : Results.IndexOf(_selected);
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }

    private static int CategoryRank(string category) => category switch { "Action" => 0, "View" => 1, _ => 2 };

    private void Filter()
    {
        Results.Clear();
        IEnumerable<PaletteItem> found;
        if (_query.Trim().Length == 0)
        {
            // Nothing typed: the actions and views (sheets appear when you type).
            found = _items.Where(i => i.Category != "Sheet");
        }
        else
        {
            found = _items
                .Select(i => (Item: i, Score: FuzzyMatch.Score(_query, $"{i.Title} {i.Keywords}") ?? (FuzzyMatch.Score(_query, i.Subtitle) is { } s ? s / 3 : null)))
                .Where(x => x.Score is not null)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => CategoryRank(x.Item.Category))
                .Select(x => x.Item);
        }
        foreach (var item in found.Take(MaxResults)) Results.Add(item);
        Selected = Results.FirstOrDefault();
        Raise(nameof(Footer));
    }
}

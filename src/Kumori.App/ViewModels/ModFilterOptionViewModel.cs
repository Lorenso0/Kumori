using CommunityToolkit.Mvvm.ComponentModel;
using Kumori.Core.Models;

namespace Kumori.App.ViewModels;

public partial class ModFilterOptionViewModel : ObservableObject
{
    public ModFilterOptionViewModel(string acronym) => Acronym = acronym;

    public string Acronym { get; }

    [ObservableProperty]
    private bool _isSelected;
}

internal static class ModFilterMatcher
{
    public static bool Matches(
        AttemptSummary attempt,
        IEnumerable<string> selectedMods,
        bool exact)
    {
        var selected = selectedMods
            .Where(acronym => !string.IsNullOrWhiteSpace(acronym))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            return true;
        }

        var active = (attempt.Mods.Count > 0
                ? attempt.Mods.Select(mod => mod.Acronym)
                : ModDisplayText.AcronymsFromKey(attempt.ModsKey))
            .Where(acronym => !string.Equals(acronym, "NM", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return selected.IsSubsetOf(active) && (!exact || selected.SetEquals(active));
    }
}

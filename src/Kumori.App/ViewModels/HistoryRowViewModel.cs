using CommunityToolkit.Mvvm.ComponentModel;

namespace Kumori.App.ViewModels;

/// <summary>
/// Base type for rows in the interleaved history list. The list mixes session
/// separators (<see cref="SessionRowViewModel"/>) with attempt cards
/// (<see cref="AttemptRowViewModel"/>); WPF picks the template by runtime type.
/// </summary>
public abstract class HistoryRowViewModel : ObservableObject
{
    public bool IsSessionHeader => this is SessionRowViewModel;
}

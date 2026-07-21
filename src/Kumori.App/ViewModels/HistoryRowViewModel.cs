using CommunityToolkit.Mvvm.ComponentModel;

namespace Kumori.App.ViewModels;

/// <summary>
/// Base type for rows in the interleaved history list. The list mixes day and
/// session separators with attempt cards; WPF picks the template by runtime type.
/// </summary>
public abstract class HistoryRowViewModel : ObservableObject
{
    public bool IsSessionHeader => this is SessionRowViewModel;
    public bool IsDayHeader => this is DayRowViewModel;
}

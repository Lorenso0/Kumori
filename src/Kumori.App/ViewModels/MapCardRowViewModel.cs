namespace Kumori.App.ViewModels;

public sealed class MapCardRowViewModel
{
    public MapCardRowViewModel(MapCardViewModel first, MapCardViewModel? second)
    {
        Cards = second is null ? [first] : [first, second];
    }

    public IReadOnlyList<MapCardViewModel> Cards { get; }
}

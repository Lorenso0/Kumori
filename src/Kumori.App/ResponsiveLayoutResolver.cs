namespace Kumori.App;

public enum ResponsiveLayoutMode
{
    Compact,
    Standard,
    Wide,
}

public readonly record struct ResponsiveLayoutState(ResponsiveLayoutMode Mode, bool IsShort)
{
    public bool IsCompact => Mode == ResponsiveLayoutMode.Compact;
    public bool IsStandard => Mode == ResponsiveLayoutMode.Standard;
    public bool IsWide => Mode == ResponsiveLayoutMode.Wide;
}

public static class ResponsiveLayoutResolver
{
    public const double CompactMaximumWidth = 1023;
    public const double StandardMaximumWidth = 1279;
    public const double ShortMaximumHeight = 599;

    public static ResponsiveLayoutState Resolve(double width, double height)
    {
        var mode = width switch
        {
            <= CompactMaximumWidth => ResponsiveLayoutMode.Compact,
            <= StandardMaximumWidth => ResponsiveLayoutMode.Standard,
            _ => ResponsiveLayoutMode.Wide,
        };
        return new ResponsiveLayoutState(mode, height <= ShortMaximumHeight);
    }
}

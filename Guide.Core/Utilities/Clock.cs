namespace Guide.Core.Utilities;

// The time that the guide shows. Set Offset to show another time, for example to compare
// a screenshot with a screenshot of ESQ.
public static class Clock
{
    public static TimeSpan Offset { get; set; } = TimeSpan.Zero;

    public static DateTime Now => DateTime.Now + Offset;
}

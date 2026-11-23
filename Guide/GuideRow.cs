using Guide.Core.SDL.Wrappers;

namespace Guide;

// A row of the guide: its texture, and the time to stop the scroll when its top reaches the top of the guide.
// PauseDone is true after the row stopped the scroll, or when it must not stop the scroll.
public sealed class GuideRow(Texture texture, TimeSpan pause, DateTime? headerStartTime = null) : IDisposable
{
    // For a time header row: the time of its first column. The time bar takes its labels from it.
    public DateTime? HeaderStartTime { get; } = headerStartTime;

    public Texture Texture { get; } = texture;
    public TimeSpan Pause { get; } = pause;
    public bool PauseDone { get; set; }
    public int Height => (int)Texture.Size.h;

    public void Dispose() => Texture.Dispose();
}

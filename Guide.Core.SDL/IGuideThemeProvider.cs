using Guide.Core.Model;
using Guide.Core.Model.Listings;
using Guide.Core.SDL.Wrappers;

namespace Guide.Core.SDL;

// This interface will define methods for generating channel label textures, channel programming
// data textures, time textures, and basically anything else that is related to a program guide.
public interface IGuideThemeProvider
{
    SDL3.SDL.Color DefaultGuideBackground { get; } // Might remove this in favor of a "render background" method.
    int DefaultWindowWidth { get; }
    int DefaultWindowHeight { get; }
    float ScaleRatio { get; }
    FullscreenMode DefaultFullscreenMode { get; }
    int GuideHeight { get; }
    int StandardRowHeight { get; }
    int TimeBarHeight { get; }
    int GridWidth { get; }

    // The space between the time bar and the guide.
    int TimeBarGap { get; }

    // The speed of the scroll, in unscaled pixels per second.
    double ScrollPixelsPerSecond { get; }

    void SetRenderer(nint renderer);

    // Draw the time bar, with its clock, at a position in pixels on the current render target.
    // The labels show labelsStartTime. While a header row passes the top of the guide, incomingStartTime
    // is its start time, and rollProgress is how far (in unscaled pixels) it has moved past the top.
    void DrawTimeBar(float x, float y, DateTime now, DateTime labelsStartTime, DateTime? incomingStartTime,
        float rollProgress);
    Texture GenerateRow(IListing listing);

    // The time to stop the scroll when the top of the row of this listing reaches the top of the guide.
    TimeSpan GetScrollPause(IListing listing);

    // Remove cached textures, for example after the scale changes.
    void ClearCachedTextures();
}

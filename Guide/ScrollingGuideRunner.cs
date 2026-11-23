using System.Diagnostics;
using Guide.Core;
using Guide.Core.Data;
using Guide.Core.Data.ChannelsDVR;
using Guide.Core.Logging;
using Guide.Core.Model.Listings;
using Guide.Core.SDL;
using Guide.Core.SDL.Esquire;
using Guide.Core.SDL.Wrappers;
using Guide.Core.Utilities;
using Microsoft.Extensions.Logging;
using SDL3;

namespace Guide;

// Clean up the amount of texture generation going on. That stuff kills performance.
// Make multiple providers (guide logo, copyright, "sbs", now playing on plex, etc.)
// How do we orchestrate said providers?
// Mark textures dirty? (Time)
// How do I manage caching of guide entries? Do I just regenerate the textures every time the guide time will roll-over?
// If so, can we do something to make it so we can procedurally create the textures instead of doing them all in one-shot to make the delay less obvious?
// At any given time, we need enough guide elements to render the whole screen filled. The guide is presented by being shifted down (or up?) a specific amount.
// Don't rerender every element if they have not changed.
// Allow listings provider to cache listings if they're not updated and silently update them in the background
//     if they need to be...? Maybe have it on a timer and do it async.
// yank esq specific code out of guide.cs
// https://github.com/sabdul-khabir/SDL3_gfx/tree/master use this to draw arrows?
// Some shows starting at 5 minutes to aren't getting pushed into the right boundary
// "Top content provider" ... provides images or videos, and a runtime (if necessary) to be rendered above the guide.

public class ScrollingGuideRunner : IDisposable
{
    private const string FiraCodeFontKey = "FiraCode";
    private const string FpsTextureKey = "fps";
    private const string GuideTextureKey = "guide";

    // The number of pixels of background color above the time bar.
    private const int TimeBarTopBand = 2;

    // At 60 Hz a frame has about 16.7 ms. Log a frame that takes much longer.
    private const int SlowFrameMilliseconds = 50;

    // Start the next load of the listings when this number of listings (or fewer) remain.
    private const int ListingsLoadThreshold = 5;

    // The number of row textures to make ahead of time.
    private const int ReadyRowsTarget = 2;

    private readonly ILogger _logger;
    private readonly RunnerOptions _options;
    private readonly Stopwatch _runTime = new();
    private readonly TextureManager _textureManager;
    private readonly FontManager _fontManager;
    private readonly IGuideThemeProvider _esquireGuideThemeProvider;
    private readonly ContainedLineLogger _containedLineLogger;
    private readonly List<int> _frameTimes;
    private readonly Queue<GuideRow> _rowsTextureQueue = new();

    private int _updateFPSFrameCounter;
    private IntPtr _window;
    private IntPtr _renderer;
    private bool _running;
    private bool _showLogs;
    private bool _showFps;
    private bool _vsync = true;
    private bool _fullscreen;
    private bool _thirtyFpsLimit;
    private FullscreenMode _currentFullscreenMode;
    private long _lastLinesLoggedCount = -1;
    private int _logLineCount;
    private bool _regenerateGuideTexture = true;
    private int _guideYSrcRectOffset;

    private bool _highDpi = true;

    // Listings from the loader that do not have a row texture yet.
    private readonly Queue<IListing> _pendingListings = new();

    // Row textures made ahead of time, one per frame, for the next regeneration of the guide texture.
    private readonly Queue<GuideRow> _readyRowsQueue = new();

    // The part of a scaled pixel that the scroll moved, but did not show yet.
    private double _scrollRemainder;
    private TimeSpan _lastScrollTime;
    private TimeSpan _pauseEndTime;
    private int _standByCycleIndex;
    private int _screenshotIndex;
    private double _nextScreenshotSeconds;

    // The time of the first column that the labels of the time bar show, and the header row that rolls in.
    private DateTime? _timeBarLabelsStartTime;
    private DateTime? _incomingHeaderStartTime;
    private float _timeBarRollProgress;

    private ListingsLoader? _listingsLoader;

    public ScrollingGuideRunner(ILogger logger, RunnerOptions options)
    {
        _options = options;

        if (!SDL.Init(SDL.InitFlags.Video))
        {
            throw new Exception($"There was an issue initializing SDL. {SDL.GetError()}");
        }

        _ = TTF.Init();

        _logger = logger;
        _textureManager = new TextureManager(logger);
        _fontManager = new FontManager(logger);
        _esquireGuideThemeProvider = new EsquireGuideThemeProvider(_logger);
        _currentFullscreenMode = _esquireGuideThemeProvider.DefaultFullscreenMode;
        _frameTimes = [];
        _containedLineLogger = ContainedLineLoggerProvider.Logger;
    }

    public void Run()
    {
        Setup();

        var stopWatch = new Stopwatch();
        while (_running)
        {
            stopWatch.Start();

            PollEvents();
            Render();

            if (_thirtyFpsLimit)
            {
                var delay = 33 - stopWatch.Elapsed.Milliseconds;
                if (delay > 0)
                    Thread.Sleep(delay);
            }

            stopWatch.Stop();

            if (stopWatch.ElapsedMilliseconds > SlowFrameMilliseconds)
                _logger.LogWarning("Slow frame: {Milliseconds} ms at {Seconds:0.00} s", stopWatch.ElapsedMilliseconds, _runTime.Elapsed.TotalSeconds);

            while (_frameTimes.Count >= 10)
            {
                _frameTimes.RemoveAt(0);
            }
            _frameTimes.Add(stopWatch.Elapsed.Milliseconds);

            stopWatch.Reset();
        }
    }

    private void Setup()
    {
        _window = SDL.CreateWindow("Guide",
            _esquireGuideThemeProvider.DefaultWindowWidth,
            _esquireGuideThemeProvider.DefaultWindowHeight,
            _highDpi
                ? SDL.WindowFlags.HighPixelDensity | SDL.WindowFlags.InputFocus
                : SDL.WindowFlags.InputFocus);

        if (_window == IntPtr.Zero)
        {
            throw new Exception($"There was an issue creating the window. {SDL.GetError()}");
        }

        _renderer = SDL.CreateRenderer(_window, "");
        _ = SDL.SetRenderDrawBlendMode(_renderer, SDL.BlendMode.Blend);

        if (_renderer == IntPtr.Zero)
        {
            throw new Exception($"There was an issue creating the renderer. {SDL.GetError()}");
        }

        _esquireGuideThemeProvider.SetRenderer(_renderer);

        if (_options.StartTime.HasValue)
            Clock.Offset = _options.StartTime.Value - DateTime.Now;

        IListingsDataProvider provider = _options.UseSampleListings
            ? new SampleListingsDataProvider()
            : new ChannelsDVRListingsDataProvider(_logger, _options.ChannelsDvrAddress);
        _listingsLoader = new ListingsLoader(_logger, provider);
        _listingsLoader.Request();

        SetFullscreen();
        SetVSync();
        SetScaleFromWindowSize();

        _running = true;
        _nextScreenshotSeconds = _options.ScreenshotDelaySeconds;
        _runTime.Start();
    }

    private void SaveScreenshot(string path)
    {
        var surface = SDL.RenderReadPixels(_renderer, null);
        if (surface == IntPtr.Zero)
        {
            _logger.LogError("Unable to read the window pixels: {Error}", SDL.GetError());
            return;
        }

        using var _ = new Surface(surface);
        if (Image.SavePNG(surface, path))
            _logger.LogInformation("Saved screenshot to {Path}", Path.GetFullPath(path));
        else
            _logger.LogError("Unable to save screenshot to {Path}: {Error}", path, SDL.GetError());
    }

    private void SetScaleFromWindowSize()
    {
        _ = SDL.GetWindowSizeInPixels(_window, out var windowWidthPixels, out var windowHeightPixels);
        Configuration.WindowWidth = windowWidthPixels;
        Configuration.WindowHeight = windowHeightPixels;
        _logger.LogInformation(@"[Window] Window Size: {Width} x {Height}", windowWidthPixels, windowHeightPixels);

        var rawWidthScale = (float)windowWidthPixels / _esquireGuideThemeProvider.DefaultWindowWidth;
        var rawHeightScale = (float)windowHeightPixels / _esquireGuideThemeProvider.DefaultWindowHeight;
        _logger.LogInformation(@"[Window] Scales: {Width} x {Height}", rawWidthScale, rawHeightScale);

        var smallerScale = rawWidthScale > rawHeightScale ? rawHeightScale : rawWidthScale;
        Configuration.Scale = (int)float.Floor(smallerScale);
        _logger.LogInformation(@"[Window] Scale: {Scale}x", Configuration.Scale);

        switch (_currentFullscreenMode)
        {
            case FullscreenMode.Letterbox:
                _logger.LogInformation($"Using fullscreen mode: {FullscreenMode.Letterbox}");

                Configuration.DrawableWidth = Configuration.Scale * _esquireGuideThemeProvider.DefaultWindowWidth;
                Configuration.DrawableHeight = Configuration.Scale * _esquireGuideThemeProvider.DefaultWindowHeight;
                _logger.LogInformation(@"[Window] Drawable Size: {Width} x {Height}", Configuration.DrawableWidth,
                    Configuration.DrawableHeight);

                Configuration.X = (windowWidthPixels - Configuration.DrawableWidth) / 2;
                Configuration.Y = (windowHeightPixels - Configuration.DrawableHeight) / 2;
                _logger.LogInformation(@"[Window] Offset: {Width} x {Height}", Configuration.X, Configuration.Y);

                Configuration.RenderedWidth = Configuration.DrawableWidth;
                Configuration.RenderedHeight = Configuration.DrawableHeight;
                break;
            case FullscreenMode.ZoomedFill:
                _logger.LogInformation($"Using fullscreen mode: {FullscreenMode.ZoomedFill}");

                Configuration.DrawableWidth = Configuration.Scale * _esquireGuideThemeProvider.DefaultWindowWidth;
                Configuration.DrawableHeight = Configuration.Scale * _esquireGuideThemeProvider.DefaultWindowHeight;
                _logger.LogInformation(@"[Window] Drawable Size: {Width} x {Height}", Configuration.DrawableWidth,
                    Configuration.DrawableHeight);

                Configuration.RenderedWidth = (int)(smallerScale * _esquireGuideThemeProvider.DefaultWindowWidth);
                Configuration.RenderedHeight = (int)(smallerScale * _esquireGuideThemeProvider.DefaultWindowHeight);
                _logger.LogInformation(@"[Window] Rendered: {Width} x {Height}", Configuration.RenderedWidth, Configuration.RenderedHeight);

                Configuration.X = (windowWidthPixels - Configuration.RenderedWidth) / 2;
                Configuration.Y = (windowHeightPixels - Configuration.RenderedHeight) / 2;
                _logger.LogInformation(@"[Window] Offset: {Width} x {Height}", Configuration.X, Configuration.Y);
                break;
            default:
                // Default: ScaledFill
                _logger.LogInformation($"Using fullscreen mode: {FullscreenMode.ScaledFill}");

                Configuration.DrawableWidth = windowWidthPixels;
                Configuration.DrawableHeight = windowHeightPixels;
                _logger.LogInformation(@"[Window] Drawable Size: {Width} x {Height}", Configuration.DrawableWidth,
                    Configuration.DrawableHeight);

                Configuration.X = 0;
                Configuration.Y = 0;
                _logger.LogInformation(@"[Window] Offset: {Width} x {Height}", Configuration.X, Configuration.Y);

                Configuration.RenderedWidth = Configuration.DrawableWidth;
                Configuration.RenderedHeight = Configuration.DrawableHeight;
                break;
        }
    }

    private void SetFullscreen()
    {
        _logger.LogInformation($"SDL: Setting fullscreen to: {_fullscreen}");
        _ = SDL.SetWindowFullscreen(_window, _fullscreen);
    }

    private void SetVSync()
    {
        _logger.LogInformation($"SDL: Setting vsync to: {_vsync}");
        _ = SDL.SetRenderVSync(_renderer, _vsync ? 1 : 0);
    }

    private void PollEvents()
    {
        while (SDL.PollEvent(out var sdlEvent))
        {
            if (sdlEvent.Type == (uint)SDL.EventType.Quit)
            {
                _logger.LogInformation("SDL: Quit event encountered.");
                _running = false;
            }
            else if (sdlEvent.Type == (uint)SDL.EventType.KeyDown)
            {
                switch (sdlEvent.Key.Key)
                {
                    case SDL.Keycode.D:
                        if (!_rowsTextureQueue.TryDequeue(out var removedRow))
                            break;
                        removedRow.Dispose();
                        _logger.LogInformation("Destroying current first row texture");
                        FillRowTexturesQueue();
                        break;
                    case SDL.Keycode.F:
                        _fullscreen = !_fullscreen;
                        SetFullscreen();
                        break;
                    case SDL.Keycode.L:
                        _showLogs = !_showLogs;
                        _logger.LogInformation("Show logs: {ShowLogs}", _showLogs);
                        break;
                    case SDL.Keycode.M:
                        _currentFullscreenMode = _currentFullscreenMode switch
                        {
                            FullscreenMode.ScaledFill => FullscreenMode.ZoomedFill,
                            FullscreenMode.ZoomedFill => FullscreenMode.Letterbox,
                            _ => FullscreenMode.ScaledFill
                        };

                        _logger.LogInformation("Fullscreen mode change: {FullscreenMode}", _currentFullscreenMode);

                        if (_fullscreen)
                        {
                            SetScaleFromWindowSize();
                            ResetGuideTextures();
                        }
                        break;
                    case SDL.Keycode.T:
                        _thirtyFpsLimit = !_thirtyFpsLimit;
                        _logger.LogInformation("30 FPS Limit: {ThirtyFpsLimit}", _thirtyFpsLimit);
                        break;
                    case SDL.Keycode.S:
                        SaveScreenshot($"guide-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                        break;
                    case SDL.Keycode.Q:
                        _logger.LogInformation("Quitting");
                        _running = false;
                        break;
                    case SDL.Keycode.P:
                        _showFps = !_showFps;
                        _logger.LogInformation("Show fps: {ShowFps}", _showFps);
                        break;
                    case SDL.Keycode.V:
                        _vsync = !_vsync;
                        SetVSync();
                        break;
                }
            }
            else if (sdlEvent.Type == (uint)SDL.EventType.WindowPixelSizeChanged)
            {
                SetScaleFromWindowSize();
                ResetGuideTextures();
            }
        }
    }

    // The scale changed, so the textures must be made again at the new scale.
    // The guide starts again at the next listing.
    private void ResetGuideTextures()
    {
        while (_rowsTextureQueue.TryDequeue(out var row))
            row.Dispose();

        while (_readyRowsQueue.TryDequeue(out var row))
            row.Dispose();

        _esquireGuideThemeProvider.ClearCachedTextures();
        _guideYSrcRectOffset = 0;
        _regenerateGuideTexture = true;
    }

    private int GetQueueHeight()
    {
        return _rowsTextureQueue.Sum(row => row.Height);
    }

    private void Render()
    {
        var theme = _esquireGuideThemeProvider;
        var scale = Configuration.Scale;

        // Draw to textures before the first draw to the window in this frame. After a draw to the
        // window, a change of render target waits for the next display refresh (with vsync).
        PrepareNextRow();

        if (_regenerateGuideTexture)
        {
            _regenerateGuideTexture = false;
            _textureManager.PurgeTexture(GuideTextureKey);
        }

        var guideTexture = _textureManager[GuideTextureKey];
        if (guideTexture == null)
        {
            guideTexture = new Texture(_renderer,
                theme.DefaultWindowWidth,
                theme.StandardRowHeight + theme.GuideHeight + (_guideYSrcRectOffset / scale));
            _textureManager[GuideTextureKey] = guideTexture;

            FillRowTexturesQueue();

            using (_ = new RenderingTarget(_renderer, guideTexture))
            {
                _ = InternalSDL3.SetRenderDrawColor(_renderer, theme.DefaultGuideBackground);
                _ = SDL.RenderClear(_renderer);

                var y = 0f;

                foreach (var row in _rowsTextureQueue)
                {
                    var rowDstFRect = new SDL.FRect
                    {
                        X = (guideTexture.Size.w - row.Texture.Size.w) / 2,
                        Y = y,
                        W = row.Texture.Size.w,
                        H = row.Height
                    };

                    _ = SDL.RenderTexture(_renderer, row.Texture.SdlTexture, IntPtr.Zero, rowDstFRect);

                    y += row.Height;
                }
            }
        }

        _ = SDL.SetRenderTarget(_renderer, IntPtr.Zero);
        _ = InternalSDL3.SetRenderDrawColor(_renderer, Colors.Black0);
        _ = SDL.RenderClear(_renderer);

        // The guide is at the bottom of the screen. The time bar is above it, with a small gap.
        var screenWidth = theme.DefaultWindowWidth * scale;
        var guideHeight = theme.GuideHeight * scale;
        var guideY = Configuration.Y + Configuration.RenderedHeight - guideHeight;
        var timeBarY = guideY - (theme.TimeBarHeight + theme.TimeBarGap) * scale;

        // ESQ fills the area of the guide, and a band above the time bar, with the background color.
        _ = InternalSDL3.SetRenderDrawColor(_renderer, theme.DefaultGuideBackground);
        var backgroundRect = new SDL.FRect
        {
            X = Configuration.X,
            Y = timeBarY - TimeBarTopBand * scale,
            W = screenWidth,
            H = guideY + guideHeight - (timeBarY - TimeBarTopBand * scale)
        };
        _ = SDL.RenderFillRect(_renderer, backgroundRect);

        var srcRect = new SDL.FRect
        {
            X = 0,
            Y = _guideYSrcRectOffset,
            W = guideTexture.Size.w,
            H = guideHeight
        };

        var guideDstFRect = new SDL.FRect
        {
            X = Configuration.X,
            Y = guideY,
            W = guideTexture.Size.w,
            H = guideHeight
        };

        _ = SDL.RenderTexture(_renderer, guideTexture.SdlTexture, srcRect, guideDstFRect);

        var scrollStep = GetScrollStep();
        var rowsBelow = GetQueueHeight() - (_guideYSrcRectOffset + guideHeight);

        if (rowsBelow > 0)
        {
            ScrollBy(Math.Min(scrollStep, rowsBelow));
        }
        else if (HasListingsAvailable())
        {
            // The rows ran out, and new listings are available. Put them in the guide texture.
            _regenerateGuideTexture = true;
        }

        UpdateTimeBarRoll();

        // Determine if the offset pushes us to the bottom of the guide texture.
        if (_guideYSrcRectOffset + guideHeight > guideTexture.Size.h)
        {
            // While the first texture's height is less than the y offset, purge and recalculate the y offset.
            while (_rowsTextureQueue.Any() && _rowsTextureQueue.First().Height <= _guideYSrcRectOffset)
            {
                _logger.LogDebug("Guide adjustment: removing row texture");

                using var first = _rowsTextureQueue.Dequeue();
                _guideYSrcRectOffset -= first.Height;
            }

            _regenerateGuideTexture = true;
        }

        var now = Clock.Now;
        var timeBarX = Configuration.X + (screenWidth - theme.GridWidth * scale) / 2f;
        _timeBarLabelsStartTime ??= Time.ClampToNextHalfHourIfTenMinutesAway(now);
        theme.DrawTimeBar(timeBarX, timeBarY, now, _timeBarLabelsStartTime.Value, _incomingHeaderStartTime,
            _timeBarRollProgress);

        if (_showLogs) RenderLogs();
        if (_showFps) RenderFps();

        if (_options.ScreenshotPath != null && _runTime.Elapsed.TotalSeconds >= _nextScreenshotSeconds)
        {
            if (_options.ScreenshotCount <= 1)
            {
                SaveScreenshot(_options.ScreenshotPath);
                _running = false;
            }
            else
            {
                // Save a series: name-000.png, name-001.png, and so on.
                var path = Path.Combine(Path.GetDirectoryName(_options.ScreenshotPath) ?? ".",
                    $"{Path.GetFileNameWithoutExtension(_options.ScreenshotPath)}-{_screenshotIndex:000}.png");
                SaveScreenshot(path);
                _screenshotIndex++;
                _nextScreenshotSeconds += _options.ScreenshotIntervalSeconds;
                _running = _screenshotIndex < _options.ScreenshotCount;
            }
        }

        _ = SDL.RenderPresent(_renderer);
    }

    // The number of scaled pixels to scroll in this frame. It depends on the time, not on the frame rate.
    private int GetScrollStep()
    {
        var now = _runTime.Elapsed;
        var elapsed = now - _lastScrollTime;
        _lastScrollTime = now;

        if (now < _pauseEndTime)
            return 0;

        // Do not make up for a long frame, for example while the window moves.
        var seconds = Math.Min(elapsed.TotalSeconds, 0.1);
        _scrollRemainder += seconds * _esquireGuideThemeProvider.ScrollPixelsPerSecond * Configuration.Scale;

        var step = (int)_scrollRemainder;
        _scrollRemainder -= step;
        return step;
    }

    // Scroll down by a number of scaled pixels. Stop at the top of a row that pauses the scroll.
    private void ScrollBy(int step)
    {
        var rowTop = 0;
        var rows = _rowsTextureQueue.ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            if (rowTop > _guideYSrcRectOffset + step)
                break;

            if (row.Pause > TimeSpan.Zero && !row.PauseDone && rowTop >= _guideYSrcRectOffset)
            {
                row.PauseDone = true;

                // Measured on ESQ: the row directly after a row that stopped the scroll does not stop it.
                if (i + 1 < rows.Count)
                    rows[i + 1].PauseDone = true;

                _guideYSrcRectOffset = rowTop;
                _pauseEndTime = _runTime.Elapsed + row.Pause;
                _logger.LogInformation("Scroll paused for {Seconds} s at {Time:0.00} s", row.Pause.TotalSeconds, _runTime.Elapsed.TotalSeconds);
                _scrollRemainder = 0;
                return;
            }

            rowTop += row.Height;
        }

        _guideYSrcRectOffset += step;
    }

    // ESQ takes the labels of the time bar from the header rows of the grid. When a header row reaches
    // the top of the guide, its labels roll into the time bar while the row moves up by its height.
    private void UpdateTimeBarRoll()
    {
        var scale = Configuration.Scale;
        var timeBarHeight = _esquireGuideThemeProvider.TimeBarHeight;
        var rowTop = 0;

        _incomingHeaderStartTime = null;

        foreach (var row in _rowsTextureQueue)
        {
            if (rowTop > _guideYSrcRectOffset)
                break;

            if (row.HeaderStartTime.HasValue)
            {
                var progress = (_guideYSrcRectOffset - rowTop) / (float)scale;

                if (progress >= timeBarHeight)
                {
                    if (_timeBarLabelsStartTime != row.HeaderStartTime)
                        _logger.LogInformation("Time bar labels rolled to {Time:h:mm tt} at {Seconds:0.00} s",
                            row.HeaderStartTime, _runTime.Elapsed.TotalSeconds);

                    _timeBarLabelsStartTime = row.HeaderStartTime;
                }
                else
                {
                    _incomingHeaderStartTime = row.HeaderStartTime;
                    _timeBarRollProgress = progress;
                }
            }

            rowTop += row.Height;
        }
    }

    private bool HasListingsAvailable() =>
        _readyRowsQueue.Count > 0 || _pendingListings.Count > 0 || _listingsLoader?.HasResult == true;

    private bool TryGetNextListing(out IListing listing)
    {
        if (_pendingListings.Count == 0 && _listingsLoader != null && _listingsLoader.TryTake(out var listings))
        {
            foreach (var loadedListing in listings)
                _pendingListings.Enqueue(loadedListing);
        }

        // Start the next load before the listings run out, so it is ready in time.
        if (_pendingListings.Count <= ListingsLoadThreshold)
            _listingsLoader?.Request();

        if (_pendingListings.TryDequeue(out listing!))
            return true;

        // No listings could be loaded. Until a load succeeds, show what ESQ shows with no listings:
        // the time header, the date, and the stand-by message (ER007), again and again.
        if (_listingsLoader?.LastLoadFailed == true)
        {
            var now = Clock.Now;
            listing = (_standByCycleIndex++ % 3) switch
            {
                0 => new TimeBarListing(Time.ClampToNextHalfHourIfTenMinutesAway(now)),
                1 => new DateBannerListing(now.Date),
                _ => new StandByListing()
            };
            return true;
        }

        return false;
    }

    private GuideRow? TryGenerateNextRow()
    {
        if (!TryGetNextListing(out var listing))
            return null;

        try
        {
            return new GuideRow(_esquireGuideThemeProvider.GenerateRow(listing),
                _esquireGuideThemeProvider.GetScrollPause(listing),
                (listing as TimeBarListing)?.StartTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to make a row for a {Listing}", listing.GetType().Name);
            return null;
        }
    }

    // Make one row texture per frame ahead of time, so that the regeneration of the guide texture
    // does not have to make many rows in one frame.
    private void PrepareNextRow()
    {
        if (_readyRowsQueue.Count >= ReadyRowsTarget)
            return;

        var row = TryGenerateNextRow();
        if (row != null)
            _readyRowsQueue.Enqueue(row);
    }

    private void FillRowTexturesQueue()
    {
        var guideTexture = _textureManager[GuideTextureKey];
        if (guideTexture == null)
            return;

        // Fill the guide texture. If no rows are available, stop. The guide stops scrolling
        // until the loader has more listings.
        while (GetQueueHeight() < guideTexture.Size.h)
        {
            var row = _readyRowsQueue.TryDequeue(out var readyRow) ? readyRow : TryGenerateNextRow();
            if (row == null)
                break;

            _rowsTextureQueue.Enqueue(row);
        }
    }

    private void RenderFps()
    {
        if (_updateFPSFrameCounter == 0)
        {
            _updateFPSFrameCounter = 30;

            var fps = 1000 / _frameTimes.Average();
            using var surface = new Surface(TTF.RenderTextBlended(_fontManager[FiraCodeFontKey], $"{fps:0.00 FPS}", 0, Colors.Yellow));
            _textureManager[FpsTextureKey] = new Texture(SDL.CreateTextureFromSurface(_renderer, surface.SdlSurface));
        }
        else
        {
            _updateFPSFrameCounter--;
        }

        if (_textureManager[FpsTextureKey] == null)
            return;

        var texture = _textureManager[FpsTextureKey];

        if (texture == null)
            return;

        var lineHeight = _fontManager.FontConfigurations[FiraCodeFontKey].PointSize;

        _ = SDL.GetTextureSize(texture.SdlTexture, out var width, out var height);
        var fRect = new SDL.FRect
        {
            W = width,
            H = height,
            X = 0,
            Y = Configuration.RenderedHeight - lineHeight * Configuration.Scale
        };

        _ = SDL.RenderTexture(_renderer, texture.SdlTexture, IntPtr.Zero, fRect);
    }

    private void RenderLogs()
    {
        if (_lastLinesLoggedCount != _containedLineLogger.LinesLogged)
        {
            _lastLinesLoggedCount = _containedLineLogger.LinesLogged;

            var font = _fontManager[FiraCodeFontKey];
            var yellow = new SDL.Color { A = 255, R = 255, G = 255, B = 0 };

            var lines = _containedLineLogger.GetLines();
            _logLineCount = lines.Count;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                using var surface = new Surface(TTF.RenderTextBlended(font, line, 0, yellow));
                _textureManager[$"log_{i}"] = new Texture(SDL.CreateTextureFromSurface(_renderer, surface.SdlSurface));
            }
        }

        var lineHeight = _fontManager.FontConfigurations[FiraCodeFontKey].PointSize;
        var y = Configuration.WindowHeight - (lineHeight * Configuration.Scale * 50);

        for (var i = 0; i < _logLineCount; i++)
        {
            var texture = _textureManager[$"log_{i}"];

            if (texture == null)
                continue;

            _ = SDL.GetTextureSize(texture.SdlTexture, out var width, out var height);
            var fRect = new SDL.FRect
            {
                W = width,
                H = height,
                X = Configuration.WindowWidth - width,
                Y = y
            };
            _ = SDL.RenderTexture(_renderer, texture.SdlTexture, IntPtr.Zero, fRect);
            y += lineHeight * Configuration.Scale;
        }
    }

    public void Dispose()
    {
        _listingsLoader?.Dispose();

        while (_rowsTextureQueue.TryDequeue(out var row))
            row.Dispose();

        while (_readyRowsQueue.TryDequeue(out var readyRow))
            readyRow.Dispose();

        _textureManager.Dispose();

        TTF.Quit();
        SDL.DestroyRenderer(_renderer);
        SDL.DestroyWindow(_window);
        SDL.Quit();
    }
}

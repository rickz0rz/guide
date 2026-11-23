using System.Globalization;
using System.Runtime.InteropServices;
using Guide.Core.Model.Listings;
using Guide.Core.Model.Listings.Channel;
using Guide.Core.SDL.Wrappers;
using Guide.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Guide.Core.SDL.Esquire;

public class EsquireGuideThemeProvider : IGuideThemeProvider, IDisposable
{
    private const string EsquireGuideFontName = "PrevueGrid";

    private const int ChannelColumnWidth = 144;
    private const int StandardColumnWidth = 172;
    private const int LastColumnWidth = StandardColumnWidth + 36;
    private static readonly int[] ProgramColumnWidths = [StandardColumnWidth, StandardColumnWidth, LastColumnWidth];
    private const int SingleArrowWidth = 16;
    private const int DoubleArrowWidth = 24;
    private const int BevelMargin = Frame.BevelSize + 1;

    // Text positions from the ESQ source (PrevueC.font 25: the height is 25 and the baseline is 19).
    // Each y is the top of the font: the baseline less 19.
    private const int FontHeight = 25;
    private const int FontBaseline = 19;
    private const int TextLineHeight = FontHeight - 1;
    private const int ExtraLineSpacing = 28;
    private const int FullRowMaximumLines = 20;
    private const int FirstLineTop = 24 - FontBaseline;
    private const int CellTextLeft = 6;
    // Measured on ESQ: the left edges of the right arrows in the grid, and the top of all arrows in a cell.
    private const int SingleRightArrowX = 635;
    private const int DoubleRightArrowX = 629;
    private const int ArrowTop = 6;
    private const int ChannelTextCenter = 90;
    private const int ChannelNumberTop = 24 - FontBaseline;
    private const int CallSignTop = 48 - FontBaseline;
    private const int HeaderTextTop = 25 - FontBaseline;
    private const int ClockLeft = 44;

    // The source centers the labels in 172 pixels and adds 2. The screenshot of ESQ shows them
    // 5 pixels more to the right, so use the measured value.
    private const int HeaderLabelOffset = 7;

    // The offset from the top-left of the ESQ font to the origin of the texture of our TrueType text.
    // Measured on screenshots against ESQ (docs/prevue-guide.png in the esquire repository).
    private const float FontOriginX = -1;
    private const float FontOriginY = 0;
    private const int TextShadowOverhang = 2;

    // ESQ stops the scroll for 2 seconds (120 fields) when a tall row reaches the top of the grid.
    private static readonly TimeSpan TallRowScrollPause = TimeSpan.FromSeconds(2);

    private const string StandByMessage = "Please Stand By for your Local Listings.  ER007";
    private const int StandByMessageLeft = 36;
    private const int StandByMessageAreaWidth = 624;
    private const int StandByMessageMaximumWidth = 612;

    public int GuideHeight => 172;
    public int TimeBarGap => 2;

    // ESQ scrolls 1 line (2 pixels here) every 4 fields at the default speed (3): 30 pixels each second.
    public double ScrollPixelsPerSecond => 30;
    public int StandardRowHeight => 56;
    public int TimeBarHeight => 34;
    public int GridWidth => ChannelColumnWidth + ProgramColumnWidths.Sum();

    private nint _renderer;

    private readonly ILogger _logger;
    private readonly FontManager _fontManager;
    private FontSizeManager _fontSizeManager;

    private Texture? _timeBarFrameTexture;
    private readonly Dictionary<DateTime, Texture> _timeBarLabelsTextures = new();
    private Texture? _clockTexture;
    private string? _clockText;

    public EsquireGuideThemeProvider(ILogger logger)
    {
        _logger = logger;
        _fontManager = new FontManager(logger);
        _fontSizeManager = new FontSizeManager(_fontManager[EsquireGuideFontName]);
    }

    public void SetRenderer(nint renderer)
    {
        _renderer = renderer;
    }

    public SDL3.SDL.Color DefaultGuideBackground => Colors.Blue51;
    public int DefaultWindowWidth => 716;
    public int DefaultWindowHeight => 436;
    public float ScaleRatio => 1f;
    public FullscreenMode DefaultFullscreenMode => FullscreenMode.Letterbox;

    private string GetLineText(Program program)
    {
        var titleValue = program.Title;

        var title = program.IsMovie
            ? titleValue.Split("\"", StringSplitOptions.RemoveEmptyEntries).First().Split("(").First().Trim()
                .Replace("%", "%%")
            : titleValue.Replace("%", "%%");

        var description = program.Description.Replace("%", "%%");

        if (!string.IsNullOrWhiteSpace(description))
        {
            description += " ";
        }

        var rating = !string.IsNullOrWhiteSpace(program.Rating) ? $"%{program.Rating.Replace("-", "")}% " : "";
        var stereo = program.IsStereo ? "%STEREO% " : string.Empty;
        var closedCaptioning = program.IsClosedCaptioned ? "%CC% " : string.Empty;

        var extraString = program.IsMovie
            ? $"{rating}({program.Year}) {description}{stereo}{closedCaptioning}"
            : $"{rating}{stereo}{closedCaptioning}".Trim();
        extraString = string.IsNullOrWhiteSpace(extraString) ? string.Empty : $" {extraString}".TrimEnd();

        var generatedDescription = program.IsMovie
            ? $"\"{title.Trim()}\" {extraString}"
            : $"{title.Trim()} {extraString}";

        return Font.FormatWithFontTokens(generatedDescription);
    }

    public Texture GenerateRow(IListing listing) => listing switch
    {
        ChannelListing channelListing => GenerateChannelListingTexture(channelListing),
        TimeBarListing timeBarListing => GenerateTimeBarTexture(timeBarListing.StartTime),
        ImageListing imageListing => GenerateImageListing(imageListing),
        StandByListing => GenerateStandByTexture(),
        DateBannerListing dateBannerListing => GenerateDateBannerTexture(dateBannerListing),
        _ => throw new NotImplementedException(listing.GetType().Name)
    };

    public void ClearCachedTextures()
    {
        _timeBarFrameTexture?.Dispose();
        _timeBarFrameTexture = null;
        foreach (var texture in _timeBarLabelsTextures.Values)
            texture.Dispose();
        _timeBarLabelsTextures.Clear();
        _clockTexture?.Dispose();
        _clockTexture = null;
    }

    // A program that fits in the grid: the columns it fills and the text to show.
    private record ProgramCell(
        int FirstColumn,
        int LastColumn,
        ArrowType LeftArrow,
        ArrowType RightArrow,
        List<string> TextLines)
    {
        public int LeftMargin => ArrowWidth(LeftArrow);
        public int RightMargin => ArrowWidth(RightArrow);
        public int ColumnCount => LastColumn - FirstColumn + 1;

        private static int ArrowWidth(ArrowType arrowType) => arrowType switch
        {
            ArrowType.Single => SingleArrowWidth,
            ArrowType.Double => DoubleArrowWidth,
            _ => 0
        };
    }

    private static int ColumnX(int column) => ChannelColumnWidth + ProgramColumnWidths.Take(column).Sum();

    private static int ColumnsWidth(int firstColumn, int lastColumn) =>
        ProgramColumnWidths.Skip(firstColumn).Take(lastColumn - firstColumn + 1).Sum();

    private static ArrowType GetArrow(TimeSpan timeOutsideGrid) => timeOutsideGrid.TotalMinutes switch
    {
        > 30 => ArrowType.Double,
        > 0 => ArrowType.Single,
        _ => ArrowType.None
    };

    // The height of a row with this number of text lines.
    private int GetRowHeight(int lines) => lines <= 2 ? StandardRowHeight : lines * ExtraLineSpacing;

    // The font top of a line of a program cell. ESQ puts the baselines of a two-line cell at 24 and 48.
    // In a cell with more lines, the second baseline is at 50, and the line after it at 28 * line + 22.
    // The last of these lines is 2 pixels higher.
    private static int GetLineTop(int line, int lineCount)
    {
        if (line == 0)
            return FirstLineTop;

        if (lineCount <= 2)
            return FirstLineTop + TextLineHeight;

        if (line == 1)
            return FirstLineTop + TextLineHeight + 2;

        var baseline = ExtraLineSpacing * line + (line == lineCount - 1 ? 20 : 22);
        return baseline - FontBaseline;
    }

    // Measured on ESQ: the scroll stops for a row that is taller than a standard row, that is, a program
    // across the full grid with more than 2 lines (usually a movie, sometimes a sports event).
    public TimeSpan GetScrollPause(IListing listing) =>
        listing is ChannelListing channelListing && GetProgramCells(channelListing).Any(cell => cell.TextLines.Count > 2)
            ? TallRowScrollPause
            : TimeSpan.Zero;

    private List<ProgramCell> GetProgramCells(ChannelListing channelListing)
    {
        var gridStartTime = channelListing.FirstColumnStartTime;
        var gridEndTime = gridStartTime.AddMinutes(30 * ProgramColumnWidths.Length);
        var lastColumn = ProgramColumnWidths.Length - 1;

        var cells = new List<ProgramCell>();
        var nextFreeColumn = 0;

        foreach (var program in channelListing.Programs.OrderBy(p => p.StartTime))
        {
            if (program.EndTime <= gridStartTime || program.StartTime >= gridEndTime)
                continue;

            var firstColumn = Math.Clamp((int)Math.Floor((program.StartTime - gridStartTime).TotalMinutes / 30), 0, lastColumn);
            var lastProgramColumn = (int)Math.Ceiling((program.EndTime - gridStartTime).TotalMinutes / 30) - 1;
            lastProgramColumn = Math.Clamp(lastProgramColumn, firstColumn, lastColumn);

            // Show only the first program that starts in a half hour.
            if (firstColumn < nextFreeColumn)
                continue;

            var leftArrow = GetArrow(gridStartTime - program.StartTime);
            var rightArrow = GetArrow(program.EndTime - gridEndTime);

            var cell = new ProgramCell(firstColumn, lastProgramColumn, leftArrow, rightArrow, []);

            // ESQ wraps the text at 172 pixels per column, less 12. On the first two lines, the arrows
            // also take space. Only a program that fills all the columns can have more than 2 lines.
            var wrapWidth = StandardColumnWidth * cell.ColumnCount - 12;

            // The outline and the shadow of our TrueType text go 2 pixels past the advance of the
            // last letter. Keep that space before a right arrow, so the text does not touch it.
            if (rightArrow != ArrowType.None)
                wrapWidth -= TextShadowOverhang;
            var maximumLines = cell.ColumnCount == ProgramColumnWidths.Length ? FullRowMaximumLines : 2;
            var textLines = GetTextLines(cell.LeftMargin, cell.RightMargin, wrapWidth, GetLineText(program));

            cells.Add(cell with { TextLines = textLines.Take(maximumLines).ToList() });
            nextFreeColumn = lastProgramColumn + 1;
        }

        return cells;
    }

    private Texture GenerateChannelListingTexture(ChannelListing channelListing)
    {
        var cells = GetProgramCells(channelListing);

        var lines = cells.Count > 0 ? cells.Max(cell => cell.TextLines.Count) : 0;
        var rowHeight = GetRowHeight(lines);

        var rowTexture = new Texture(_renderer, GridWidth, rowHeight);

        using (_ = new RenderingTarget(_renderer, rowTexture))
        {
            _ = InternalSDL3.SetRenderDrawColor(_renderer, Colors.Blue85);
            _ = SDL3.SDL.RenderClear(_renderer);

            // The channel number and the call sign are centered on x = 90.
            DrawCenteredText(channelListing.ChannelNumber, Colors.Yellow, ChannelTextCenter, ChannelNumberTop,
                ChannelColumnWidth);
            DrawCenteredText(channelListing.CallSign, Colors.Yellow, ChannelTextCenter, CallSignTop,
                ChannelColumnWidth);
        }

        Frame.CreateBevelOnTexture(_renderer, rowTexture,
            new SDL3.SDL.Rect { X = 0, Y = 0, W = ChannelColumnWidth, H = rowHeight });

        // Draw an empty cell for each half hour that has no program.
        var filledColumns = cells.SelectMany(cell => Enumerable.Range(cell.FirstColumn, cell.ColumnCount)).ToHashSet();
        for (var column = 0; column < ProgramColumnWidths.Length; column++)
        {
            if (filledColumns.Contains(column))
                continue;

            Frame.CreateBevelOnTexture(_renderer, rowTexture, new SDL3.SDL.Rect
            {
                X = ColumnX(column), Y = 0, W = ProgramColumnWidths[column], H = rowHeight
            });
        }

        foreach (var cell in cells)
        {
            var width = ColumnsWidth(cell.FirstColumn, cell.LastColumn);
            using var programTexture = GenerateProgramTexture(cell, width, rowHeight);

            using (_ = new RenderingTarget(_renderer, rowTexture))
            {
                var fRect = new SDL3.SDL.FRect
                {
                    X = ColumnX(cell.FirstColumn) * Configuration.Scale,
                    Y = 0,
                    W = programTexture.Size.w,
                    H = programTexture.Size.h
                };

                _ = SDL3.SDL.RenderTexture(_renderer, programTexture.SdlTexture, IntPtr.Zero, fRect);
            }
        }

        return rowTexture;
    }

    private Texture GenerateProgramTexture(ProgramCell cell, int width, int height)
    {
        var programTexture = new Texture(_renderer, width, height);

        using (_ = new RenderingTarget(_renderer, programTexture))
        {
            _ = InternalSDL3.SetRenderDrawColor(_renderer, Colors.Transparent);
            _ = SDL3.SDL.RenderClear(_renderer);

            if (cell.LeftArrow != ArrowType.None)
                DrawArrow(cell.LeftArrow, pointsRight: false, CellTextLeft);

            // ESQ puts the right arrows at fixed places in the grid, not at the right edge of the cell.
            if (cell.RightArrow != ArrowType.None)
            {
                var arrowX = cell.RightArrow == ArrowType.Double ? DoubleRightArrowX : SingleRightArrowX;
                DrawArrow(cell.RightArrow, pointsRight: true, arrowX - ColumnX(cell.FirstColumn));
            }

            for (var line = 0; line < cell.TextLines.Count; line++)
            {
                // The arrows are only as tall as the first two lines.
                var leftMargin = line < 2 ? cell.LeftMargin : 0;
                DrawText(cell.TextLines[line], Colors.Gray170, CellTextLeft + leftMargin,
                    GetLineTop(line, cell.TextLines.Count));
            }
        }

        Frame.CreateBevelOnTexture(_renderer, programTexture);
        return programTexture;
    }

    // Draw the time bar with its clock. The labels show the times of the last header row that reached
    // the top of the guide. While the next header row passes the top, ESQ rolls its labels in: in each
    // box, the old labels move up and out and the new labels come in from below. The frame does not move.
    public void DrawTimeBar(float x, float y, DateTime now, DateTime labelsStartTime,
        DateTime? incomingStartTime, float rollProgress)
    {
        _timeBarFrameTexture ??= GenerateTimeBarTexture(null);

        var timeBarRect = new SDL3.SDL.FRect { X = x, Y = y, W = _timeBarFrameTexture.Size.w, H = _timeBarFrameTexture.Size.h };
        _ = SDL3.SDL.RenderTexture(_renderer, _timeBarFrameTexture.SdlTexture, IntPtr.Zero, timeBarRect);

        var roll = incomingStartTime.HasValue ? Math.Clamp(rollProgress, 0, TimeBarHeight) : 0;
        DrawTimeBarLabels(x, y, labelsStartTime, -roll);

        if (incomingStartTime.HasValue)
            DrawTimeBarLabels(x, y, incomingStartTime.Value, TimeBarHeight - roll);

        DrawClock(x, y, now);
    }

    // Draw the labels moved down by an offset (unscaled), clipped to the inside of each box.
    private void DrawTimeBarLabels(float x, float y, DateTime startTime, float offset)
    {
        var labels = GetTimeBarLabelsTexture(startTime);
        var scale = Configuration.Scale;

        for (var column = 0; column < ProgramColumnWidths.Length; column++)
        {
            // The inside of the box, in the coordinates of the time bar.
            float insideX = ColumnX(column) + Frame.BevelSize;
            float insideWidth = ProgramColumnWidths[column] - 2 * Frame.BevelSize;
            float insideTop = Frame.BevelSize;
            float insideBottom = TimeBarHeight - Frame.BevelSize;

            // The part of the labels texture that lands inside the box.
            var sourceTop = Math.Max(insideTop - offset, 0);
            var sourceBottom = Math.Min(insideBottom - offset, TimeBarHeight);
            if (sourceBottom <= sourceTop)
                continue;

            var source = new SDL3.SDL.FRect
            {
                X = insideX * scale, Y = sourceTop * scale, W = insideWidth * scale, H = (sourceBottom - sourceTop) * scale
            };
            var destination = new SDL3.SDL.FRect
            {
                X = x + insideX * scale, Y = y + (sourceTop + offset) * scale, W = source.W, H = source.H
            };

            _ = SDL3.SDL.RenderTexture(_renderer, labels.SdlTexture, source, destination);
        }
    }

    private Texture GetTimeBarLabelsTexture(DateTime startTime)
    {
        if (_timeBarLabelsTextures.TryGetValue(startTime, out var texture))
            return texture;

        // Keep only the textures that a roll can use.
        foreach (var oldStartTime in _timeBarLabelsTextures.Keys.Where(time => time < startTime.AddHours(-1)).ToList())
        {
            _timeBarLabelsTextures[oldStartTime].Dispose();
            _timeBarLabelsTextures.Remove(oldStartTime);
        }

        texture = new Texture(_renderer, GridWidth, TimeBarHeight);
        using (_ = new RenderingTarget(_renderer, texture))
        {
            _ = InternalSDL3.SetRenderDrawColor(_renderer, Colors.Transparent);
            _ = SDL3.SDL.RenderClear(_renderer);
            DrawHeaderLabels(startTime);
        }

        _timeBarLabelsTextures[startTime] = texture;
        return texture;
    }

    private void DrawClock(float x, float y, DateTime now)
    {
        // ESQ formats the clock with "%2d:%02d:%02d" at x = 44. A one-digit hour has a space before it.
        var hour = now.Hour % 12 == 0 ? 12 : now.Hour % 12;
        var clockText = $"{hour}:{now:mm}:{now:ss}";
        if (_clockTexture == null || _clockText != clockText)
        {
            _clockTexture?.Dispose();
            _clockTexture = new Texture(_renderer, ChannelColumnWidth, TimeBarHeight);

            using (_ = new RenderingTarget(_renderer, _clockTexture))
            {
                _ = InternalSDL3.SetRenderDrawColor(_renderer, Colors.Transparent);
                _ = SDL3.SDL.RenderClear(_renderer);

                var padding = hour < 10 ? TextWidth(" ") : 0;
                DrawText(clockText, Colors.Gray170, ClockLeft + padding, HeaderTextTop);
            }

            _clockText = clockText;
        }

        var clockRect = new SDL3.SDL.FRect { X = x, Y = y, W = _clockTexture.Size.w, H = _clockTexture.Size.h };
        _ = SDL3.SDL.RenderTexture(_renderer, _clockTexture.SdlTexture, IntPtr.Zero, clockRect);
    }

    // The header row: the frame and, with a start time, the half-hour labels. The clock box is empty.
    // ESQ scrolls this row in the grid. The time bar uses the frame without labels.
    private Texture GenerateTimeBarTexture(DateTime? firstColumnStartTime)
    {
        var timeBarTexture = new Texture(_renderer, GridWidth, TimeBarHeight);

        using (_ = new RenderingTarget(_renderer, timeBarTexture))
        {
            _ = InternalSDL3.SetRenderDrawColor(_renderer, Colors.Blue136);
            _ = SDL3.SDL.RenderClear(_renderer);

            if (firstColumnStartTime.HasValue)
                DrawHeaderLabels(firstColumnStartTime.Value);
        }

        Frame.CreateBevelOnTexture(_renderer, timeBarTexture,
            new SDL3.SDL.Rect { X = 0, Y = 0, W = ChannelColumnWidth, H = TimeBarHeight });

        for (var column = 0; column < ProgramColumnWidths.Length; column++)
        {
            Frame.CreateBevelOnTexture(_renderer, timeBarTexture,
                new SDL3.SDL.Rect { X = ColumnX(column), Y = 0, W = ProgramColumnWidths[column], H = TimeBarHeight });
        }

        return timeBarTexture;
    }

    // ESQ centers each label in 172 pixels, also in the last column, which is wider.
    private void DrawHeaderLabels(DateTime firstColumnStartTime)
    {
        for (var column = 0; column < ProgramColumnWidths.Length; column++)
        {
            var label = firstColumnStartTime.AddMinutes(30 * column).ToString("h:mm tt", CultureInfo.InvariantCulture);
            var labelX = ColumnX(column) + (StandardColumnWidth - TextWidth(label)) / 2 + HeaderLabelOffset;
            DrawText(label, Colors.Yellow, labelX, HeaderTextTop);
        }
    }

    // A row with an empty channel box and the date across the program columns.
    private Texture GenerateDateBannerTexture(DateBannerListing dateBannerListing)
    {
        var rowTexture = new Texture(_renderer, GridWidth, TimeBarHeight);
        var date = dateBannerListing.Date.ToString("dddd MMMM d yyyy", CultureInfo.InvariantCulture);
        var programsWidth = StandardColumnWidth * ProgramColumnWidths.Length;

        using (_ = new RenderingTarget(_renderer, rowTexture))
        {
            _ = InternalSDL3.SetRenderDrawColor(_renderer, Colors.Blue85);
            _ = SDL3.SDL.RenderClear(_renderer);

            // Measured on ESQ: the date is centered in 3 * 172 pixels, with no offset.
            DrawText(date, Colors.Yellow, ChannelColumnWidth + (programsWidth - TextWidth(date)) / 2f, HeaderTextTop);
        }

        Frame.CreateBevelOnTexture(_renderer, rowTexture,
            new SDL3.SDL.Rect { X = 0, Y = 0, W = ChannelColumnWidth, H = TimeBarHeight });
        Frame.CreateBevelOnTexture(_renderer, rowTexture,
            new SDL3.SDL.Rect { X = ChannelColumnWidth, Y = 0, W = GridWidth - ChannelColumnWidth, H = TimeBarHeight });

        return rowTexture;
    }

    // The red gradient of the stand-by row, measured on ESQ: one color for each 2 pixels, from y = 4.
    private static readonly (byte Red, byte Blue)[] StandByGradient =
    [
        (153, 34), (153, 34), (153, 51), (170, 34), (170, 51), (187, 34), (187, 51), (204, 34),
        (204, 51), (187, 51), (187, 34), (170, 51), (170, 34), (153, 51), (153, 34), (136, 51),
        (136, 34), (119, 51), (119, 34), (102, 51), (102, 34), (85, 51), (85, 34), (85, 34)
    ];

    // ESQ (_NEWGRID_DrawAwaitingListingsMessage): one bevel frame across the full grid, with the
    // message on one line. The message is centered in the 624 pixels that start at x = 36.
    private Texture GenerateStandByTexture()
    {
        var rowTexture = new Texture(_renderer, GridWidth, StandardRowHeight);

        using (_ = new RenderingTarget(_renderer, rowTexture))
        {
            _ = InternalSDL3.SetRenderDrawColor(_renderer, Colors.Blue85);
            _ = SDL3.SDL.RenderClear(_renderer);

            for (var line = 0; line < StandByGradient.Length; line++)
            {
                var (red, blue) = StandByGradient[line];
                _ = SDL3.SDL.SetRenderDrawColor(_renderer, red, 34, blue, 255);
                InternalSDL3.RenderFillRect(_renderer, new ScaledFRect
                {
                    X = 0, Y = Frame.BevelSize + 2 * line, W = GridWidth, H = 2
                });
            }

            var messageX = StandByMessageLeft + Math.Max(0, StandByMessageAreaWidth - TextWidth(StandByMessage)) / 2;
            var messageTop = (StandardRowHeight - FontHeight) / 2 - 1;
            // ESQ centers the message with the width of the full string, but its wrapped-text routine
            // draws the words with one space between them.
            var words = StandByMessage.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            DrawText(string.Join(' ', words), Colors.Gray170, messageX, messageTop, StandByMessageMaximumWidth);
        }

        Frame.CreateBevelOnTexture(_renderer, rowTexture);
        return rowTexture;
    }

    private Texture GenerateImageListing(ImageListing imageListing)
    {
        using var openedImageTexture = new Texture(_logger, _renderer, imageListing.Filename);
        var imageTexture = new Texture(_renderer, GridWidth, (int)openedImageTexture.Size.h);

        using (_ = new RenderingTarget(_renderer, imageTexture))
        {
            _ = InternalSDL3.SetRenderDrawColor(_renderer, Colors.Blue85);
            _ = SDL3.SDL.RenderClear(_renderer);

            // Todo: If the image is bigger than the texture width, scale it down to fit. Useful for high-res images.
            // Also maybe cap the height or allow it to be capped?

            var dstRect = new SDL3.SDL.FRect
            {
                X = ((GridWidth - openedImageTexture.Size.w) / 2) * Configuration.Scale,
                Y = 0,
                W = openedImageTexture.Size.w * Configuration.Scale,
                H = openedImageTexture.Size.h * Configuration.Scale
            };

            _ = SDL3.SDL.RenderTexture(_renderer, openedImageTexture.SdlTexture, IntPtr.Zero, dstRect);
        }

        return imageTexture;
    }

    // The width of the text in unscaled pixels, as Amiga TextLength measures it (the sum of the advances).
    private int TextWidth(string text) => text.Sum(c => _fontSizeManager[c].width);

    // Draw text on the current render target. The x and y are in unscaled pixels, at the top-left of
    // the font (ESQ: the baseline less the baseline of the font). A maximum width cuts the text.
    private void DrawText(string text, SDL3.SDL.Color color, float x, float y, int? maximumWidth = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        using var texture = new Texture(GenerateDropShadowText(_renderer, _fontManager[EsquireGuideFontName],
            _fontManager.GetOutlinedFont(EsquireGuideFontName), text, color));

        var width = maximumWidth.HasValue ? Math.Min(texture.Size.w, maximumWidth.Value * Configuration.Scale) : texture.Size.w;
        var source = new SDL3.SDL.FRect { X = 0, Y = 0, W = width, H = texture.Size.h };
        var destination = new SDL3.SDL.FRect
        {
            X = (x + FontOriginX) * Configuration.Scale,
            Y = (y + FontOriginY) * Configuration.Scale,
            W = width,
            H = texture.Size.h
        };

        _ = SDL3.SDL.RenderTexture(_renderer, texture.SdlTexture, source, destination);
    }

    // Draw text centered on a point. Keep it inside the area from 0 to the given width.
    private void DrawCenteredText(string text, SDL3.SDL.Color color, int center, int y, int areaWidth)
    {
        var width = TextWidth(text);
        var x = center - width / 2;
        x = Math.Min(x, areaWidth - BevelMargin - width);
        DrawText(text, color, Math.Max(x, BevelMargin), y);
    }

    private List<string> GetTextLines(int leftMargin, int rightMargin, int columnWidth, string targetString)
    {
        var currentLineLength = 0;
        var currentLineNumber = 0;
        var currentLine = string.Empty;
        var renderedLines = new List<string>();
        var lineWidth = columnWidth - leftMargin - rightMargin;

        foreach (var component in targetString.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var componentLength = component.ToCharArray().Select(c => _fontSizeManager[$"{c}"]).Sum(v => v.width);
            var paddedComponentLength = (string.IsNullOrWhiteSpace(currentLine) ? 0 : _fontSizeManager[' '].width) + componentLength;

            if (currentLineLength + paddedComponentLength > lineWidth)
            {
                if (!string.IsNullOrWhiteSpace(currentLine))
                {
                    renderedLines.Add(currentLine);
                    currentLine = component;
                    currentLineLength = componentLength;

                    currentLineNumber++;
                    lineWidth = currentLineNumber >= 2
                        ? columnWidth
                        : columnWidth - leftMargin - rightMargin;
                }
                else
                {
                    // We have to split the line in the middle somewhere.
                    var chars = component.ToCharArray();
                    var componentSubLength = 0;
                    var chunk = string.Empty;

                    foreach (var targetChar in chars)
                    {
                        var glyphWidth = _fontSizeManager[targetChar].width;
                        var newSubLength = componentSubLength + glyphWidth;

                        if (newSubLength > lineWidth)
                        {
                            renderedLines.Add(chunk);
                            chunk = string.Empty;
                            componentSubLength = 0;

                            currentLineNumber++;
                            lineWidth = currentLineNumber >= 2
                                ? columnWidth
                                : columnWidth - leftMargin - rightMargin;
                        }

                        chunk = $"{chunk}{targetChar}";
                        componentSubLength += glyphWidth;
                    }

                    if (!string.IsNullOrWhiteSpace(chunk))
                    {
                        var padding = string.IsNullOrWhiteSpace(currentLine) ? string.Empty : " ";
                        currentLine = $"{currentLine}{padding}{chunk}";
                        currentLineLength += componentSubLength;
                    }
                }
            }
            else
            {
                var padding = string.IsNullOrWhiteSpace(currentLine) ? string.Empty : " ";
                currentLine = $"{currentLine}{padding}{component}";
                currentLineLength += paddedComponentLength;
            }
        }

        if (!string.IsNullOrWhiteSpace(currentLine))
        {
            renderedLines.Add(currentLine);
        }

        return renderedLines;
    }

    // The arrows of ESQ, copied from a screenshot of ESQ. Each character is one pixel wide and one
    // line (2 pixels) tall: '#' fill, 'k' black, 'm' dark gray, 's' shadow, '.' nothing.
    // The left arrows are mirror images of the right arrows.
    private static readonly string[] SingleRightArrowPixels =
    [
        "k#mks...........",
        "k##mks..........",
        "k###mks.........",
        "k####mks........",
        "k#####mks.......",
        "k######mks......",
        "k#######mks.....",
        "k########mks....",
        "k#########mks...",
        "k##########mks..",
        "k###########mks.",
        "k############mks",
        "k############kk.",
        "k###########kk..",
        "k##########kk...",
        "k#########kk....",
        "k########kk.....",
        "k#######kk......",
        "k######kk.......",
        "k#####kk........",
        "k####kk.........",
        "k###kk..........",
        "k##kk..........."
    ];

    private static readonly string[] DoubleRightArrowPixels =
    [
        "k#mks...k#mks...........",
        "k##mks..k##mks..........",
        "k###mks.k###mks.........",
        "k####mksk####mks........",
        "k#####mkk#####mks.......",
        "k######mkm#####mks......",
        "k#######mkm#####mks.....",
        "k########mkm#####mks....",
        "k#########mkm#####mks...",
        "k##########mkm#####mks..",
        "k###########mkm#####mks.",
        "k############mkm#####mks",
        "k############kk######kk.",
        "k###########kk######kk..",
        "k##########kk######kk...",
        "k#########kk######kk....",
        "k########kk######kk.....",
        "k#######kk######kk......",
        "k######kk######kk.......",
        "k#####kkk#####kk........",
        "k####kk.k####kk.........",
        "k###kk..k###kk..........",
        "k##kk...k##kk..........."
    ];

    private void DrawArrow(ArrowType arrowType, bool pointsRight, int x)
    {
        var pixels = arrowType == ArrowType.Double ? DoubleRightArrowPixels : SingleRightArrowPixels;

        for (var line = 0; line < pixels.Length; line++)
        {
            var row = pointsRight ? pixels[line] : new string(pixels[line].Reverse().ToArray());

            // Draw each run of one color as one rectangle.
            var start = 0;
            while (start < row.Length)
            {
                var end = start;
                while (end < row.Length && row[end] == row[start])
                    end++;

                SDL3.SDL.Color? color = row[start] switch
                {
                    '#' => Colors.Gray170,
                    'k' => Colors.Black17,
                    'm' => Colors.Gray93,
                    's' => Colors.Shadow,
                    _ => null
                };

                if (color.HasValue)
                {
                    _ = InternalSDL3.SetRenderDrawColor(_renderer, color.Value);
                    InternalSDL3.RenderFillRect(_renderer, new ScaledFRect
                    {
                        X = x + start, Y = ArrowTop + 2 * line, W = end - start, H = 2
                    });
                }

                start = end;
            }
        }
    }

    private static IntPtr GenerateDropShadowText(IntPtr renderer, IntPtr font, IntPtr outlinedFont, string text,
        SDL3.SDL.Color fontColor)
    {
        const int horizontalOffset = 1;
        const int verticalOffset = 1;

        using var outlineSurface = new Surface(SDL3.TTF.RenderTextBlended(outlinedFont, text, 0, Colors.Black17));
        using var outlineTexture = new Texture(SDL3.SDL.CreateTextureFromSurface(renderer, outlineSurface.SdlSurface));

        using var shadowSurface = new Surface(SDL3.TTF.RenderTextBlended(font, text, 0, Colors.Black17));
        using var shadowTexture = new Texture(SDL3.SDL.CreateTextureFromSurface(renderer, shadowSurface.SdlSurface));
        using var mainSurface = new Surface(SDL3.TTF.RenderTextBlended(font, text, 0, fontColor));
        using var mainTexture = new Texture(SDL3.SDL.CreateTextureFromSurface(renderer, mainSurface.SdlSurface));

        var outlineSdlSurface = Marshal.PtrToStructure<SDL3.SDL.Surface>(outlineSurface.SdlSurface);

        // Generate a texture that's 1 * scale wider than the outline, as it's going to
        // first be used to draw the drop shadow at a 1 x 1 offset.
        var resultTexture = SDL3.SDL.CreateTexture(renderer, SDL3.SDL.PixelFormat.RGBA8888,
            SDL3.SDL.TextureAccess.Target, (outlineSdlSurface.Width + 1 * Configuration.Scale),
            outlineSdlSurface.Height + 1 * Configuration.Scale);
        _ = SDL3.SDL.SetTextureBlendMode(resultTexture, SDL3.SDL.BlendMode.Blend);

        // Switch to the texture for rendering.
        using (_ = new RenderingTarget(renderer, resultTexture))
        {
            _ = SDL3.SDL.SetRenderDrawColor(renderer, 0, 0, 0, 0);
            _ = SDL3.SDL.RenderClear(renderer);

            // Draw clock, black shadow outline
            var dstRect1 = new SDL3.SDL.FRect
                { H = outlineTexture.Size.h, W = outlineTexture.Size.w, X = horizontalOffset * Configuration.Scale, Y = verticalOffset * Configuration.Scale };
            _ = SDL3.SDL.RenderTexture(renderer, outlineTexture.SdlTexture, IntPtr.Zero, dstRect1);

            // Draw clock, black shadow main
            var dstRect2 = new SDL3.SDL.FRect
                { H = shadowTexture.Size.h, W = shadowTexture.Size.w, X = (horizontalOffset + 2) * Configuration.Scale, Y = (verticalOffset + 2) * Configuration.Scale };
            _ = SDL3.SDL.RenderTexture(renderer, shadowTexture.SdlTexture, IntPtr.Zero, dstRect2);

            // Draw clock, black outline
            var dstRect3 = new SDL3.SDL.FRect
                { H = outlineTexture.Size.h, W = outlineTexture.Size.w, X = (horizontalOffset - 1) * Configuration.Scale, Y = (verticalOffset - 1) * Configuration.Scale };
            _ = SDL3.SDL.RenderTexture(renderer, outlineTexture.SdlTexture, IntPtr.Zero, dstRect3);

            // Draw clock, main without outline
            var dstRect4 = new SDL3.SDL.FRect
                { H = mainTexture.Size.h, W = mainTexture.Size.w, X = horizontalOffset * Configuration.Scale, Y = verticalOffset * Configuration.Scale };
            _ = SDL3.SDL.RenderTexture(renderer, mainTexture.SdlTexture, IntPtr.Zero, dstRect4);
        }

        return resultTexture;
    }

    public void Dispose()
    {
        ClearCachedTextures();
        _fontSizeManager.Dispose();
        _fontManager.Dispose();
    }
}

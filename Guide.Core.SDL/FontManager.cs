using System.Collections.Concurrent;
using System.Text.Json;
using Guide.Core.Model;
using Microsoft.Extensions.Logging;

namespace Guide.Core.SDL;

public class FontManager : IDisposable
{
    private readonly ConcurrentDictionary<(string, int), nint> _openedFonts;
    private readonly ConcurrentDictionary<(string, int), nint> _outlinedFonts = new();
    private readonly ILogger _logger;

    public readonly Dictionary<string, FontConfiguration> FontConfigurations;

    public nint this[string fontName] => this[(fontName, FontConfigurations[fontName].PointSize)];

    public nint this[(string fontName, int size) key]
    {
        get
        {
            var scaledSize = key.size * Configuration.Scale;
            var scaledKey = key with { size = scaledSize };

            if (!_openedFonts.ContainsKey(scaledKey))
            {
                _logger.LogInformation("Font {fontName} @ {size} pt. (scaled: {scaledSize} pt.) has not been opened",
                    key.fontName, key.size, scaledSize);
                var filename = FontConfigurations[key.fontName].Filename;
                _logger.LogInformation("Opening font {filename} @ {scaledSize} pt", filename, scaledSize);
                _openedFonts.TryAdd(scaledKey, SDL3.TTF.OpenFont(filename, scaledKey.size));
            }

            return _openedFonts.TryGetValue(scaledKey, out var font) ? font : IntPtr.Zero;
        }
    }

    // Get a font that always draws an outline of one unscaled pixel. A change to the outline of a font
    // empties its glyph cache, so keep a separate font for the outline.
    public nint GetOutlinedFont(string fontName)
    {
        var scaledKey = (fontName, FontConfigurations[fontName].PointSize * Configuration.Scale);

        return _outlinedFonts.GetOrAdd(scaledKey, key =>
        {
            var font = SDL3.TTF.OpenFont(FontConfigurations[fontName].Filename, key.Item2);
            _ = SDL3.TTF.SetFontOutline(font, Configuration.Scale);
            return font;
        });
    }

    public FontManager(ILogger logger)
    {
        _logger = logger;
        _openedFonts = new ConcurrentDictionary<(string, int), nint>();

        FontConfigurations = JsonSerializer.Deserialize<Dictionary<string, FontConfiguration>>(File.ReadAllText("assets/fonts/fonts.json"));
    }

    public void Dispose()
    {
        foreach (var fonts in new[] { _openedFonts, _outlinedFonts })
        {
            foreach (var openedFont in fonts.Keys)
            {
                if (fonts.TryRemove(openedFont, out var font))
                {
                    SDL3.TTF.CloseFont(font);
                }
            }
        }
    }
}

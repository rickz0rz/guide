namespace Guide;

public class RunnerOptions
{
    // The address of the Channels DVR server that supplies the listings.
    public string ChannelsDvrAddress { get; init; } = "http://192.168.0.195:8089";

    // When set, save a PNG of the window to this path and then quit.
    public string? ScreenshotPath { get; init; }

    // Show fixed sample listings in place of the listings of a server.
    public bool UseSampleListings { get; init; }

    // The time that the guide shows at the start, in place of the current time.
    public DateTime? StartTime { get; init; }

    // The number of screenshots to save, and the seconds between them.
    public int ScreenshotCount { get; init; } = 1;
    public double ScreenshotIntervalSeconds { get; init; } = 0.1;

    // The number of seconds to wait before the screenshot.
    public double ScreenshotDelaySeconds { get; init; } = 3;

    public static RunnerOptions Parse(string[] args)
    {
        var address = Environment.GetEnvironmentVariable("GUIDE_CHANNELS_DVR") ?? new RunnerOptions().ChannelsDvrAddress;
        string? screenshotPath = null;
        var screenshotDelay = 3d;
        var useSampleListings = false;
        var screenshotCount = 1;
        var screenshotInterval = 0.1;
        DateTime? startTime = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--channels-dvr" when i + 1 < args.Length:
                    address = args[++i];
                    break;
                case "--screenshot" when i + 1 < args.Length:
                    screenshotPath = Path.GetFullPath(args[++i]);
                    break;
                case "--screenshot-count" when i + 1 < args.Length:
                    screenshotCount = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--screenshot-interval" when i + 1 < args.Length:
                    screenshotInterval = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--sample":
                    useSampleListings = true;
                    break;
                case "--time" when i + 1 < args.Length:
                    startTime = DateTime.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--screenshot-delay" when i + 1 < args.Length:
                    screenshotDelay = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }

        return new RunnerOptions
        {
            ChannelsDvrAddress = address,
            ScreenshotPath = screenshotPath,
            ScreenshotDelaySeconds = screenshotDelay,
            UseSampleListings = useSampleListings,
            ScreenshotCount = screenshotCount,
            ScreenshotIntervalSeconds = screenshotInterval,
            StartTime = startTime
        };
    }
}

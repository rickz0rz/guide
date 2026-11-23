using System.Text.Json;
using Guide.Core.Model.Listings;
using Guide.Core.Model.Listings.Channel;
using Guide.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace Guide.Core.Data.ChannelsDVR;

public class ChannelsDVRListingsDataProvider : IListingsDataProvider
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly ILogger _logger;
    private readonly string _address;
    private DateTime _previousStartTime;
    private JsonDocument? _guideJsonData;

    public ChannelsDVRListingsDataProvider(ILogger logger, string address)
    {
        _logger = logger;
        _address = address;
        _previousStartTime = DateTime.MinValue;
    }

    public bool RequiresManualUpdating => false;
    // When set, add a "GUIDE" channel row at the top of the listings. The server listings
    // usually have a guide channel already, so this is off by default.
    public int? GuideChannelNumber { get; set; }

    public void Dispose()
    {
        _guideJsonData?.Dispose();
    }

    public async IAsyncEnumerable<IListing> GetEntries()
    {
        var returnedChannels = false;

        // Get the current time to determine the half-hours we're showing.
        var now = Clock.Now;
        var startTime = Time.ClampToNextHalfHourIfTenMinutesAway(now);

        // ESQ starts each cycle of the grid with the time header and the date.
        // Logo and copyright text will be returned via other data providers eventually.
        yield return new TimeBarListing(startTime);
        yield return new DateBannerListing(now.Date);
        yield return new ImageListing("assets/images/guide-channel.png");

        if (_previousStartTime != startTime || _guideJsonData == null)
        {
            _logger.LogInformation("Guide data dirty, fetching new guide data...");
            var guideJsonData = await GetGuide();

            // Mark the data as current only after the fetch succeeds, so that a failed fetch tries again.
            _guideJsonData?.Dispose();
            _guideJsonData = guideJsonData;
            _previousStartTime = startTime;
            _logger.LogInformation("Guide data retrieved.");
        }

        if (GuideChannelNumber.HasValue)
        {
            yield return new ChannelListing
            {
                CallSign = "GUIDE",
                ChannelNumber = "1",
                FirstColumnStartTime = startTime,
                Programs =
                [
                    new Program
                    {
                        StartTime = now.AddHours(-4),
                        EndTime = now.AddHours(4),
                        Title = "Can't decide? Check out the GUIDE!",
                        IsMovie = false,
                        Description = "",
                        IsClosedCaptioned = false,
                        IsStereo = false,
                        Rating = "",
                        Year = "2025"
                    }
                ]
            };
        }

        foreach (var guideElement in _guideJsonData.RootElement.EnumerateArray())
        {
            returnedChannels = true;

            var channelElement = guideElement.GetProperty("Channel");
            var channelListing = new ChannelListing();

            try
            {
                channelListing.CallSign = GetString(channelElement, "CallSign") ?? GetString(channelElement, "Name") ?? string.Empty;
                channelListing.ChannelNumber = GetString(channelElement, "Number") ?? string.Empty;
                channelListing.FirstColumnStartTime = startTime;
                channelListing.Programs = [];

                foreach (var airingElement in guideElement.GetProperty("Airings").EnumerateArray())
                {
                    var start = airingElement.GetProperty("Time").GetInt64();
                    var airingStart = DateTimeOffset.FromUnixTimeSeconds(start).LocalDateTime;
                    var airingEnd = airingStart.AddSeconds(airingElement.GetProperty("Duration").GetInt32());

                    // The grid has half-hour columns, so lock the start and the end to :00 or :30.
                    var startDateTime = Time.RoundToNearestHalfHour(airingStart);
                    var endDateTime = Time.RoundToNearestHalfHour(airingEnd);

                    var endTime =
                        startTime.AddMinutes(90); // make this dynamic depending on the width in the configuration.

                    if ((startDateTime < startTime && endDateTime > startTime && endDateTime <= endTime) ||
                        (startDateTime >= startTime && startDateTime < endTime && endDateTime > startTime &&
                         endDateTime <= endTime) ||
                        (startDateTime >= startTime && startDateTime < endTime && endDateTime > endTime) ||
                        (startDateTime < startTime && endDateTime > endTime))
                        {
                            var hasTags = airingElement.TryGetProperty("Tags", out var tags);
                            var isMovie = airingElement.TryGetProperty("MovieID", out _);

                            channelListing.Programs.Add(new Program
                            {
                                StartTime = startDateTime,
                                EndTime = endDateTime,
                                Title = airingElement.GetProperty("Title").GetString(),
                                IsMovie = isMovie,
                                Description = airingElement.TryGetProperty("Summary", out _)
                                    ? airingElement.GetProperty("Summary").GetString()
                                    : string.Empty,
                                Rating = airingElement.TryGetProperty("ContentRating", out _)
                                    ? airingElement.GetProperty("ContentRating").GetString()
                                    : string.Empty,
                                Year = isMovie
                                    ? airingElement.GetProperty("ReleaseYear").GetInt32().ToString()
                                    : string.Empty,
                                IsStereo = hasTags && tags.EnumerateArray().Any(x => x.GetString().Equals("Stereo")),
                                IsClosedCaptioned =
                                    hasTags && tags.EnumerateArray().Any(x => x.GetString().Equals("CC"))
                            });
                        }
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Unable to process channel element");
                continue;
            }

            yield return channelListing;
        }

        // The server has no channels. The GUIDE channel does not count.
        if (!returnedChannels)
        {
            yield return new StandByListing();
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private async Task<JsonDocument> GetGuide()
    {
        var response = await HttpClient.GetAsync($"{_address}/devices/ANY/guide");
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }
}

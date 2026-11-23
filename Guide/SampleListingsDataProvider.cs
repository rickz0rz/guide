using Guide.Core.Data;
using Guide.Core.Model.Listings;
using Guide.Core.Model.Listings.Channel;
using ListingProgram = Guide.Core.Model.Listings.Channel.Program;
using Guide.Core.Utilities;

namespace Guide;

// Fixed listings for development, with no server. The first three channels are the channels of the
// ESQ screenshot in the esquire repository (docs/prevue-guide.png), so the two can be compared.
public class SampleListingsDataProvider : IListingsDataProvider
{
    public async IAsyncEnumerable<IListing> GetEntries()
    {
        await Task.CompletedTask;

        var now = Clock.Now;
        var start = Time.ClampToNextHalfHourIfTenMinutesAway(now);

        yield return new TimeBarListing(start);
        yield return new DateBannerListing(now.Date);

        yield return Channel("27", "KSINDT",
            Show(start, 0, 60, "Poldark on Masterpiece", cc: true),
            Show(start, 60, 120, "Poldark on Masterpiece"));

        yield return Channel("44", "KPTHDT",
            Show(start, -35, 190, "NFL Football", cc: true));

        yield return Channel("45", "KTIVDT",
            Show(start, 0, 30, "The Goldbergs", cc: true),
            Show(start, 30, 60, "The Goldbergs", cc: true),
            Show(start, 60, 90, "Bob's Burgers", cc: true));

        yield return Channel("2.2", "WJBKDT2",
            Movie(start, -40, 75, "The Wild Bunch", "1969", "R",
                "An outlaw (William Holden) and a gang plot to rob a U.S. Army train."));

        // A second tall row directly after a tall row. ESQ does not stop the scroll for it.
        yield return Channel("2.3", "WJBKDT3",
            Movie(start, -10, 100, "Rio 2", "2014", "G",
                "Blu and his family travel to the Amazon to learn to live as wild birds."));

        yield return Channel("4", "WDIV",
            Show(start, 0, 30, "Today With Hoda & Jenna", rating: "TV-PG", cc: true),
            Show(start, 30, 90, "Local 4 News at Noon", stereo: true, cc: true));

        yield return Channel("7", "WXYZ",
            Show(start, 0, 60, "General Hospital", rating: "TV-14", cc: true),
            Show(start, 60, 90, "Tamron Hall", rating: "TV-PG", cc: true));

        // A tall row after other rows. ESQ stops the scroll for it again.
        yield return Channel("708", "ESPNUH",
            Show(start, -20, 120, "College Basketball: ESPN Events Invitational, Adventure Group: Akron vs. Wright State",
                cc: true));
    }

    private static ChannelListing Channel(string number, string callSign, params ListingProgram[] programs) => new()
    {
        ChannelNumber = number,
        CallSign = callSign,
        FirstColumnStartTime = programs.Length > 0 ? Time.ClampToNextHalfHourIfTenMinutesAway(Clock.Now) : default,
        Programs = programs.ToList()
    };

    private static ListingProgram Show(DateTime start, int fromMinutes, int toMinutes, string title,
        string rating = "", bool stereo = false, bool cc = false) => new()
    {
        Title = title,
        Description = string.Empty,
        Year = string.Empty,
        Rating = rating,
        IsStereo = stereo,
        IsClosedCaptioned = cc,
        StartTime = start.AddMinutes(fromMinutes),
        EndTime = start.AddMinutes(toMinutes)
    };

    private static ListingProgram Movie(DateTime start, int fromMinutes, int toMinutes, string title, string year,
        string rating, string description) => new()
    {
        Title = title,
        IsMovie = true,
        Description = description,
        Year = year,
        Rating = rating,
        StartTime = start.AddMinutes(fromMinutes),
        EndTime = start.AddMinutes(toMinutes)
    };

    public void Dispose()
    {
    }
}

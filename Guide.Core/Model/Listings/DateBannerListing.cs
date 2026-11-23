namespace Guide.Core.Model.Listings;

// A row with the date, for example "Sunday November 1 2020".
public class DateBannerListing(DateTime date) : IListing
{
    public DateTime Date { get; set; } = date;
}

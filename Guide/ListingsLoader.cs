using Guide.Core.Data;
using Guide.Core.Model.Listings;
using Microsoft.Extensions.Logging;

namespace Guide;

// Loads the listings on a background task, so that a network request does not stop the render loop.
// The render thread asks for a load with Request, and later takes the result with TryTake.
public class ListingsLoader : IDisposable
{
    // After a load fails, wait this long before the next attempt.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly IListingsDataProvider _provider;
    private readonly CancellationTokenSource _cancellation = new();

    private Task<List<IListing>?>? _loadTask;
    private DateTime _nextAttemptTime = DateTime.MinValue;

    public ListingsLoader(ILogger logger, IListingsDataProvider provider)
    {
        _logger = logger;
        _provider = provider;
    }

    public bool IsLoading => _loadTask is { IsCompleted: false };

    public bool HasResult => _loadTask is { IsCompleted: true };

    // True when the most recent load that was taken failed.
    public bool LastLoadFailed { get; private set; }

    // Start a load, if no load is in progress or waiting to be taken.
    public void Request()
    {
        if (_loadTask != null || DateTime.Now < _nextAttemptTime)
            return;

        var token = _cancellation.Token;
        _loadTask = Task.Run(() => LoadAsync(token), token);
    }

    // Get the listings of a load that is complete. Returns false while a load is in progress,
    // when no load was requested, or when the load failed.
    public bool TryTake(out List<IListing> listings)
    {
        listings = [];

        if (_loadTask is not { IsCompleted: true })
            return false;

        var result = _loadTask.IsCompletedSuccessfully ? _loadTask.Result : null;
        _loadTask = null;

        LastLoadFailed = result == null;

        if (result == null)
        {
            _nextAttemptTime = DateTime.Now + RetryDelay;
            return false;
        }

        listings = result;
        return true;
    }

    private async Task<List<IListing>?> LoadAsync(CancellationToken token)
    {
        var started = DateTime.Now;

        try
        {
            var listings = new List<IListing>();

            await foreach (var listing in _provider.GetEntries().WithCancellation(token))
            {
                listings.Add(listing);
            }

            _logger.LogInformation("Loaded {Count} listings in {Milliseconds:0} ms",
                listings.Count, (DateTime.Now - started).TotalMilliseconds);
            return listings;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to load the listings. Retry in {Seconds} seconds.", RetryDelay.TotalSeconds);
            return null;
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();

        try
        {
            _loadTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The load was cancelled or failed. Nothing more to do.
        }

        _cancellation.Dispose();
        _provider.Dispose();
    }
}

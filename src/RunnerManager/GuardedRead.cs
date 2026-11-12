namespace RunnerManager;

/// <summary>
/// A read that can hang for good (a WMI query, for example), for a loop that must keep going: it is waited for at most
/// <paramref name="limit"/>, and not started again while an earlier one still runs, so hung reads don't pile up. A read
/// that took too long is logged once, until one works again.
/// </summary>
internal sealed class GuardedRead<T>(Func<Task<T>> read, TimeSpan limit, Action<string> log, string slowText, string againText)
    where T : class
{
    private Task<T>? _running;
    private bool _slow;

    /// <summary>The result, or null when this read or an earlier one took longer than the limit. A failed read throws.</summary>
    public async Task<T?> ReadAsync()
    {
        if (_running is { IsCompleted: false }) return null;
        var running = _running = read();
        try
        {
            var result = await running.WaitAsync(limit);
            if (_slow)
            {
                _slow = false;
                log(againText);
            }
            return result;
        }
        catch (TimeoutException)
        {
            if (!_slow)
            {
                _slow = true;
                log(slowText);
            }
            // Nobody waits for it any more.
            _ = running.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return null;
        }
    }
}

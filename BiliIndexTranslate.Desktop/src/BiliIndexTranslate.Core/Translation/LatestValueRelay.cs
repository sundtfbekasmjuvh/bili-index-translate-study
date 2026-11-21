namespace BiliIndexTranslate.Core.Translation;

/// <summary>
/// Hands streamed values to a consumer that runs somewhere else, typically the UI thread. A model
/// emits deltas faster than a window can lay them out; while one hand-over is waiting to run, newer
/// values replace the waiting one instead of queueing behind it, so the consumer is given the newest
/// state once rather than every state in between.
/// </summary>
public sealed class LatestValueRelay<T> where T : class
{
    private readonly Action<Action> _post;
    private readonly Action<T> _consume;
    private T? _waiting;

    /// <param name="post">Schedules a callback where the consumer runs, e.g. on the dispatcher.</param>
    /// <param name="consume">Receives the newest value; runs wherever <paramref name="post"/> runs its callbacks.</param>
    public LatestValueRelay(Action<Action> post, Action<T> consume)
    {
        _post = post;
        _consume = consume;
    }

    /// <summary>Makes <paramref name="value"/> the one to deliver. May be called from any thread.</summary>
    public void Offer(T value)
    {
        if (Interlocked.Exchange(ref _waiting, value) is null)
            _post(Deliver);
    }

    /// <summary>Drops a value that has not been delivered yet, for example because the final result replaces it.</summary>
    public void Clear() => Interlocked.Exchange(ref _waiting, null);

    private void Deliver()
    {
        var value = Interlocked.Exchange(ref _waiting, null);
        if (value is not null) _consume(value);
    }
}

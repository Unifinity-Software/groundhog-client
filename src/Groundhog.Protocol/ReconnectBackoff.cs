namespace Groundhog.Protocol;

/// <summary>
/// The agent's wait between reconnection attempts: 1, 2, 4, 8, 16 then 30 seconds, forever, each varied by up to
/// ±20% so many agents don't reconnect in step, and never more than <see cref="MaxDelay"/>.
/// </summary>
public static class ReconnectBackoff
{
    /// <summary>Wait before the first retry, before jitter.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait, after jitter.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>The largest fraction by which a delay is shortened or lengthened.</summary>
    public const double JitterFraction = 0.2;

    /// <summary>
    /// The delay before retry number <paramref name="attempt"/> (0 for the first retry), before jitter.
    /// </summary>
    public static TimeSpan GetBaseDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        // 1 s << 5 = 32 s already exceeds the cap; stopping there also keeps large attempts from overflowing.
        return attempt >= 5 ? MaxDelay : TimeSpan.FromTicks(InitialDelay.Ticks << attempt);
    }

    /// <summary>
    /// The delay before retry number <paramref name="attempt"/> (0 for the first retry), with jitter.
    /// </summary>
    /// <param name="attempt">Zero-based retry number. Reset it to zero after a successful connection.</param>
    /// <param name="randomSample">
    /// A random value in [0, 1), typically <see cref="Random.NextDouble"/>. 0 gives the shortest delay, values
    /// near 1 the longest. Pass it in so callers and tests control the source.
    /// </param>
    public static TimeSpan GetDelay(int attempt, double randomSample)
    {
        if (double.IsNaN(randomSample) || randomSample < 0 || randomSample >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomSample), randomSample, "Must be in [0, 1).");
        }

        var factor = 1 - JitterFraction + (2 * JitterFraction * randomSample);
        var ticks = (long)Math.Round(GetBaseDelay(attempt).Ticks * factor);
        return TimeSpan.FromTicks(Math.Min(ticks, MaxDelay.Ticks));
    }

    /// <summary>
    /// The delay before retry number <paramref name="attempt"/>, with jitter drawn from <paramref name="random"/>.
    /// </summary>
    public static TimeSpan GetDelay(int attempt, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return GetDelay(attempt, random.NextDouble());
    }
}

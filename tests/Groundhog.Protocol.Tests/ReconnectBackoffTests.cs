namespace Groundhog.Protocol.Tests;

public class ReconnectBackoffTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(6, 30)]
    [InlineData(int.MaxValue, 30)]
    public void Doubles_from_one_second_up_to_thirty(int attempt, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ReconnectBackoff.GetBaseDelay(attempt));

    [Theory]
    [InlineData(0, 0.0, 800)]
    [InlineData(0, 0.5, 1000)]
    [InlineData(3, 0.0, 6400)]
    [InlineData(3, 0.999999, 9600)]
    [InlineData(5, 0.0, 24000)]
    [InlineData(5, 0.999999, 30000)] // jitter never pushes past the cap
    public void Jitter_varies_the_delay_by_up_to_twenty_percent(int attempt, double sample, int expectedMilliseconds) =>
        Assert.Equal(expectedMilliseconds, ReconnectBackoff.GetDelay(attempt, sample).TotalMilliseconds, 0.5);

    [Fact]
    public void Draws_jitter_from_the_supplied_random_source()
    {
        var delays = Enumerable.Range(0, 10).Select(attempt => ReconnectBackoff.GetDelay(attempt, new FixedRandom(0)));

        Assert.Equal([0.8, 1.6, 3.2, 6.4, 12.8, 24, 24, 24, 24, 24], delays.Select(d => d.TotalSeconds));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void Rejects_samples_outside_zero_to_one(double sample) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReconnectBackoff.GetDelay(0, sample));

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}

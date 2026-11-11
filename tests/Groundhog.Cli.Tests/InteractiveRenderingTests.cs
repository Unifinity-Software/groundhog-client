using Groundhog.Cli.Interactive;

namespace Groundhog.Cli.Tests;

public sealed class InteractiveRenderingTests
{
    [Fact]
    public async Task A_navigation_frame_is_written_once_before_waiting_for_input()
    {
        using var console = Harness.Console(120, 36);
        var keys = new InspectKeys(() =>
        {
            Assert.Equal(1, console.Output.Split("◆ GROUNDHOG", StringSplitOptions.None).Length - 1);
            return new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false);
        });
        var app = new InteractiveApp(console, keys, Harness.Clock(), new FakeServices(),
            TestContext.Current.CancellationToken, new AppOptions(Boot: false, AlternateScreen: false));

        Assert.Equal(0, await app.RunAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Idle_input_is_read_within_one_ten_millisecond_poll(bool boot)
    {
        using var console = Harness.Console(80, 24);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = Harness.Clock();
        var keys = new WaitingKeys();
        var app = new InteractiveApp(console, keys, clock, new FakeServices(), stop.Token,
            new AppOptions(Boot: boot, AlternateScreen: false));
        var frames = new List<string>();
        app.Drawn = (screen, _) => frames.Add(screen);
        var running = app.RunAsync();
        try
        {
            // Synchronously completed loads are already in the first frame and must not cause another redraw.
            Assert.Single(frames);
            Assert.False(running.IsCompleted);
            keys.Ready = true;
            clock.Advance(TimeSpan.FromMilliseconds(10));
            Assert.True(running.IsCompletedSuccessfully);
            Assert.Equal(0, await running);
        }
        finally
        {
            stop.Cancel();
            try { await running; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task An_invalidation_during_rendering_is_drawn_before_waiting()
    {
        using var console = Harness.Console(80, 24);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = Harness.Clock();
        var keys = new WaitingKeys();
        var app = new InteractiveApp(console, keys, clock, new FakeServices(), stop.Token,
            new AppOptions(Boot: false, AlternateScreen: false));
        var frames = 0;
        app.Drawn = (_, _) =>
        {
            if (++frames == 1) { app.Invalidate(); }
        };
        var running = app.RunAsync();
        try
        {
            Assert.Equal(2, frames);
            keys.Ready = true;
            clock.Advance(TimeSpan.FromMilliseconds(10));
            Assert.True(running.IsCompletedSuccessfully);
            await running;
        }
        finally
        {
            stop.Cancel();
            try { await running; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private sealed class WaitingKeys : IKeySource
    {
        internal bool Ready { get; set; }
        public bool KeyAvailable => Ready;
        public ConsoleKeyInfo ReadKey() => new('q', ConsoleKey.Q, false, false, false);
    }

    private sealed class InspectKeys(Func<ConsoleKeyInfo> read) : IKeySource
    {
        public bool KeyAvailable => true;
        public ConsoleKeyInfo ReadKey() => read();
    }
}

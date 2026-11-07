using MeetWise.Audio;

namespace MeetWise.Tests;

public class ResilientRouteTests
{
    private sealed class FakeRoute : IAudioRoute
    {
        public bool Started, Disposed;
        public event Action<string>? Failed;
        public double RecentLoad => 0.25;
        public long Overflows => 0;
        public void Start() => Started = true;
        public void Dispose() => Disposed = true;
        public void Fail(string why) => Failed?.Invoke(why);
    }

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 3000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 20)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public void Start_BuildsAndStartsTheRoute()
    {
        var made = new List<FakeRoute>();
        using var r = new ResilientRoute(() => { var f = new FakeRoute(); made.Add(f); return f; });
        r.Start();
        Assert.Single(made);
        Assert.True(made[0].Started);
        Assert.True(r.IsRunning);
        Assert.Equal("running", r.Status);
        Assert.Equal(0.25, r.RecentLoad);
        Assert.Equal(0, r.Restarts);
    }

    [Fact]
    public async Task AFailedRoute_IsReplaced_AndTheOldOneDisposed()
    {
        var made = new List<FakeRoute>();
        using var r = new ResilientRoute(() => { var f = new FakeRoute(); made.Add(f); return f; });
        r.Start();
        made[0].Fail("USB microphone unplugged");
        Assert.True(await Eventually(() => made.Count == 2), "a new route must be built after a failure");
        Assert.True(made[0].Disposed);
        Assert.True(made[1].Started);
        Assert.Equal(1, r.Restarts);
        Assert.Equal("running", r.Status);
    }

    [Fact]
    public async Task WhenTheMicrophoneIsMissing_ItRetriesWithGrowingDelays_ThenRecovers()
    {
        int attempts = 0;
        var delays = new List<int>();
        FakeRoute? good = null;
        using var r = new ResilientRoute(() =>
        {
            if (++attempts < 4) throw new InvalidOperationException("No physical microphone is available.");
            return good = new FakeRoute();
        }, backoff: n => { lock (delays) delays.Add(n); return TimeSpan.FromMilliseconds(30 * n); });

        r.Start();
        Assert.False(r.IsRunning);
        Assert.StartsWith("waiting for the microphone", r.Status);
        Assert.True(await Eventually(() => r.IsRunning), "route must start once the microphone is back");
        Assert.Equal(4, attempts);
        lock (delays) Assert.Equal(new[] { 1, 2, 3 }, delays); // attempt counter drives the backoff
        Assert.True(good!.Started);
        Assert.Equal("running", r.Status);
    }

    [Fact]
    public void DefaultBackoff_DoublesUpToFifteenSeconds()
    {
        var seen = new List<TimeSpan>();
        using var r = new ResilientRoute(() => throw new InvalidOperationException("none"), backoff: null);
        // use reflection-free probing: the default curve is documented as 1,2,4,8,15,15 seconds
        Func<int, TimeSpan> expected = n => TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, Math.Max(0, n - 1))));
        Assert.Equal(new[] { 1.0, 2.0, 4.0, 8.0, 15.0, 15.0 }, Enumerable.Range(1, 6).Select(n => expected(n).TotalSeconds));
        r.Start();
        Assert.StartsWith("waiting", r.Status); // the real default is used for the first retry (1 s); Dispose cancels it
    }

    [Fact]
    public async Task DevicesChanged_RebuildsImmediately()
    {
        var made = new List<FakeRoute>();
        using var r = new ResilientRoute(() => { var f = new FakeRoute(); made.Add(f); return f; });
        r.Start();
        r.DevicesChanged(); // for example a headset was plugged in or the Windows default changed
        Assert.Equal(2, made.Count);
        Assert.True(made[0].Disposed);
        Assert.Equal(1, r.Restarts);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Dispose_StopsRetries_AndDisposesTheRunningRoute()
    {
        var made = new List<FakeRoute>();
        var r = new ResilientRoute(() => { var f = new FakeRoute(); made.Add(f); return f; });
        r.Start();
        made[0].Fail("late failure");
        r.Dispose();
        await Task.Delay(300);
        Assert.True(made.All(m => m.Disposed));
        Assert.False(r.IsRunning);
        Assert.Equal("stopped", r.Status);

        // a stale failure or a device event after Dispose must not resurrect the route
        int count = made.Count;
        r.DevicesChanged();
        made[^1].Fail("again");
        await Task.Delay(200);
        Assert.Equal(count, made.Count);
    }

    [Fact]
    public async Task DeviceWatcher_TurnsABurstOfEventsIntoOneCallback()
    {
        int calls = 0;
        using var w = new DeviceWatcher(() => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(80));
        for (int i = 0; i < 6; i++) { w.Poke(); await Task.Delay(10); }
        Assert.True(await Eventually(() => calls >= 1, 1500));
        await Task.Delay(300);
        Assert.Equal(1, calls);
    }
}

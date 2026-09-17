namespace Inventoryzing.Agent.Tests;

public sealed class BrotherBpacStaDispatcherTests
{
    [Fact]
    public async Task Work_executes_serially_on_one_sta_thread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var dispatcher = new BrotherBpacStaDispatcher();
        List<int> order = [];
        var first = dispatcher.InvokeAsync(() =>
        {
            order.Add(1);
            return ThreadSnapshot();
        });
        var second = dispatcher.InvokeAsync(() =>
        {
            order.Add(2);
            return ThreadSnapshot();
        });

        var firstThread = await first;
        var secondThread = await second;

        Assert.Equal([1, 2], order);
        Assert.Equal(firstThread.ManagedThreadId, secondThread.ManagedThreadId);
        Assert.Equal(ApartmentState.STA, firstThread.ApartmentState);
        Assert.Equal(ApartmentState.STA, secondThread.ApartmentState);
    }

    [Fact]
    public async Task Cancelled_queued_work_is_not_executed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var dispatcher = new BrotherBpacStaDispatcher();
        using var release = new ManualResetEventSlim();
        var first = dispatcher.InvokeAsync(() =>
        {
            release.Wait();
            return true;
        });
        using var cancellation = new CancellationTokenSource();
        var executed = false;
        var second = dispatcher.InvokeAsync(
            () =>
            {
                executed = true;
                return true;
            },
            cancellation.Token);

        cancellation.Cancel();
        release.Set();
        Assert.True(await first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AsTask());
        Assert.False(executed);
    }

    private static ThreadState ThreadSnapshot() =>
        new(Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState());

    private sealed record ThreadState(int ManagedThreadId, ApartmentState ApartmentState);
}
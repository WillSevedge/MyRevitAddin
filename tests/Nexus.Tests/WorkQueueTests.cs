using Nexus.Agent;
using Nexus.Contracts;

namespace Nexus.Tests;

public class WorkQueueTests
{
    [Fact]
    public async Task Abandoned_work_never_runs()
    {
        var log = new AgentLog("test-queue");
        var queue = new WorkQueue(() => { }, log); // host never drains on its own
        bool ran = false;

        var ex = await Assert.ThrowsAsync<AgentException>(() =>
            queue.InvokeAsync(() => ran = true, TimeSpan.FromMilliseconds(100), default));
        Assert.Equal(ErrorCodes.HostBusy, ex.Code);

        queue.Drain(TimeSpan.FromSeconds(1)); // host becomes idle later
        Assert.False(ran);
    }

    [Fact]
    public async Task Exceptions_flow_back_to_the_caller()
    {
        var log = new AgentLog("test-queue");
        WorkQueue? queue = null;
        queue = new WorkQueue(() => ThreadPool.QueueUserWorkItem(_ => queue!.Drain(TimeSpan.FromSeconds(1))), log);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.InvokeAsync<int>(() => throw new InvalidOperationException("x"), TimeSpan.FromSeconds(5), default));
    }
}

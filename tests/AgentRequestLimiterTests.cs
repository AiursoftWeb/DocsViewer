using Aiursoft.DocsViewer.Services.Agents;

namespace Aiursoft.DocsViewer.Tests;

[TestClass]
public sealed class AgentRequestLimiterTests
{
    [TestMethod]
    public async Task AllowsOneInFlightAndReleasesLease()
    {
        var limiter = new AgentRequestLimiter();
        using var first = await limiter.TryAcquireAsync("user");
        Assert.IsNotNull(first);
        Assert.IsNull(await limiter.TryAcquireAsync("user"));
        first.Dispose();
        using var second = await limiter.TryAcquireAsync("user");
        Assert.IsNotNull(second);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public async Task DistinguishesRejectionsAndExpiresIdleUsers()
    {
        var clock = new Clock();
        var limiter = new AgentRequestLimiter(clock);
        using var active = (await limiter.AcquireAsync("user")).Lease;
        Assert.AreEqual(AgentLimitStatus.ConcurrencyLimited, (await limiter.AcquireAsync("user")).Status);
        active!.Dispose();
        for (var i = 0; i < 2; i++) (await limiter.AcquireAsync("user")).Lease!.Dispose();
        Assert.AreEqual(AgentLimitStatus.RateLimited, (await limiter.AcquireAsync("user")).Status);
        clock.Now = clock.Now.AddMinutes(2);
        using var fresh = (await limiter.AcquireAsync("user")).Lease;
        Assert.IsNotNull(fresh);
    }

    [TestMethod]
    public async Task LimitsThreeStartsPerMinute()
    {
        var limiter = new AgentRequestLimiter();
        for (var i = 0; i < 3; i++)
        {
            using var lease = await limiter.TryAcquireAsync("user");
            Assert.IsNotNull(lease);
        }
        Assert.IsNull(await limiter.TryAcquireAsync("user"));
    }
}

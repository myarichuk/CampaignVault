using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

public class TimeObserverTests
{
    private sealed class Recorder(List<TimeAdvance> seen, bool throws = false) : IWorldTimeObserver
    {
        public Task OnTimeAdvancedAsync(TimeAdvance advance, IChangeContext context, CancellationToken ct = default)
        {
            seen.Add(advance);
            if (throws)
                throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }
    }

    private static (WorldChangeDispatcher dispatcher, ChangeContext ctx) Build(params IWorldTimeObserver[] observers)
    {
        var dispatcher = new WorldChangeDispatcher(
            [new TimeAdvancedChangeHandler()], new CampaignDocumentKeys(), NullLogger<WorldChangeDispatcher>.Instance,
            timeObservers: observers);
        var ctx = new ChangeContext(
            null, [], [], [], [], [], NullLogger.Instance, [], dispatcher);
        return (dispatcher, ctx);
    }

    private static TimeAdvancedChange Span(double hours, double bucket = 6, string source = "travel") => new()
    {
        Source = source, Hours = hours, BucketHours = bucket, TotalHoursAfter = 100 + hours,
        CharacterIds = ["chars/a", "chars/b"], LocationId = "locations/x", Terrain = "mountains"
    };

    [Fact]
    public async Task TwelveHourTrip_FiresTwoSixHourSteps_WithCumulativeClock()
    {
        var seen = new List<TimeAdvance>();
        var (d, ctx) = Build(new Recorder(seen));
        await d.NotifyTimeObserversAsync(Span(12), ctx, TestContext.Current.CancellationToken);

        Assert.Equal(2, seen.Count);
        Assert.Equal([6.0, 6.0], seen.Select(s => s.Hours));
        Assert.Equal([106.0, 112.0], seen.Select(s => s.TotalHoursSoFar));
        Assert.All(seen, s => Assert.Equal("mountains", s.Terrain));
        Assert.Equal(["chars/a", "chars/b"], seen[0].CharacterIds);
    }

    [Fact]
    public async Task PartialLastBucket_IsShorter()
    {
        var seen = new List<TimeAdvance>();
        var (d, ctx) = Build(new Recorder(seen));
        await d.NotifyTimeObserversAsync(Span(9, bucket: 4, source: "rest"), ctx, TestContext.Current.CancellationToken);

        Assert.Equal([4.0, 4.0, 1.0], seen.Select(s => s.Hours));
        Assert.Equal(109.0, seen[^1].TotalHoursSoFar);
    }

    [Fact]
    public async Task ThrowingObserver_IsIsolated_OthersStillRun()
    {
        var bad = new List<TimeAdvance>();
        var good = new List<TimeAdvance>();
        var (d, ctx) = Build(new Recorder(bad, throws: true), new Recorder(good));
        await d.NotifyTimeObserversAsync(Span(6), ctx, TestContext.Current.CancellationToken);

        Assert.Single(bad);
        Assert.Single(good);
    }

    [Fact]
    public async Task LongSkip_IsCappedToMaxSteps()
    {
        var seen = new List<TimeAdvance>();
        var (d, ctx) = Build(new Recorder(seen));
        await d.NotifyTimeObserversAsync(Span(24 * 365, source: "advance_world"), ctx, TestContext.Current.CancellationToken);

        Assert.Equal(WorldChangeDispatcher.MaxTimeSteps, seen.Count);
        Assert.Equal(24 * 365, seen.Sum(s => s.Hours), 3);
    }

    [Fact]
    public async Task ObserverThatTriggersTimeAgain_DoesNotRecurse()
    {
        var seen = new List<TimeAdvance>();
        WorldChangeDispatcher? dispatcher = null;
        ChangeContext? context = null;
        var nested = new NestingObserver(seen, () => dispatcher!.NotifyTimeObserversAsync(Span(6), context!));
        var (d, ctx) = Build(nested);
        dispatcher = d;
        context = ctx;
        await d.NotifyTimeObserversAsync(Span(6), ctx, TestContext.Current.CancellationToken);

        Assert.Single(seen);
    }

    private sealed class NestingObserver(List<TimeAdvance> seen, Func<Task> again) : IWorldTimeObserver
    {
        public async Task OnTimeAdvancedAsync(TimeAdvance advance, IChangeContext context, CancellationToken ct = default)
        {
            seen.Add(advance);
            await again();
        }
    }

    [Fact]
    public async Task NoObservers_IsANoOp()
    {
        var (d, ctx) = Build();
        await d.NotifyTimeObserversAsync(Span(6), ctx, TestContext.Current.CancellationToken);
    }
}

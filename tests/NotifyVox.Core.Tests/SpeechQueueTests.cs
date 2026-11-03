using Microsoft.Extensions.Logging.Abstractions;
using NotifyVox.Core.Models;
using NotifyVox.Core.Queueing;

namespace NotifyVox.Core.Tests;

public sealed class SpeechQueueTests
{
    private static SpeechRequest MakeRequest(string text, Priority priority = Priority.Normal)
    {
        var ev = new NotificationEvent("App", "App", text, text, DateTimeOffset.UtcNow);
        return new SpeechRequest(ev, text, 3, priority);
    }

    [Fact]
    public async Task Enqueue_HighPriority_JumpsAheadOfNormalAndSignalsChunkInterrupt()
    {
        using var queue = new SpeechQueue(NullLogger<SpeechQueue>.Instance, maxCapacity: 10);

        using var activeScope = queue.BeginActiveSpeech(Priority.Normal, TestContext.Current.CancellationToken);
        Assert.False(activeScope.InterruptAtChunkToken.IsCancellationRequested);

        queue.Enqueue(MakeRequest("Normal-1", Priority.Normal));
        queue.Enqueue(MakeRequest("Normal-2", Priority.Normal));
        queue.Enqueue(MakeRequest("High-1", Priority.High));
        queue.Enqueue(MakeRequest("High-2", Priority.High));

        // 通常優先度発話中に高優先度が来るとチャンク境界割込みトークンが発火する (SPEC F14)
        Assert.True(activeScope.InterruptAtChunkToken.IsCancellationRequested);
        Assert.False(activeScope.AbortToken.IsCancellationRequested);

        var r1 = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        var r2 = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        var r3 = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        var r4 = await queue.DequeueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["High-1", "High-2", "Normal-1", "Normal-2"], [r1.Text, r2.Text, r3.Text, r4.Text]);
    }

    [Fact]
    public async Task Enqueue_ExceedingCapacity_DropsOldestNormalItem()
    {
        using var queue = new SpeechQueue(NullLogger<SpeechQueue>.Instance, maxCapacity: 3);

        queue.Enqueue(MakeRequest("High-Keep", Priority.High));
        queue.Enqueue(MakeRequest("Normal-Oldest", Priority.Normal));
        queue.Enqueue(MakeRequest("Normal-Newer", Priority.Normal));

        // 4件目投入で Normal-Oldest が破棄される (SPEC F15)
        queue.Enqueue(MakeRequest("Normal-Latest", Priority.Normal));
        Assert.Equal(3, queue.Count);

        var r1 = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        var r2 = await queue.DequeueAsync(TestContext.Current.CancellationToken);
        var r3 = await queue.DequeueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["High-Keep", "Normal-Newer", "Normal-Latest"], [r1.Text, r2.Text, r3.Text]);
    }

    [Fact]
    public void ClearAll_EmptiesQueueAndCancelsActiveAbortToken()
    {
        using var queue = new SpeechQueue(NullLogger<SpeechQueue>.Instance);
        using var scope = queue.BeginActiveSpeech(Priority.Normal, TestContext.Current.CancellationToken);

        queue.Enqueue(MakeRequest("Item-1"));
        queue.Enqueue(MakeRequest("Item-2"));

        var removed = queue.ClearAll();
        Assert.Equal(2, removed);
        Assert.Equal(0, queue.Count);
        Assert.True(scope.AbortToken.IsCancellationRequested);
    }
}

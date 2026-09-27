using WinAudioRoute.Events;

namespace WinAudioRoute.Tests;

/// <summary>
/// <see cref="SafeEventDispatcher{TEventArgs}"/> 的测试。
/// <para>
/// 核心契约：订阅者异常<b>绝不</b>向外传播（否则会穿过 COM 回调边界），
/// 且单个订阅者失败不影响其他订阅者。
/// </para>
/// </summary>
public class SafeEventDispatcherTests
{
    private sealed class SampleArgs : EventArgs
    {
        public int Value { get; init; }
    }

    [Fact]
    public void Raise_NoSubscribers_DoesNothing()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");

        dispatcher.Raise(null, new SampleArgs { Value = 1 });

        Assert.Equal(0, dispatcher.SubscriberCount);
    }

    [Fact]
    public void Raise_InvokesSubscriber()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");
        int received = 0;
        dispatcher.Add((_, args) => received = args.Value);

        dispatcher.Raise(null, new SampleArgs { Value = 42 });

        Assert.Equal(42, received);
    }

    [Fact]
    public void Add_Null_IsIgnored()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");

        dispatcher.Add(null);
        dispatcher.Remove(null);

        Assert.Equal(0, dispatcher.SubscriberCount);
    }

    [Fact]
    public void SubscriberCount_TracksAddRemove()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");
        EventHandler<SampleArgs> h1 = (_, _) => { };
        EventHandler<SampleArgs> h2 = (_, _) => { };

        dispatcher.Add(h1);
        dispatcher.Add(h2);
        Assert.Equal(2, dispatcher.SubscriberCount);

        dispatcher.Remove(h1);
        Assert.Equal(1, dispatcher.SubscriberCount);

        dispatcher.Remove(h2);
        Assert.Equal(0, dispatcher.SubscriberCount);
    }

    [Fact]
    public void SubscriberCount_CountsDuplicateSubscriptions()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");
        EventHandler<SampleArgs> handler = (_, _) => { };

        dispatcher.Add(handler);
        dispatcher.Add(handler);

        Assert.Equal(2, dispatcher.SubscriberCount);
    }

    [Fact]
    public void Raise_SubscriberThrows_DoesNotPropagate()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");
        dispatcher.Add((_, _) => throw new InvalidOperationException("subscriber failure"));

        // 不抛异常 = 通过（这是 COM 回调安全的关键契约）
        dispatcher.Raise(null, new SampleArgs());
    }

    [Fact]
    public void Raise_SubscriberThrows_ReportsFault()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");
        var faults = new List<(string Source, Exception Error)>();
        dispatcher.HandlerFaulted += (source, error) => faults.Add((source, error));

        dispatcher.Add((_, _) => throw new InvalidOperationException("boom"));

        dispatcher.Raise(null, new SampleArgs());

        Assert.Single(faults);
        Assert.Contains("Test", faults[0].Source, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(faults[0].Error);
    }

    [Fact]
    public void Raise_OneSubscriberThrows_OthersStillRun()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");
        var invoked = new List<string>();

        dispatcher.Add((_, _) => invoked.Add("first"));
        dispatcher.Add((_, _) => throw new InvalidOperationException("boom"));
        dispatcher.Add((_, _) => invoked.Add("third"));

        dispatcher.Raise(null, new SampleArgs());

        Assert.Equal(["first", "third"], invoked);
    }

    [Fact]
    public void Raise_FaultHandlerThrows_IsSwallowed()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");
        dispatcher.HandlerFaulted += (_, _) => throw new InvalidOperationException("fault handler failure");
        dispatcher.Add((_, _) => throw new InvalidOperationException("boom"));

        dispatcher.Raise(null, new SampleArgs());
    }

    [Fact]
    public void RaiseRegistrationFailure_InvokesSubscribers()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");
        var codes = new List<int>();
        dispatcher.RegistrationFailed += codes.Add;

        dispatcher.RaiseRegistrationFailure(unchecked((int)0x80070005u));

        Assert.Equal([unchecked((int)0x80070005u)], codes);
    }

    [Fact]
    public void RaiseRegistrationFailure_SubscriberThrows_IsSwallowed()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");
        dispatcher.RegistrationFailed += _ => throw new InvalidOperationException("boom");

        dispatcher.RaiseRegistrationFailure(1);
    }

    [Fact]
    public void Raise_ConcurrentAddAndRaise_DoesNotThrow()
    {
        var dispatcher = new SafeEventDispatcher<SampleArgs>("Test");

        Parallel.For(0, 200, i =>
        {
            EventHandler<SampleArgs> handler = (_, _) => { };
            dispatcher.Add(handler);
            dispatcher.Raise(null, new SampleArgs { Value = i });
            dispatcher.Remove(handler);
        });

        // 只要没有抛异常（尤其没有"集合已修改"）即通过
        Assert.True(dispatcher.SubscriberCount >= 0);
    }
}

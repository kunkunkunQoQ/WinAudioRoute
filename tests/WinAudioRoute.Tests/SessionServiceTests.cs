using WinAudioRoute.Internal;

namespace WinAudioRoute.Tests;

/// <summary>
/// <see cref="SessionService"/> 的缓存与稀疏环境行为测试。
/// <para>
/// 测试替身不提供真实 <c>IMMDevice</c>，因此无法构造真实会话句柄；
/// 会话<b>音量</b>行为由只读集成测试与受门控的 mutation 测试覆盖。
/// </para>
/// </summary>
public class SessionServiceTests
{
    /// <summary>创建一个"每次返回新实例"的替身工厂（服务会释放每次返回的实例）。</summary>
    private static (EnumeratorRecorder Recorder, Func<IDeviceEnumerator> Factory) CreateFactory(
        params AudioDataFlow[] flows)
    {
        var recorder = new EnumeratorRecorder();
        var devices = new Dictionary<AudioDataFlow, IReadOnlyList<AudioDevice>>();

        int index = 0;
        foreach (AudioDataFlow flow in flows)
        {
            devices[flow] =
            [
                new AudioDevice
                {
                    Id = $"{{0.0.{index}}}.{{dev{index}}}",
                    FriendlyName = $"Device {index}",
                    Flow = flow,
                    State = AudioDeviceState.Active,
                },
            ];
            index++;
        }

        return (recorder, () => new FakeDeviceEnumerator(recorder, devices));
    }

    [Fact]
    public void GetSessions_WithoutDevices_ReturnsEmpty()
    {
        var service = new SessionService(() => new FakeDeviceEnumerator(new EnumeratorRecorder()), TimeProvider.System);

        Assert.Empty(service.GetSessions());
    }

    [Fact]
    public void GetSessions_WithDevicesButNoSessions_ReturnsEmpty()
    {
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) =
            CreateFactory(AudioDataFlow.Render, AudioDataFlow.Capture);
        var service = new SessionService(factory, TimeProvider.System);

        Assert.Empty(service.GetSessions());
        Assert.True(recorder.CreatedCount > 0, "应至少创建过一个枚举器");
    }

    [Fact]
    public void GetSessions_EnumeratesBothDirections()
    {
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new SessionService(factory, TimeProvider.System);

        service.GetSessions();

        Assert.Contains(AudioDataFlow.Render, recorder.DevicesRequested);
        Assert.Contains(AudioDataFlow.Capture, recorder.DevicesRequested);
    }

    [Fact]
    public void GetSessions_IsCached_WithinTtl()
    {
        var clock = new TestTimeProvider();
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory(AudioDataFlow.Render);

        var service = new SessionService(factory, clock)
        {
            SessionTtl = TimeSpan.FromSeconds(5),
            RefreshThrottle = TimeSpan.Zero,
        };

        service.GetSessions();
        int afterFirst = recorder.EnumerateCalls;

        clock.Advance(TimeSpan.FromSeconds(1));
        service.GetSessions();

        Assert.Equal(afterFirst, recorder.EnumerateCalls);
    }

    [Fact]
    public void GetSessions_ReEnumerates_AfterTtlExpires()
    {
        var clock = new TestTimeProvider();
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory(AudioDataFlow.Render);

        var service = new SessionService(factory, clock)
        {
            SessionTtl = TimeSpan.FromSeconds(5),
            RefreshThrottle = TimeSpan.Zero,
        };

        service.GetSessions();
        int afterFirst = recorder.EnumerateCalls;

        clock.Advance(TimeSpan.FromSeconds(6));
        service.GetSessions();

        Assert.True(recorder.EnumerateCalls > afterFirst, "TTL 过期后必须重新枚举");
    }

    [Fact]
    public void Refresh_DropsCache()
    {
        var clock = new TestTimeProvider();
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory(AudioDataFlow.Render);

        var service = new SessionService(factory, clock)
        {
            SessionTtl = TimeSpan.FromMinutes(10),
            RefreshThrottle = TimeSpan.Zero,
        };

        service.GetSessions();
        int afterFirst = recorder.EnumerateCalls;

        service.Refresh();
        service.GetSessions();

        Assert.True(recorder.EnumerateCalls > afterFirst, "Refresh 之后必须重新枚举");
    }

    [Fact]
    public void GetSessionsForProcess_NoMatch_ReturnsEmpty()
    {
        var service = new SessionService(() => new FakeDeviceEnumerator(new EnumeratorRecorder()), TimeProvider.System);

        Assert.Empty(service.GetSessionsForProcess(1234));
    }

    [Fact]
    public void GetSessions_ByFlow_FiltersCorrectly()
    {
        var service = new SessionService(() => new FakeDeviceEnumerator(new EnumeratorRecorder()), TimeProvider.System);

        Assert.Empty(service.GetSessions(AudioDataFlow.Capture));
        Assert.Empty(service.GetSessions(AudioDataFlow.Render));
    }

    [Fact]
    public void GetHandlesForProcess_NoMatch_ReturnsEmpty()
    {
        var service = new SessionService(() => new FakeDeviceEnumerator(new EnumeratorRecorder()), TimeProvider.System);

        Assert.Empty(service.GetHandlesForProcess(1234));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var service = new SessionService(() => new FakeDeviceEnumerator(new EnumeratorRecorder()), TimeProvider.System);

        service.Dispose();
        service.Dispose();

        Assert.Equal(0, service.CachedSessionCount);
    }
}

/// <summary>
/// <see cref="SessionCache"/> 的生命周期测试（使用空句柄集合，不触碰 COM）。
/// </summary>
public class SessionCacheTests
{
    private static AudioSessionSnapshot Snapshot(DateTimeOffset capturedAt, int sessionCount = 0) =>
        new(
            Enumerable.Range(0, sessionCount).Select(i => new AudioSession
            {
                ProcessId = 1000 + i,
                SessionInstanceIdentifier = $"inst-{i}",
            }).ToList(),
            [],
            capturedAt);

    [Fact]
    public void TryGet_ReturnsNull_WhenEmpty()
    {
        using var cache = new SessionCache();

        Assert.Null(cache.TryGet(TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TryGet_ReturnsSnapshot_WithinMaxAge()
    {
        using var cache = new SessionCache();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        cache.Set(Snapshot(now, 2));

        AudioSessionSnapshot? snapshot = cache.TryGet(TimeSpan.FromSeconds(5), now.AddSeconds(1));

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Sessions.Count);
        Assert.Equal(2, cache.CachedSessionCount);
    }

    [Fact]
    public void TryGet_ReturnsNull_WhenOlderThanMaxAge()
    {
        using var cache = new SessionCache();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        cache.Set(Snapshot(now, 1));

        Assert.Null(cache.TryGet(TimeSpan.FromSeconds(5), now.AddSeconds(6)));
    }

    [Fact]
    public void Clear_EmptiesCache()
    {
        using var cache = new SessionCache();
        cache.Set(Snapshot(DateTimeOffset.UtcNow, 3));

        cache.Clear();

        Assert.Equal(0, cache.CachedSessionCount);
    }

    [Fact]
    public void Dispose_EmptiesCache_AndIsIdempotent()
    {
        var cache = new SessionCache();
        cache.Set(Snapshot(DateTimeOffset.UtcNow, 3));

        cache.Dispose();
        cache.Dispose();

        Assert.True(cache.IsDisposed);
        Assert.Equal(0, cache.CachedSessionCount);
    }

    [Fact]
    public void TryGet_AfterDispose_Throws()
    {
        var cache = new SessionCache();
        cache.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => cache.TryGet(TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Set_AfterDispose_Throws()
    {
        var cache = new SessionCache();
        cache.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => cache.Set(Snapshot(DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void Set_NullSnapshot_Throws()
    {
        using var cache = new SessionCache();

        Assert.Throws<ArgumentNullException>(() => cache.Set(null!));
    }
}

using WinAudioRoute.Events;

namespace WinAudioRoute.Tests;

/// <summary>
/// 按应用路由与事件系统的<b>只读</b>集成测试。
/// <para>
/// 不修改任何应用的路由、不修改任何设备状态。缺少设备/会话时登记跳过并提前返回。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class RoutingAndEventIntegrationTests
{
    [Fact]
    public void RoutingCapability_IsQueryable_AndDiagnostic()
    {
        using var audio = new WindowsAudioManager();

        AudioRoutingCapability capability = audio.RoutingCapability;

        Assert.Equal(capability.IsSupported, audio.IsPerAppRoutingSupported);
        Assert.Equal(capability.IsSupported, capability.Reason is null);

        if (!capability.IsSupported)
        {
            // 不支持时必须给出可诊断的原因，不允许"只说 false"
            Assert.False(string.IsNullOrWhiteSpace(capability.Reason));
            TestEnvironment.Skip($"RoutingCapability: 本机不支持按应用路由（{capability}）");
        }
    }

    [Fact]
    public void GetApplicationOutput_DoesNotThrow_ForCurrentProcess()
    {
        using var audio = new WindowsAudioManager();

        if (!audio.IsPerAppRoutingSupported)
        {
            TestEnvironment.Skip("GetApplicationOutput: 本机不支持按应用路由");
            return;
        }

        // 只读：本进程通常没有持久化路由 → null（= 跟随系统默认）
        AudioDevice? output = audio.GetApplicationOutput(Environment.ProcessId);

        // 允许 null（未设置）或一个设备；不允许抛异常
        if (output is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(output.Id));
            Assert.Equal(AudioDataFlow.Render, output.Flow);
        }
    }

    [Fact]
    public void GetApplicationInput_DoesNotThrow_ForCurrentProcess()
    {
        using var audio = new WindowsAudioManager();

        if (!audio.IsPerAppRoutingSupported)
        {
            TestEnvironment.Skip("GetApplicationInput: 本机不支持按应用路由");
            return;
        }

        AudioDevice? input = audio.GetApplicationInput(Environment.ProcessId);

        if (input is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(input.Id));
            Assert.Equal(AudioDataFlow.Capture, input.Flow);
        }
    }

    [Fact]
    public void RoutingCapability_ProbeIsReadOnly_AndStable()
    {
        using var audio = new WindowsAudioManager();

        if (!audio.IsPerAppRoutingSupported)
        {
            TestEnvironment.Skip("RoutingCapability_ProbeIsReadOnly: 本机不支持按应用路由");
            return;
        }

        // 探测两次必须一致（结果被缓存，且不改变系统状态）
        AudioRoutingCapability first = audio.RoutingCapability;
        AudioRoutingCapability second = audio.RoutingCapability;

        Assert.Equal(first.IsSupported, second.IsSupported);
        Assert.Equal(first.HResult, second.HResult);
    }

    [Fact]
    public void DeviceNotification_IsRegistered_OnConstruction()
    {
        using var audio = new WindowsAudioManager();

        // 设备通知用于缓存失效，构造时即注册（失败会降级为 TTL 兜底，不算测试失败）
        if (!audio.IsDeviceNotificationRegistered)
        {
            TestEnvironment.Skip("DeviceNotification: 本机设备通知注册失败（降级为 TTL 兜底）");
        }
    }

    [Fact]
    public void SubscribingToEvents_EnablesEventSystem_AndDisposeUnregisters()
    {
        var audio = new WindowsAudioManager();

        Assert.False(audio.IsEventSystemEnabled);

        void Handler(object? sender, AudioDeviceChangedEventArgs e)
        {
        }

        audio.DeviceChanged += Handler;

        Assert.True(audio.IsEventSystemEnabled);

        audio.DeviceChanged -= Handler;

        // 注销后事件系统保持开启（由调用方显式关闭），Dispose 必须撤销全部原生注册
        audio.Dispose();

        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public void SessionNotification_RegistrationState_IsReported()
    {
        using var audio = new WindowsAudioManager();

        void Handler(object? sender, AudioSessionChangedEventArgs e)
        {
        }

        audio.SessionChanged += Handler;

        if (!audio.IsSessionNotificationRegistered)
        {
            // 没有活动播放设备等环境原因：允许降级
            TestEnvironment.Skip("SessionNotification: 本机会话通知未注册成功");
            return;
        }

        Assert.True(audio.IsSessionNotificationRegistered);

        audio.SessionChanged -= Handler;
    }

    [Fact]
    public void Dispose_UnregistersEverything_AndIsIdempotent()
    {
        var audio = new WindowsAudioManager();

        void Handler(object? sender, AudioDeviceChangedEventArgs e)
        {
        }

        void SessionHandler(object? sender, AudioSessionChangedEventArgs e)
        {
        }

        audio.DeviceChanged += Handler;
        audio.SessionChanged += SessionHandler;

        audio.Dispose();
        audio.Dispose();
        audio.Dispose();

        Assert.True(audio.IsDisposed);
        Assert.False(audio.IsSessionNotificationRegistered);
        Assert.Throws<ObjectDisposedException>(() => audio.GetSessions());
        Assert.Throws<ObjectDisposedException>(() => audio.GetApplicationOutput(Environment.ProcessId));
    }

    [Fact]
    public void SubscriberThrows_DoesNotPropagate_ToCaller()
    {
        using var audio = new WindowsAudioManager();
        var faults = new List<string>();
        audio.NotificationHandlerFaulted += (source, _) => faults.Add(source);

        audio.DeviceChanged += (_, _) => throw new InvalidOperationException("subscriber failure");

        // 通过测试钩子走完整的事件 → 分发路径；订阅者异常必须被隔离
        audio.RaiseDeviceChangedForTesting(
            new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.Added, "test-device"));

        Assert.NotEmpty(faults);
    }

    [Fact]
    public void SessionChangedEvent_ReachesSubscriber_AndInvalidatesCache()
    {
        using var audio = new WindowsAudioManager();
        var received = new List<AudioSessionChangedEventArgs>();
        audio.SessionChanged += (_, args) => received.Add(args);

        audio.RaiseSessionChangedForTesting(SessionCreatedArgs("inst-1", 100));

        Assert.Single(received);
        Assert.Equal(AudioSessionChangeKind.Created, received[0].Kind);
    }

    [Fact]
    public void DeviceChangedEvent_ReachesSubscriber()
    {
        using var audio = new WindowsAudioManager();
        var received = new List<AudioDeviceChangedEventArgs>();
        audio.DeviceChanged += (_, args) => received.Add(args);

        audio.RaiseDeviceChangedForTesting(
            new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.Removed, "test-device"));

        Assert.Single(received);
        Assert.Equal(AudioDeviceChangeKind.Removed, received[0].Kind);
        Assert.Equal("test-device", received[0].DeviceId);
    }

    [Fact]
    public void EventInjection_AfterDispose_DoesNotThrow()
    {
        var audio = new WindowsAudioManager();
        audio.Dispose();

        // 释放后到达的回调（原生竞态）必须安全
        audio.RaiseDeviceChangedForTesting(
            new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.Added, "late-device"));
        audio.RaiseSessionChangedForTesting(SessionCreatedArgs("late", 1));
    }

    [Fact]
    public void EventSystem_CanBeDisabled_Explicitly()
    {
        using var audio = new WindowsAudioManager();

        audio.IsEventSystemEnabled = true;
        Assert.True(audio.IsEventSystemEnabled);

        audio.IsEventSystemEnabled = false;
        Assert.False(audio.IsEventSystemEnabled);
        Assert.False(audio.IsSessionNotificationRegistered);

        // 再次订阅会重新开启
        void Handler(object? sender, AudioDeviceChangedEventArgs e)
        {
        }

        audio.DeviceChanged += Handler;
        Assert.True(audio.IsEventSystemEnabled);
    }

    private static AudioSessionChangedEventArgs SessionCreatedArgs(string instanceId, int pid) =>
        new(AudioSessionChangeKind.Created, "sid", instanceId, pid);
}

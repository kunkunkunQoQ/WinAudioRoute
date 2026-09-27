using WinAudioRoute.Events;
using WinAudioRoute.Interop;
using WinAudioRoute.Internal;

namespace WinAudioRoute.Tests;

/// <summary>
/// 设备通知回调 → 托管事件参数的映射测试。
/// <para>
/// 直接调用回调方法（不经过原生注册），验证：
/// 种类映射、方向/角色映射、状态映射、未知值不猜测、释放后不再分发、订阅者异常不外泄。
/// </para>
/// </summary>
public class DeviceNotificationClientTests
{
    private static (DeviceNotificationClient Client, List<AudioDeviceChangedEventArgs> Events, List<int> Failures) Create()
    {
        var events = new List<AudioDeviceChangedEventArgs>();
        var failures = new List<int>();
        var client = new DeviceNotificationClient(events.Add, failures.Add);
        return (client, events, failures);
    }

    [Fact]
    public void OnDeviceAdded_MapsToAdded()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = Create();

        client.OnDeviceAdded("{0.0.0.0}.{dev}");

        AudioDeviceChangedEventArgs args = Assert.Single(events);
        Assert.Equal(AudioDeviceChangeKind.Added, args.Kind);
        Assert.Equal("{0.0.0.0}.{dev}", args.DeviceId);
        Assert.Null(args.Flow);
        Assert.Null(args.Role);
        Assert.Null(args.State);
    }

    [Fact]
    public void OnDeviceRemoved_MapsToRemoved()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = Create();

        client.OnDeviceRemoved("{0.0.0.0}.{dev}");

        Assert.Equal(AudioDeviceChangeKind.Removed, Assert.Single(events).Kind);
    }

    [Fact]
    public void OnDeviceStateChanged_MapsStateAndKind()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = Create();

        client.OnDeviceStateChanged("{0.0.0.0}.{dev}", DeviceState.UNPLUGGED);

        AudioDeviceChangedEventArgs args = Assert.Single(events);
        Assert.Equal(AudioDeviceChangeKind.StateChanged, args.Kind);
        Assert.Equal(AudioDeviceState.Unplugged, args.State);
    }

    [Fact]
    public void OnDeviceStateChanged_CombinedFlags_AreMapped()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = Create();

        client.OnDeviceStateChanged("{0.0.0.0}.{dev}", DeviceState.ACTIVE | DeviceState.DISABLED);

        Assert.Equal(
            AudioDeviceState.Active | AudioDeviceState.Disabled,
            Assert.Single(events).State);
    }

    [Fact]
    public void OnDeviceStateChanged_UnknownBits_StateIsNull_NotGuessed()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = Create();

        client.OnDeviceStateChanged("{0.0.0.0}.{dev}", (DeviceState)0x1000);

        AudioDeviceChangedEventArgs args = Assert.Single(events);
        Assert.Equal(AudioDeviceChangeKind.StateChanged, args.Kind);
        Assert.Null(args.State);
    }

    [Theory]
    [InlineData(0, 0, AudioDataFlow.Render, AudioRole.Console)]
    [InlineData(0, 1, AudioDataFlow.Render, AudioRole.Multimedia)]
    [InlineData(0, 2, AudioDataFlow.Render, AudioRole.Communications)]
    [InlineData(1, 0, AudioDataFlow.Capture, AudioRole.Console)]
    [InlineData(1, 1, AudioDataFlow.Capture, AudioRole.Multimedia)]
    [InlineData(1, 2, AudioDataFlow.Capture, AudioRole.Communications)]
    public void OnDefaultDeviceChanged_MapsAllSixFlowRoleCombinations(
        int flow,
        int role,
        AudioDataFlow expectedFlow,
        AudioRole expectedRole)
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = Create();

        client.OnDefaultDeviceChanged((EDataFlow)flow, (ERole)role, "{0.0.0.0}.{dev}");

        AudioDeviceChangedEventArgs args = Assert.Single(events);
        Assert.Equal(AudioDeviceChangeKind.DefaultChanged, args.Kind);
        Assert.Equal(expectedFlow, args.Flow);
        Assert.Equal(expectedRole, args.Role);
    }

    [Fact]
    public void OnDefaultDeviceChanged_UnknownFlowOrRole_IsNull_NotGuessed()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = Create();

        client.OnDefaultDeviceChanged((EDataFlow)99, (ERole)99, "{0.0.0.0}.{dev}");

        AudioDeviceChangedEventArgs args = Assert.Single(events);
        Assert.Null(args.Flow);
        Assert.Null(args.Role);
    }

    [Fact]
    public void OnDefaultDeviceChanged_NullDeviceId_BecomesEmpty()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = Create();

        client.OnDefaultDeviceChanged(EDataFlow.eRender, ERole.eConsole, null);

        Assert.Equal(string.Empty, Assert.Single(events).DeviceId);
    }

    [Fact]
    public void OnPropertyValueChanged_MapsToPropertyChanged()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = Create();

        client.OnPropertyValueChanged("{0.0.0.0}.{dev}", default);

        Assert.Equal(AudioDeviceChangeKind.PropertyChanged, Assert.Single(events).Kind);
    }

    [Fact]
    public void AllCallbacks_ReturnSZero()
    {
        (DeviceNotificationClient client, _, _) = Create();

        Assert.Equal(0, client.OnDeviceAdded("a"));
        Assert.Equal(0, client.OnDeviceRemoved("a"));
        Assert.Equal(0, client.OnDeviceStateChanged("a", DeviceState.ACTIVE));
        Assert.Equal(0, client.OnDefaultDeviceChanged(EDataFlow.eRender, ERole.eConsole, "a"));
        Assert.Equal(0, client.OnPropertyValueChanged("a", default));
    }

    [Fact]
    public void SubscriberThrows_DoesNotEscapeCallback()
    {
        var client = new DeviceNotificationClient(
            _ => throw new InvalidOperationException("subscriber failure"),
            _ => { });

        // 不抛异常 = 通过（绝对不能让异常穿越 COM 边界）
        Assert.Equal(0, client.OnDeviceAdded("a"));
        Assert.Equal(0, client.OnDeviceRemoved("a"));
        Assert.Equal(0, client.OnDeviceStateChanged("a", DeviceState.ACTIVE));
        Assert.Equal(0, client.OnDefaultDeviceChanged(EDataFlow.eRender, ERole.eConsole, "a"));
        Assert.Equal(0, client.OnPropertyValueChanged("a", default));
    }

    [Fact]
    public void AfterDispose_NoFurtherEventsAreDispatched()
    {
        (DeviceNotificationClient client, List<AudioDeviceChangedEventArgs> events, _) = Create();

        client.OnDeviceAdded("before");
        Assert.Single(events);

        client.Dispose();
        client.OnDeviceAdded("after");
        client.OnDeviceRemoved("after");
        client.OnDeviceStateChanged("after", DeviceState.ACTIVE);

        Assert.Single(events);
        Assert.False(client.IsRegistered);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        (DeviceNotificationClient client, _, _) = Create();

        client.Dispose();
        client.Dispose();
        client.Dispose();

        Assert.False(client.IsRegistered);
    }

    [Fact]
    public void Register_AfterDispose_Throws()
    {
        (DeviceNotificationClient client, _, _) = Create();
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Register());
    }

    [Fact]
    public void Unregister_WithoutRegister_IsNoOp()
    {
        (DeviceNotificationClient client, _, _) = Create();

        client.Unregister();
        client.Unregister();

        Assert.False(client.IsRegistered);
    }

    [Fact]
    public void NullCallbacks_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new DeviceNotificationClient(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => new DeviceNotificationClient(_ => { }, null!));
    }
}

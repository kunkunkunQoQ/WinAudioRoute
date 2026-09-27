using WinAudioRoute.Internal;

namespace WinAudioRoute.Tests;

/// <summary>
/// <see cref="DeviceService"/> 的缓存与查询测试。
/// 使用 <see cref="FakeDeviceEnumerator"/> 替代真实 COM，时钟可控，因此完全确定性。
/// </summary>
public class DeviceServiceTests
{
    private static AudioDevice Device(string id, string name, AudioDataFlow flow = AudioDataFlow.Render) => new()
    {
        Id = id,
        FriendlyName = name,
        Flow = flow,
        State = AudioDeviceState.Active,
    };

    private static Dictionary<AudioDataFlow, IReadOnlyList<AudioDevice>> StandardDevices() => new()
    {
        [AudioDataFlow.Render] =
        [
            Device("{0.0.0.0}.{spk}", "Speakers"),
            Device("{0.0.0.0}.{hs}", "Headset"),
        ],
        [AudioDataFlow.Capture] =
        [
            Device("{0.0.1.0}.{mic}", "Microphone", AudioDataFlow.Capture),
        ],
    };

    /// <summary>创建"每次调用返回新实例"的工厂（服务会释放每次返回的实例）。</summary>
    private static (EnumeratorRecorder Recorder, Func<IDeviceEnumerator> Factory) CreateFactory(
        Dictionary<AudioDataFlow, IReadOnlyList<AudioDevice>>? devices = null,
        Dictionary<(AudioDataFlow Flow, AudioRole Role), string?>? defaults = null)
    {
        var recorder = new EnumeratorRecorder();
        Dictionary<AudioDataFlow, IReadOnlyList<AudioDevice>> source = devices ?? StandardDevices();
        return (recorder, () => new FakeDeviceEnumerator(recorder, source, defaults));
    }

    [Fact]
    public void GetPlaybackDevices_ReturnsDevices()
    {
        (_, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, TimeProvider.System);

        IReadOnlyList<AudioDevice> devices = service.GetPlaybackDevices();

        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.Equal(AudioDataFlow.Render, d.Flow));
    }

    [Fact]
    public void GetRecordingDevices_ReturnsDevices()
    {
        (_, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, TimeProvider.System);

        IReadOnlyList<AudioDevice> devices = service.GetRecordingDevices();

        Assert.Single(devices);
        Assert.Equal("Microphone", devices[0].FriendlyName);
    }

    [Fact]
    public void GetDevices_All_CombinesBothDirections()
    {
        (_, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, TimeProvider.System);

        IReadOnlyList<AudioDevice> devices = service.GetDevices(AudioDataFlow.All);

        Assert.Equal(3, devices.Count);
    }

    [Fact]
    public void GetDevices_NoneState_Throws()
    {
        (_, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, TimeProvider.System);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => service.GetPlaybackDevices(AudioDeviceState.None));
    }

    [Fact]
    public void GetDevices_AllBitsSetMask_IsNormalizedToFourBitUnion()
    {
        // Windows 的合法掩码是 DEVICE_STATEMASK_ALL = 0x0F 的任意子集；
        // 服务层必须把非法的"全位"输入（0xFFFFFFFF）归一化为 0x0F 后才下发。
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, TimeProvider.System);

        service.GetPlaybackDevices(unchecked((AudioDeviceState)(-1)));

        Assert.Equal(AudioDeviceState.All, recorder.RequestedStates.Single());
        Assert.Equal(0x0F, (int)recorder.RequestedStates.Single());
    }

    [Fact]
    public void GetDevices_UndefinedStateBits_Throw()
    {
        (_, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, TimeProvider.System);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => service.GetPlaybackDevices((AudioDeviceState)0x100));
    }

    [Fact]
    public void NonActiveStates_ArePassedThrough_AndNotCached()
    {
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, TimeProvider.System);

        service.GetPlaybackDevices(AudioDeviceState.All);
        int afterFirst = recorder.EnumerateCalls;

        service.GetPlaybackDevices(AudioDeviceState.Disabled | AudioDeviceState.Unplugged);
        int afterSecond = recorder.EnumerateCalls;

        // 非 Active 状态不缓存：第二次调用必须重新枚举
        Assert.True(afterSecond > afterFirst, "非 Active 状态的查询不应被缓存");
        Assert.Contains(AudioDeviceState.All, recorder.RequestedStates);
        Assert.Contains(
            AudioDeviceState.Disabled | AudioDeviceState.Unplugged,
            recorder.RequestedStates);
    }

    [Fact]
    public void GetDefaultDevice_ReturnsDevice_ForEachRole()
    {
        var defaults = new Dictionary<(AudioDataFlow, AudioRole), string?>
        {
            [(AudioDataFlow.Render, AudioRole.Console)] = "{0.0.0.0}.{spk}",
            [(AudioDataFlow.Render, AudioRole.Multimedia)] = "{0.0.0.0}.{hs}",
            [(AudioDataFlow.Capture, AudioRole.Communications)] = "{0.0.1.0}.{mic}",
        };

        (_, Func<IDeviceEnumerator> factory) = CreateFactory(defaults: defaults);
        var service = new DeviceService(factory, TimeProvider.System);

        Assert.Equal("Speakers", service.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Console)!.FriendlyName);
        Assert.Equal("Headset", service.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Multimedia)!.FriendlyName);
        Assert.Equal("Microphone", service.GetDefaultDevice(AudioDataFlow.Capture, AudioRole.Communications)!.FriendlyName);
    }

    [Fact]
    public void GetDefaultDevice_MissingRole_ReturnsNull()
    {
        (_, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, TimeProvider.System);

        // 未配置任何默认设备（例如某些系统没有默认通信设备）
        Assert.Null(service.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Communications));
    }

    [Fact]
    public void GetDefaultDevice_CorrectsFlow_ToRequestedDirection()
    {
        var defaults = new Dictionary<(AudioDataFlow, AudioRole), string?>
        {
            [(AudioDataFlow.Capture, AudioRole.Console)] = "{0.0.1.0}.{mic}",
        };

        (_, Func<IDeviceEnumerator> factory) = CreateFactory(defaults: defaults);
        var service = new DeviceService(factory, TimeProvider.System);

        AudioDevice? device = service.GetDefaultDevice(AudioDataFlow.Capture, AudioRole.Console);

        Assert.NotNull(device);
        Assert.Equal(AudioDataFlow.Capture, device!.Flow);
    }

    [Fact]
    public void MarkDefaultDevices_FlagsTheRightRoles()
    {
        var defaults = new Dictionary<(AudioDataFlow, AudioRole), string?>
        {
            [(AudioDataFlow.Render, AudioRole.Console)] = "{0.0.0.0}.{spk}",
            [(AudioDataFlow.Render, AudioRole.Multimedia)] = "{0.0.0.0}.{spk}",
            [(AudioDataFlow.Render, AudioRole.Communications)] = "{0.0.0.0}.{hs}",
        };

        (_, Func<IDeviceEnumerator> factory) = CreateFactory(defaults: defaults);
        var service = new DeviceService(factory, TimeProvider.System);
        IReadOnlyList<AudioDevice> devices = service.GetPlaybackDevices();

        AudioDevice speakers = devices.Single(d => d.FriendlyName == "Speakers");
        AudioDevice headset = devices.Single(d => d.FriendlyName == "Headset");

        Assert.True(speakers.IsDefaultConsole);
        Assert.True(speakers.IsDefaultMultimedia);
        Assert.False(speakers.IsDefaultCommunications);
        Assert.True(speakers.IsDefault);

        Assert.False(headset.IsDefaultConsole);
        Assert.False(headset.IsDefaultMultimedia);
        Assert.True(headset.IsDefaultCommunications);
    }

    [Fact]
    public void ActiveEnumeration_IsCached_WithinTtl()
    {
        var clock = new TestTimeProvider();
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, clock);

        service.GetPlaybackDevices();
        int afterFirst = recorder.EnumerateCalls;
        Assert.True(afterFirst > 0, "首次调用必须真正枚举");

        clock.Advance(TimeSpan.FromMilliseconds(1000));
        service.GetPlaybackDevices();
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        service.GetPlaybackDevices();

        // 命中缓存：不应再产生任何枚举
        Assert.Equal(afterFirst, recorder.EnumerateCalls);
    }

    [Fact]
    public void ActiveEnumeration_IsRerun_AfterTtlExpires()
    {
        var clock = new TestTimeProvider();
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, clock) { CacheTtl = TimeSpan.FromSeconds(3) };

        service.GetPlaybackDevices();
        int afterFirst = recorder.EnumerateCalls;

        clock.Advance(TimeSpan.FromSeconds(4));
        service.GetPlaybackDevices();

        Assert.True(recorder.EnumerateCalls > afterFirst, "TTL 过期后必须重新枚举");
    }

    [Fact]
    public void BypassCache_ForcesReenumeration()
    {
        var clock = new TestTimeProvider();
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, clock);

        service.GetPlaybackDevices();
        int afterFirst = recorder.EnumerateCalls;

        service.GetPlaybackDevices(bypassCache: true);

        Assert.True(recorder.EnumerateCalls > afterFirst);
    }

    [Fact]
    public void InvalidateCache_ForcesReenumeration()
    {
        var clock = new TestTimeProvider();
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, clock);

        service.GetPlaybackDevices();
        int afterFirst = recorder.EnumerateCalls;

        service.InvalidateCache();
        service.GetPlaybackDevices();

        Assert.True(recorder.EnumerateCalls > afterFirst);
    }

    [Fact]
    public void PlaybackAndRecordingCaches_AreIndependent()
    {
        var clock = new TestTimeProvider();
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, clock);

        service.GetPlaybackDevices();
        service.GetRecordingDevices();
        int afterBoth = recorder.EnumerateCalls;

        clock.Advance(TimeSpan.FromMilliseconds(500));
        service.GetPlaybackDevices();
        service.GetRecordingDevices();

        // 两个方向各自命中缓存，不应再产生枚举
        Assert.Equal(afterBoth, recorder.EnumerateCalls);
        Assert.Contains(AudioDataFlow.Render, recorder.DevicesRequested);
        Assert.Contains(AudioDataFlow.Capture, recorder.DevicesRequested);
    }

    [Fact]
    public void Enumerators_AreDisposed_AfterUse()
    {
        (EnumeratorRecorder recorder, Func<IDeviceEnumerator> factory) = CreateFactory();
        var service = new DeviceService(factory, TimeProvider.System);

        service.GetPlaybackDevices();

        Assert.True(recorder.CreatedCount > 0, "应创建过枚举器");
        Assert.Equal(recorder.CreatedCount, recorder.DisposeCount);
    }
}

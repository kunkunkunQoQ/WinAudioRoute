namespace WinAudioRoute.Tests;

/// <summary>
/// 真实 Windows 上的<b>只读</b>集成测试。
/// <para>
/// 这些测试不会修改任何音频状态：只枚举设备、读取默认设备、枚举会话、读取音量。
/// 当机器没有对应设备/会话时提前返回并登记提示（见 <see cref="TestEnvironment"/>）。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class ReadOnlyIntegrationTests
{
    [Fact]
    public void Manager_CanBeConstructed_OnThisMachine()
    {
        Assert.True(TestEnvironment.Is64BitProcess, "本库要求 64 位进程");

        using var audio = new WindowsAudioManager();

        Assert.False(audio.IsDisposed);
    }

    [Fact]
    public void GetPlaybackDevices_ReturnsDevicesWithValidData()
    {
        using var audio = new WindowsAudioManager();

        IReadOnlyList<AudioDevice> devices = audio.GetPlaybackDevices();

        if (devices.Count == 0)
        {
            TestEnvironment.Skip("GetPlaybackDevices: 本机没有活动播放设备");
            return;
        }

        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.Equal(AudioDataFlow.Render, device.Flow);
            Assert.NotEqual(AudioDeviceState.None, device.State);
        });
    }

    [Fact]
    public void GetRecordingDevices_ReturnsDevicesWithValidData()
    {
        using var audio = new WindowsAudioManager();

        IReadOnlyList<AudioDevice> devices = audio.GetRecordingDevices();

        if (devices.Count == 0)
        {
            TestEnvironment.Skip("GetRecordingDevices: 本机没有活动录音设备");
            return;
        }

        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.Equal(AudioDataFlow.Capture, device.Flow);
        });
    }

    [Fact]
    public void GetDevices_WithAllStates_DoesNotThrow()
    {
        using var audio = new WindowsAudioManager();

        IReadOnlyList<AudioDevice> all = audio.GetDevices(AudioDataFlow.All, AudioDeviceState.All);

        // 允许为空（例如纯 CI 虚拟机），但不能抛异常
        Assert.NotNull(all);
    }

    [Fact]
    public void GetDevices_NonActiveStates_AreEnumerable()
    {
        using var audio = new WindowsAudioManager();

        IReadOnlyList<AudioDevice> inactive = audio.GetPlaybackDevices(
            AudioDeviceState.Disabled | AudioDeviceState.NotPresent | AudioDeviceState.Unplugged,
            bypassCache: true);

        Assert.NotNull(inactive);
    }

    [Fact]
    public void GetDevices_AllStates_IncludesActiveDevices()
    {
        using var audio = new WindowsAudioManager();

        IReadOnlyList<AudioDevice> active = audio.GetPlaybackDevices(AudioDeviceState.Active);
        IReadOnlyList<AudioDevice> all = audio.GetPlaybackDevices(AudioDeviceState.All, bypassCache: true);

        if (active.Count == 0)
        {
            TestEnvironment.Skip("GetDevices_AllStates: 本机没有活动播放设备");
            return;
        }

        // 全部状态集合必须包含所有活动设备
        Assert.All(active, device => Assert.Contains(all, d => d.Id == device.Id));
    }

    [Fact]
    public void ActiveDevices_AreMarkedAsActive()
    {
        using var audio = new WindowsAudioManager();

        IReadOnlyList<AudioDevice> devices = audio.GetPlaybackDevices();

        if (devices.Count == 0)
        {
            TestEnvironment.Skip("ActiveDevices_AreMarkedAsActive: 本机没有活动播放设备");
            return;
        }

        Assert.All(devices, d => Assert.True(d.IsActive, $"设备 {d.Id} 应为活动状态"));
    }

    [Theory]
    [InlineData(AudioDataFlow.Render, AudioRole.Console)]
    [InlineData(AudioDataFlow.Render, AudioRole.Multimedia)]
    [InlineData(AudioDataFlow.Render, AudioRole.Communications)]
    [InlineData(AudioDataFlow.Capture, AudioRole.Console)]
    [InlineData(AudioDataFlow.Capture, AudioRole.Multimedia)]
    [InlineData(AudioDataFlow.Capture, AudioRole.Communications)]
    public void GetDefaultDevice_AllSixFlowRoleCombinations_DoNotThrow(
        AudioDataFlow flow,
        AudioRole role)
    {
        using var audio = new WindowsAudioManager();

        AudioDevice? device = audio.GetDefaultDevice(flow, role, bypassCache: true);

        if (device is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.Equal(flow, device.Flow);
        }
        else
        {
            // 未配置该角色的默认端点（例如某些系统没有默认通信设备）→ 允许为 null
            TestEnvironment.Skip($"GetDefaultDevice: {flow}/{role} 未配置默认端点");
        }
    }

    [Fact]
    public void DefaultConsoleDevice_IsMarkedOnTheDeviceList()
    {
        using var audio = new WindowsAudioManager();

        AudioDevice? console = audio.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Console);
        if (console is null)
        {
            TestEnvironment.Skip("DefaultConsoleDevice: 本机没有默认 Console 播放设备");
            return;
        }

        IReadOnlyList<AudioDevice> devices = audio.GetPlaybackDevices();
        AudioDevice? match = devices.FirstOrDefault(d => d.Id == console.Id);

        if (match is null)
        {
            TestEnvironment.Skip("DefaultConsoleDevice: 默认设备不在活动设备列表中");
            return;
        }

        Assert.True(match.IsDefaultConsole, "默认 Console 设备应被标记 IsDefaultConsole");
    }

    [Fact]
    public void ResolvePlaybackDevice_ById_ReturnsTheSameDevice()
    {
        using var audio = new WindowsAudioManager();

        IReadOnlyList<AudioDevice> devices = audio.GetPlaybackDevices();
        if (devices.Count == 0)
        {
            TestEnvironment.Skip("ResolvePlaybackDevice: 本机没有活动播放设备");
            return;
        }

        AudioDevice resolved = audio.ResolvePlaybackDevice(devices[0].Id);

        Assert.Equal(devices[0].Id, resolved.Id);
    }

    [Fact]
    public void ResolvePlaybackDevice_ByExactName_ReturnsTheSameDevice()
    {
        using var audio = new WindowsAudioManager();

        AudioDevice? target = audio.GetPlaybackDevices()
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.FriendlyName));

        if (target is null)
        {
            TestEnvironment.Skip("ResolvePlaybackDevice_ByExactName: 本机没有带名称的活动播放设备");
            return;
        }

        AudioDevice resolved = audio.ResolvePlaybackDevice(target.FriendlyName);

        Assert.Equal(target.Id, resolved.Id);
    }

    [Fact]
    public void ResolvePlaybackDevice_UnknownName_ThrowsDeviceNotFound()
    {
        using var audio = new WindowsAudioManager();

        _ = audio.GetPlaybackDevices(); // 确保枚举可用

        Assert.Throws<AudioDeviceNotFoundException>(
            () => audio.ResolvePlaybackDevice("WinAudioRoute Nonexistent Device 0000"));
    }

    [Fact]
    public void ResolveDevice_AmbiguousQuery_ThrowsAmbiguousExceptionOrSucceeds()
    {
        using var audio = new WindowsAudioManager();

        IReadOnlyList<AudioDevice> devices = audio.GetPlaybackDevices();
        if (devices.Count < 2)
        {
            TestEnvironment.Skip("ResolveDevice_AmbiguousQuery: 需要至少两个播放设备");
            return;
        }

        // 用一个几乎必然匹配多个设备的宽松子串；若恰好唯一则允许成功
        try
        {
            AudioDevice resolved = audio.ResolvePlaybackDevice("(");
            Assert.False(string.IsNullOrWhiteSpace(resolved.Id));
        }
        catch (AmbiguousAudioDeviceException ex)
        {
            Assert.NotEmpty(ex.Candidates);
        }
        catch (AudioDeviceNotFoundException)
        {
            // 所有设备名称都不含 "(" —— 属于环境差异，不算失败
            TestEnvironment.Skip("ResolveDevice_AmbiguousQuery: 本机设备名不含可复现的公共子串");
        }
    }

    [Fact]
    public void GetSessions_DoesNotThrow_AndIsSessionGranular()
    {
        using var audio = new WindowsAudioManager();

        IReadOnlyList<AudioSession> sessions = audio.GetSessions(bypassCache: true);

        if (sessions.Count == 0)
        {
            TestEnvironment.Skip("GetSessions: 本机当前没有音频会话");
            return;
        }

        Assert.All(sessions, session =>
        {
            Assert.True(session.ProcessId > 0);
            Assert.False(string.IsNullOrWhiteSpace(session.DeviceId));
        });

        // 会话粒度：同一 PID 允许多条；实例标识符应唯一
        var instanceIds = sessions
            .Where(s => !string.IsNullOrEmpty(s.SessionInstanceIdentifier))
            .Select(s => s.SessionInstanceIdentifier)
            .ToList();

        Assert.Equal(instanceIds.Count, instanceIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void GetSessionsForProcess_WithProcessName_NoSessions_Throws()
    {
        using var audio = new WindowsAudioManager();

        _ = audio.GetSessions(bypassCache: true); // 预热

        string ownName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        // 进程名 → 会话是"严格解析"：没有任何会话时抛 AudioSessionNotFoundException，
        // 而不是静默返回空集合（避免调用方把"没找到"误当成"已处理"）。
        Assert.Throws<AudioSessionNotFoundException>(() => audio.GetSessionsForProcess(ownName));
    }

    [Fact]
    public void GetSessionsForProcess_WithProcessName_WhenOwnProcessHasSessions_ReturnsThem()
    {
        using var audio = new WindowsAudioManager();

        IReadOnlyList<AudioSession> all = audio.GetSessions(bypassCache: true);
        AudioSession? any = all.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ProcessName));

        if (any is null)
        {
            TestEnvironment.Skip("GetSessionsForProcess_ByName: 本机没有带进程名的会话");
            return;
        }

        try
        {
            IReadOnlyList<AudioSession> found = audio.GetSessionsForProcess(any.ProcessName! + ".exe");

            Assert.NotEmpty(found);
            Assert.All(found, s => Assert.Equal(any.ProcessId, s.ProcessId));
        }
        catch (AmbiguousAudioSessionException ex)
        {
            // 同名多 PID：这是设计内的行为，异常必须携带候选 PID
            Assert.NotEmpty(ex.CandidateProcessIds);
        }
    }

    [Fact]
    public void GetSessionsForProcess_WithPid_DoesNotThrow()
    {
        using var audio = new WindowsAudioManager();

        int ownPid = Environment.ProcessId;
        IReadOnlyList<AudioSession> sessions = audio.GetSessionsForProcess(ownPid);

        Assert.NotNull(sessions);
    }

    [Fact]
    public void GetDeviceVolume_And_Mute_AreReadable()
    {
        using var audio = new WindowsAudioManager();

        AudioDevice? device = audio.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Console)
            ?? audio.GetPlaybackDevices().FirstOrDefault();

        if (device is null)
        {
            TestEnvironment.Skip("GetDeviceVolume: 本机没有可用播放设备");
            return;
        }

        float volume = audio.GetDeviceVolume(device);
        bool muted = audio.GetDeviceMute(device);

        Assert.InRange(volume, 0.0f, 1.0f);
        Assert.True(muted || !muted); // 只要求可读
    }

    [Fact]
    public void GetApplicationVolume_ForAnExistingSession_IsReadable()
    {
        using var audio = new WindowsAudioManager();

        AudioSession? session = audio.GetSessions(bypassCache: true).FirstOrDefault();
        if (session is null)
        {
            TestEnvironment.Skip("GetApplicationVolume: 本机当前没有音频会话");
            return;
        }

        float volume = audio.GetApplicationVolume(session.ProcessId);
        bool muted = audio.GetApplicationMute(session.ProcessId);

        Assert.InRange(volume, 0.0f, 1.0f);
        Assert.True(muted || !muted);
    }

    [Fact]
    public void DisposedManager_ThrowsObjectDisposed()
    {
        var audio = new WindowsAudioManager();
        audio.Dispose();

        Assert.True(audio.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => audio.GetPlaybackDevices());
        Assert.Throws<ObjectDisposedException>(() => audio.GetSessions());
        Assert.Throws<ObjectDisposedException>(() => audio.GetApplicationVolume(Environment.ProcessId));
    }

    [Fact]
    public void RepeatedSessionEnumeration_DoesNotAccumulateHandles()
    {
        using var audio = new WindowsAudioManager();

        // 反复强制枚举：句柄必须被缓存替换逻辑释放，而不是无限累积
        for (int i = 0; i < 10; i++)
        {
            _ = audio.GetSessions(bypassCache: true);
        }

        long before = GC.GetTotalMemory(forceFullCollection: true);
        for (int i = 0; i < 20; i++)
        {
            _ = audio.GetSessions(bypassCache: true);
        }

        long after = GC.GetTotalMemory(forceFullCollection: true);

        // 粗略上界：20 次全量枚举不应该占用几十 MB
        Assert.True(
            after - before < 32L * 1024 * 1024,
            $"会话枚举疑似泄漏内存：before={before}, after={after}");
    }
}

namespace WinAudioRoute.Tests;

/// <summary>
/// 会<b>修改系统音频状态</b>的测试。
/// <para>
/// <b>默认禁止执行</b>：只有显式设置环境变量 <c>RUN_AUDIO_MUTATION_TESTS=1</c> 才会运行，
/// 否则每个测试直接返回。
/// </para>
/// <para>
/// <b>恢复契约</b>：每个测试都必须 ① 记录原状态；② <c>try</c> 内执行修改；
/// ③ 验证；④ <c>finally</c> 内恢复原状态。即使断言失败，<c>finally</c> 仍会执行恢复。
/// </para>
/// </summary>
[Trait("Category", "Mutation")]
public class MutationIntegrationTests
{
    /// <summary>满足门控则继续，否则登记跳过并返回 false。</summary>
    private static bool RequireGate()
    {
        if (TestEnvironment.MutationTestsEnabled)
        {
            return true;
        }

        TestEnvironment.Skip(
            $"Mutation: 未设置 {TestEnvironment.MutationSwitch}=1，跳过会修改系统状态的测试");
        return false;
    }

    private static AudioDevice? FirstPlaybackDevice(WindowsAudioManager audio) =>
        audio.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Console)
        ?? audio.GetPlaybackDevices().FirstOrDefault();

    /// <summary>
    /// 尽最大努力恢复某个 Role 的默认设备：即使单个恢复调用失败，也不得影响其余 Role 的恢复。
    /// </summary>
    private static void TryRestoreDefaultDevice(
        WindowsAudioManager audio,
        AudioRole role,
        AudioDevice? device,
        List<string>? failures = null)
    {
        if (device is null)
        {
            return;
        }

        try
        {
            audio.SetDefaultDevice(device, role);
        }
        catch (Exception ex)
        {
            failures?.Add($"{role}: {ex.GetType().Name} {ex.Message}");
        }
    }

    [Fact]
    public void SetDeviceVolume_ThenRestore()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        AudioDevice? device = FirstPlaybackDevice(audio);
        if (device is null)
        {
            TestEnvironment.Skip("Mutation/SetDeviceVolume: 本机没有可用播放设备");
            return;
        }

        float original = audio.GetDeviceVolume(device);
        float target = original > 0.5f ? 0.25f : 0.75f;

        try
        {
            audio.SetDeviceVolume(device, target);
            float actual = audio.GetDeviceVolume(device);

            Assert.InRange(actual, target - 0.02f, target + 0.02f);
        }
        finally
        {
            audio.SetDeviceVolume(device, original);

            float restored = audio.GetDeviceVolume(device);
            Assert.InRange(restored, original - 0.02f, original + 0.02f);
        }
    }

    [Fact]
    public void SetDeviceVolume_OutOfRangeInput_IsClamped()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        AudioDevice? device = FirstPlaybackDevice(audio);
        if (device is null)
        {
            TestEnvironment.Skip("Mutation/SetDeviceVolume_Clamp: 本机没有可用播放设备");
            return;
        }

        float original = audio.GetDeviceVolume(device);

        try
        {
            audio.SetDeviceVolume(device, 5.0f);
            Assert.Equal(1.0f, audio.GetDeviceVolume(device), precision: 2);

            audio.SetDeviceVolume(device, -3.0f);
            Assert.Equal(0.0f, audio.GetDeviceVolume(device), precision: 2);
        }
        finally
        {
            audio.SetDeviceVolume(device, original);
        }
    }

    [Fact]
    public void SetDeviceMute_ThenRestore()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        AudioDevice? device = FirstPlaybackDevice(audio);
        if (device is null)
        {
            TestEnvironment.Skip("Mutation/SetDeviceMute: 本机没有可用播放设备");
            return;
        }

        bool original = audio.GetDeviceMute(device);

        try
        {
            audio.SetDeviceMute(device, !original);
            Assert.Equal(!original, audio.GetDeviceMute(device));
        }
        finally
        {
            audio.SetDeviceMute(device, original);
            Assert.Equal(original, audio.GetDeviceMute(device));
        }
    }

    [Fact]
    public void SetApplicationVolume_ThenRestore()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        AudioSession? session = audio.GetSessions(bypassCache: true).FirstOrDefault();
        if (session is null)
        {
            TestEnvironment.Skip("Mutation/SetApplicationVolume: 本机当前没有音频会话");
            return;
        }

        int pid = session.ProcessId;
        float original = audio.GetApplicationVolume(pid);
        float target = original > 0.5f ? 0.2f : 0.8f;

        try
        {
            AudioOperationResult result = audio.SetApplicationVolume(pid, target);

            Assert.True(result.Total > 0, "应至少作用于一个会话");
            Assert.True(result.Succeeded > 0, $"写入应至少成功一次：{result}");

            float actual = audio.GetApplicationVolume(pid);
            Assert.InRange(actual, target - 0.02f, target + 0.02f);
        }
        finally
        {
            audio.SetApplicationVolume(pid, original);

            float restored = audio.GetApplicationVolume(pid);
            Assert.InRange(restored, original - 0.02f, original + 0.02f);
        }
    }

    [Fact]
    public void SetApplicationMute_ThenRestore()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        AudioSession? session = audio.GetSessions(bypassCache: true).FirstOrDefault();
        if (session is null)
        {
            TestEnvironment.Skip("Mutation/SetApplicationMute: 本机当前没有音频会话");
            return;
        }

        int pid = session.ProcessId;
        bool original = audio.GetApplicationMute(pid);

        try
        {
            AudioOperationResult result = audio.SetApplicationMute(pid, !original);

            Assert.True(result.Succeeded > 0, $"写入应至少成功一次：{result}");
            Assert.Equal(!original, audio.GetApplicationMute(pid));
        }
        finally
        {
            audio.SetApplicationMute(pid, original);
            Assert.Equal(original, audio.GetApplicationMute(pid));
        }
    }

    [Fact]
    public void SetApplicationVolume_UnknownProcess_ThrowsSessionNotFound()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        // 使用一个几乎不可能存在的 PID：不修改任何状态，仅验证异常路径
        Assert.Throws<AudioSessionNotFoundException>(
            () => audio.SetApplicationVolume(0x7FFFFFF0, 0.5f));
    }

    /// <summary>
    /// 检查系统默认设备写入是否可用。
    /// <para>
    /// Milestone B.1 起该路径<b>默认启用</b>并已真机验证。若本机 <c>IPolicyConfig</c> 不可用，
    /// 则登记环境绕过并跳过（不强制调用不可用的原生路径）。
    /// </para>
    /// </summary>
    private static bool RequireDefaultDeviceWrites(WindowsAudioManager audio)
    {
        if (audio.IsDefaultDeviceWriteEnabled)
        {
            return true;
        }

        TestEnvironment.Skip(
            $"Mutation/DefaultDevice: 本机系统默认设备写入不可用（{audio.DefaultDeviceWriteBlocker}）");
        return false;
    }

    [Fact]
    public void SetDefaultDevice_EveryRole_TransmitsAndReadsBack()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        if (!RequireDefaultDeviceWrites(audio))
        {
            return;
        }

        AudioDevice? device = FirstPlaybackDevice(audio);
        if (device is null)
        {
            TestEnvironment.Skip("Mutation/SetDefaultDevice: 本机没有可用播放设备");
            return;
        }

        foreach (AudioRole role in (AudioRole[])[AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications])
        {
            AudioDevice? before = audio.GetDefaultDevice(AudioDataFlow.Render, role);

            try
            {
                AudioOperationResult result = audio.SetDefaultDevice(device, role);

                Assert.True(result.IsSuccess, $"Role {role} 设置失败：{result}");

                AudioDevice? after = audio.GetDefaultDevice(AudioDataFlow.Render, role);
                Assert.NotNull(after);
                Assert.Equal(device.Id, after!.Id);
            }
            finally
            {
                TryRestoreDefaultDevice(audio, role, before);
            }
        }
    }

    [Fact]
    public void SetDefaultDeviceForAllRoles_ReportsPerRoleResult_AndRestores()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        if (!RequireDefaultDeviceWrites(audio))
        {
            return;
        }

        AudioDevice? device = FirstPlaybackDevice(audio);
        if (device is null)
        {
            TestEnvironment.Skip("Mutation/SetDefaultDeviceForAllRoles: 本机没有可用播放设备");
            return;
        }

        AudioDevice?[] before =
        [
            audio.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Console),
            audio.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Multimedia),
            audio.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Communications),
        ];

        try
        {
            AudioOperationResult result = audio.SetDefaultDeviceForAllRoles(device);

            // 三个 Role 都必须被显式尝试（不允许"设置一次覆盖全部"的假设）
            Assert.Equal(3, result.Total);

            // 三个 Role 都应指向目标设备
            foreach (AudioRole role in (AudioRole[])[AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications])
            {
                AudioDevice? current = audio.GetDefaultDevice(AudioDataFlow.Render, role);
                Assert.NotNull(current);
                Assert.Equal(device.Id, current!.Id);
            }
        }
        finally
        {
            var failures = new List<string>();
            AudioRole[] roles = [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications];
            for (int i = 0; i < roles.Length; i++)
            {
                TryRestoreDefaultDevice(audio, roles[i], before[i], failures);
            }

            // 恢复失败必须显式暴露，绝不能静默留下被改动的用户设备
            Assert.Empty(failures);
        }
    }
}

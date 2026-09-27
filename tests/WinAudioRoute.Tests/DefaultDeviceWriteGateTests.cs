namespace WinAudioRoute.Tests;

/// <summary>
/// 系统默认设备写入路径的契约测试。
/// <para>
/// <b>Milestone B.1 结论</b>：写入默认<b>启用</b>。此前 Milestone B 的"默认禁用"是基于错误的
/// vtable 槽位判断（把接口方法序号 11 当成了绝对槽位 11，实际调用到 <c>GetPropertyValue</c>），
/// 已全部修正。
/// </para>
/// <para>
/// 唯一已知运行约束：执行写入时不能同时注册 <c>IMMNotificationClient</c>。
/// 本类已在写入窗口内自动临时注销并恢复，因此 <see cref="WindowsAudioManager.IsDeviceNotificationRegistered"/>
/// 在写入前后都应保持为 <see langword="true"/>。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class DefaultDeviceWriteGateTests
{
    private static AudioDevice FirstPlaybackDevice(WindowsAudioManager audio) =>
        audio.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Console)
        ?? audio.GetPlaybackDevices().First();

    [Fact]
    public void WritesAreEnabledByDefault()
    {
        using var audio = new WindowsAudioManager();

        // 接口探测是只读的；本机可用时应为 true
        Assert.Equal(audio.PrepareDefaultDeviceWrites(), audio.IsDefaultDeviceWriteEnabled);

        if (!audio.IsDefaultDeviceWriteEnabled)
        {
            // 接口不可用的环境：必须给出可诊断原因
            Assert.False(string.IsNullOrWhiteSpace(audio.DefaultDeviceWriteBlocker));
            TestEnvironment.Skip($"DefaultDeviceWrites: 本机 IPolicyConfig 不可用（{audio.DefaultDeviceWriteBlocker}）");
            return;
        }

        Assert.Null(audio.DefaultDeviceWriteBlocker);
    }

    [Fact]
    public void CapabilityProbe_IsReadOnly_AndStable()
    {
        using var audio = new WindowsAudioManager();

        bool first = audio.PrepareDefaultDeviceWrites();
        bool second = audio.PrepareDefaultDeviceWrites();

        Assert.Equal(first, second);
        Assert.Equal(first, audio.IsDefaultDeviceWriteEnabled);
    }

    [Fact]
    public void NullDevice_Throws()
    {
        using var audio = new WindowsAudioManager();

        Assert.Throws<ArgumentNullException>(() => audio.SetDefaultDevice(null!, AudioRole.Console));
        Assert.Throws<ArgumentNullException>(() => audio.SetDefaultDeviceForAllRoles(null!));
    }

    [Fact]
    public void ReadPath_IsUnaffected()
    {
        using var audio = new WindowsAudioManager();

        foreach (AudioRole role in (AudioRole[])[AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications])
        {
            AudioDevice? device = audio.GetDefaultDevice(AudioDataFlow.Render, role);
            if (device is not null)
            {
                Assert.False(string.IsNullOrWhiteSpace(device.Id));
            }
        }
    }

    [Fact]
    public void AfterDispose_WritesReportDisposed()
    {
        var audio = new WindowsAudioManager();
        AudioDevice device = FirstPlaybackDevice(audio);
        audio.Dispose();

        // 释放检查先于能力检查：这样调用方能区分"已释放"与"能力不可用"
        Assert.Throws<ObjectDisposedException>(() => audio.SetDefaultDevice(device, AudioRole.Console));
        Assert.Throws<ObjectDisposedException>(() => audio.SetDefaultDeviceForAllRoles(device));
    }

    [Fact]
    public void DisabledWrites_ThrowRoutingNotSupported_NotCrash()
    {
        using var audio = new WindowsAudioManager();
        AudioDevice device = FirstPlaybackDevice(audio);

        audio.SetDefaultDeviceWritesDisabledForTesting(disabled: true);

        AudioRoutingNotSupportedException ex = Assert.Throws<AudioRoutingNotSupportedException>(
            () => audio.SetDefaultDevice(device, AudioRole.Console));

        Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);

        // 恢复后应可再次通过能力检查
        audio.SetDefaultDeviceWritesDisabledForTesting(disabled: false);
        Assert.True(audio.IsDefaultDeviceWriteEnabled);
    }

    [Fact]
    public void DeviceNotification_RemainsRegistered_AcrossWriteCapabilityChecks()
    {
        using var audio = new WindowsAudioManager();

        bool before = audio.IsDeviceNotificationRegistered;

        _ = audio.PrepareDefaultDeviceWrites();

        Assert.Equal(before, audio.IsDeviceNotificationRegistered);
    }
}

using WinAudioRoute.Interop;

namespace WinAudioRoute.Tests;

/// <summary>
/// 枚举数值契约测试。
/// <para>
/// 这些数值直接进入原生 COM 调用（vtable 参数、状态掩码），一旦改动就是线格式破坏，
/// 因此用测试钉死。同时显式记录 <see cref="AudioRole"/> 与 <see cref="AudioDataFlow"/>
/// 的数值重合——这是此前实现中类型误用的根源，必须靠契约测试守住语义边界。
/// </para>
/// </summary>
public class EnumContractTests
{
    [Fact]
    public void AudioDataFlow_Values()
    {
        Assert.Equal(0, (int)AudioDataFlow.Render);
        Assert.Equal(1, (int)AudioDataFlow.Capture);
        Assert.Equal(2, (int)AudioDataFlow.All);
    }

    [Fact]
    public void AudioRole_Values()
    {
        Assert.Equal(0, (int)AudioRole.Console);
        Assert.Equal(1, (int)AudioRole.Multimedia);
        Assert.Equal(2, (int)AudioRole.Communications);
    }

    [Fact]
    public void AudioDeviceState_Values()
    {
        Assert.Equal(0x0, (int)AudioDeviceState.None);
        Assert.Equal(0x1, (int)AudioDeviceState.Active);
        Assert.Equal(0x2, (int)AudioDeviceState.Disabled);
        Assert.Equal(0x4, (int)AudioDeviceState.NotPresent);
        Assert.Equal(0x8, (int)AudioDeviceState.Unplugged);

        // Windows SDK 的官方常量是 DEVICE_STATEMASK_ALL = 0x0000000F（mmdeviceapi.h），
        // 不是 0xFFFFFFFF，因此本库与原生声明的 All 都必须是 0x0F。
        Assert.Equal(0x0F, (int)AudioDeviceState.All);
        Assert.Equal(
            AudioDeviceState.Active | AudioDeviceState.Disabled
                | AudioDeviceState.NotPresent | AudioDeviceState.Unplugged,
            AudioDeviceState.All);
    }

    [Fact]
    public void AudioSessionState_Values()
    {
        Assert.Equal(0, (int)AudioSessionState.Inactive);
        Assert.Equal(1, (int)AudioSessionState.Active);
        Assert.Equal(2, (int)AudioSessionState.Expired);
    }

    [Fact]
    public void Interop_EDataFlow_Matches_Public_AudioDataFlow()
    {
        Assert.Equal((int)EDataFlow.eRender, (int)AudioDataFlow.Render);
        Assert.Equal((int)EDataFlow.eCapture, (int)AudioDataFlow.Capture);
        Assert.Equal((int)EDataFlow.eAll, (int)AudioDataFlow.All);
    }

    [Fact]
    public void Interop_ERole_Matches_Public_AudioRole()
    {
        Assert.Equal((int)ERole.eConsole, (int)AudioRole.Console);
        Assert.Equal((int)ERole.eMultimedia, (int)AudioRole.Multimedia);
        Assert.Equal((int)ERole.eCommunications, (int)AudioRole.Communications);
    }

    [Fact]
    public void Role_And_DataFlow_Collide_Numerically_By_Design_Of_Windows_Api()
    {
        // Windows 头文件的既成事实：ERole.eConsole == EDataFlow.eRender == 0
        // 两者语义完全不同。本测试把"数值重合"固化成事实记录，
        // 并守住"它们是两个独立枚举、成员名互不重叠"这一语义边界。
        Assert.Equal((int)AudioDataFlow.Render, (int)AudioRole.Console);
        Assert.Equal((int)AudioDataFlow.Capture, (int)AudioRole.Multimedia);
        Assert.Equal(2, (int)AudioRole.Communications);
        Assert.Equal(2, (int)AudioDataFlow.All);

        var roleNames = Enum.GetNames<AudioRole>();
        var flowNames = Enum.GetNames<AudioDataFlow>();
        Assert.Empty(roleNames.Intersect(flowNames, StringComparer.Ordinal));
    }

    [Fact]
    public void Interop_AudioSessionState_Matches_Public()
    {
        Assert.Equal((int)Interop.InteropSessionState.Inactive, (int)AudioSessionState.Inactive);
        Assert.Equal((int)Interop.InteropSessionState.Active, (int)AudioSessionState.Active);
        Assert.Equal((int)Interop.InteropSessionState.Expired, (int)AudioSessionState.Expired);
    }

    [Fact]
    public void Interop_SessionDisconnectReason_Matches_Public()
    {
        Assert.Equal((int)Interop.InteropSessionDisconnectReason.DisconnectReasonDeviceRemoval, (int)AudioSessionDisconnectReason.DeviceRemoval);
        Assert.Equal((int)Interop.InteropSessionDisconnectReason.DisconnectReasonServerShutdown, (int)AudioSessionDisconnectReason.ServerShutdown);
        Assert.Equal((int)Interop.InteropSessionDisconnectReason.DisconnectReasonFormatChanged, (int)AudioSessionDisconnectReason.FormatChanged);
        Assert.Equal((int)Interop.InteropSessionDisconnectReason.DisconnectReasonSessionLogoff, (int)AudioSessionDisconnectReason.SessionLogoff);
        Assert.Equal((int)Interop.InteropSessionDisconnectReason.DisconnectReasonSessionDisconnected, (int)AudioSessionDisconnectReason.SessionDisconnected);
        Assert.Equal((int)Interop.InteropSessionDisconnectReason.DisconnectReasonExclusiveModeOverride, (int)AudioSessionDisconnectReason.ExclusiveModeOverride);
    }

    [Fact]
    public void AudioSessionDisconnectReason_Values()
    {
        Assert.Equal(0, (int)AudioSessionDisconnectReason.DeviceRemoval);
        Assert.Equal(1, (int)AudioSessionDisconnectReason.ServerShutdown);
        Assert.Equal(2, (int)AudioSessionDisconnectReason.FormatChanged);
        Assert.Equal(3, (int)AudioSessionDisconnectReason.SessionLogoff);
        Assert.Equal(4, (int)AudioSessionDisconnectReason.SessionDisconnected);
        Assert.Equal(5, (int)AudioSessionDisconnectReason.ExclusiveModeOverride);
    }

    [Fact]
    public void SessionState_And_DeviceState_Collide_Numerically_ButAreDistinctTypes()
    {
        // AudioSessionState.Active == AudioDeviceState.Active == 1（Windows 头文件的既成事实）
        // 两者语义完全不同：一个是会话运行状态，一个是设备状态掩码。
        Assert.Equal((int)AudioDeviceState.Active, (int)AudioSessionState.Active);
        Assert.Equal(1, (int)AudioSessionState.Active);

        // 数值碰撞是 Windows 头文件的既成事实；语义区分靠类型与成员集，而不是数值。
        // 两个枚举刻意共享 Active 这个名字（都表示"正在工作"），但各自都有对方没有的成员。
        Assert.NotEqual(typeof(AudioDeviceState), typeof(AudioSessionState));
        Assert.Contains("Active", Enum.GetNames<AudioSessionState>());
        Assert.Contains("Active", Enum.GetNames<AudioDeviceState>());
        Assert.Contains("Expired", Enum.GetNames<AudioSessionState>());
        Assert.DoesNotContain("Expired", Enum.GetNames<AudioDeviceState>());
        Assert.Contains("Disabled", Enum.GetNames<AudioDeviceState>());
        Assert.DoesNotContain("Disabled", Enum.GetNames<AudioSessionState>());
    }

    [Fact]
    public void Interop_DeviceState_Matches_Public()
    {
        Assert.Equal((uint)AudioDeviceState.Active, (uint)DeviceState.ACTIVE);
        Assert.Equal((uint)AudioDeviceState.Disabled, (uint)DeviceState.DISABLED);
        Assert.Equal((uint)AudioDeviceState.NotPresent, (uint)DeviceState.NOTPRESENT);
        Assert.Equal((uint)AudioDeviceState.Unplugged, (uint)DeviceState.UNPLUGGED);

        // 原生声明与公开枚举都取头文件真值 DEVICE_STATEMASK_ALL = 0x0000000F
        Assert.Equal(0x0Fu, (uint)DeviceState.ALL);
        Assert.Equal((uint)DeviceState.ALL, (uint)AudioDeviceState.All);
    }

    [Fact]
    public void DeviceState_All_IsFourBitUnion()
    {
        Assert.Equal(0x0F, (int)AudioDeviceState.All);
        Assert.True(AudioDeviceState.All.HasFlag(AudioDeviceState.Active));
        Assert.True(AudioDeviceState.All.HasFlag(AudioDeviceState.Disabled));
        Assert.True(AudioDeviceState.All.HasFlag(AudioDeviceState.NotPresent));
        Assert.True(AudioDeviceState.All.HasFlag(AudioDeviceState.Unplugged));
    }
}

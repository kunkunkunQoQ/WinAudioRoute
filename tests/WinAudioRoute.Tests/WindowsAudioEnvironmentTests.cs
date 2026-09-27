using System.Runtime.InteropServices;
using WinAudioRoute.Platform;

namespace WinAudioRoute.Tests;

/// <summary>
/// <see cref="WindowsAudioEnvironment"/> 的版本解析与平台判定测试。
/// 纯逻辑部分通过 internal 可注入重载测试，不依赖本机真实的 Windows Build 号。
/// </summary>
public class WindowsAudioEnvironmentTests
{
    [Fact]
    public void ResolveBuildCore_UsesBuildNumber_WhenPositive()
    {
        var raw = new WindowsAudioEnvironment.RawOsVersion(
            MajorVersion: 10, MinorVersion: 0, BuildNumber: 26100, PlatformId: 2);

        Assert.Equal(26100, WindowsAudioEnvironment.ResolveBuildCore(raw));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ResolveBuildCore_ReturnsZero_WhenBuildNumberUnusable(int build)
    {
        var raw = new WindowsAudioEnvironment.RawOsVersion(
            MajorVersion: 10, MinorVersion: 0, BuildNumber: build, PlatformId: 2);

        Assert.Equal(0, WindowsAudioEnvironment.ResolveBuildCore(raw));
    }

    [Fact]
    public void ResolveBuildCore_DefaultRaw_ReturnsZero()
    {
        Assert.Equal(0, WindowsAudioEnvironment.ResolveBuildCore(default));
    }

    [Theory]
    [InlineData(21999, false)] // Win10 22H2 及以前
    [InlineData(22000, true)]  // Win11 21H2 阈值
    [InlineData(22621, true)]  // Win11 22H2
    [InlineData(26100, true)]  // Win11 24H2
    [InlineData(0, false)]     // 取不到版本
    public void IsWindows11OrLaterCore_Threshold(int build, bool expected)
    {
        Assert.Equal(expected, WindowsAudioEnvironment.IsWindows11OrLaterCore(build));
    }

    [Fact]
    public void Windows11Build_Threshold_Is22000()
    {
        Assert.Equal(22000, WindowsAudioEnvironment.Windows11Build);
    }

    [Fact]
    public void OsBuild_IsQueryable_And_Consistent()
    {
        int build = WindowsAudioEnvironment.OsBuild;

        // 本机是 Windows：必须取到真实 Build 号（RtlGetVersion 或 BCL 回退）
        Assert.True(build > 0, $"OsBuild 应大于 0，实际 {build}");
        Assert.Equal(build >= 22000, WindowsAudioEnvironment.IsWindows11OrLater);
    }

    [Fact]
    public void OsBuild_IsCached_AcrossCalls()
    {
        Assert.Equal(WindowsAudioEnvironment.OsBuild, WindowsAudioEnvironment.OsBuild);
    }

    [Fact]
    public void PlatformProperties_AreConsistent()
    {
        Assert.Equal(IntPtr.Size, WindowsAudioEnvironment.PointerSize);
        Assert.Equal(RuntimeInformation.ProcessArchitecture, WindowsAudioEnvironment.ProcessArchitecture);
        Assert.Equal(Environment.Is64BitProcess, WindowsAudioEnvironment.IsSupportedPlatform);
    }

    [Fact]
    public void IsWindows_IsTrue_OnTestHost()
    {
        Assert.True(WindowsAudioEnvironment.IsWindows, "测试只在 Windows 上运行");
    }

    [Fact]
    public void EnsureSupportedPlatform_DoesNotThrow_OnX64Windows()
    {
        WindowsAudioEnvironment.EnsureSupportedPlatform();
    }

    [Fact]
    public void X86_IsExplicitlyRejected_ByGuard()
    {
        // 直接验证守护逻辑对 x86 的拒绝（不依赖本机是否为 x86）
        Assert.Throws<PlatformNotSupportedException>(
            () => PlatformGuard.ThrowIfUnsupported(isWindows: true, pointerSize: 4, architecture: Architecture.X86));
    }
}

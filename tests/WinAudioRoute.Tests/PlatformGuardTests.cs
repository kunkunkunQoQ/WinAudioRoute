using System.Runtime.InteropServices;
using WinAudioRoute.Platform;

namespace WinAudioRoute.Tests;

/// <summary>
/// 平台守护测试：非 Windows、x86、被拒绝架构都必须 fail fast，而不是"尽力运行"。
/// 全部断言基于纯逻辑重载，不依赖本机架构，可在任何机器上稳定运行。
/// </summary>
public class PlatformGuardTests
{
    [Fact]
    public void Supported_OnWindowsX64()
    {
        Assert.True(PlatformGuard.IsSupported(isWindows: true, pointerSize: 8));
    }

    [Fact]
    public void Supported_OnWindowsArm64()
    {
        // ARM64 保留为目标平台（实验状态），不得拒绝
        Assert.True(PlatformGuard.IsSupported(isWindows: true, pointerSize: 8));
    }

    [Theory]
    [InlineData(4)]  // x86（32 位）
    [InlineData(2)]  // 理论上的 16 位
    [InlineData(16)] // 非现实值
    public void NotSupported_OnNon64BitPointerSize(int pointerSize)
    {
        Assert.False(PlatformGuard.IsSupported(isWindows: true, pointerSize: pointerSize));
    }

    [Fact]
    public void NotSupported_OnNonWindows()
    {
        Assert.False(PlatformGuard.IsSupported(isWindows: false, pointerSize: 8));
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Wasm)]
    [InlineData(Architecture.S390x)]
    [InlineData(Architecture.LoongArch64)]
    [InlineData(Architecture.Armv6)]
    [InlineData(Architecture.Ppc64le)]
    public void RejectedArchitectures(Architecture architecture)
    {
        Assert.True(PlatformGuard.IsRejectedArchitecture(architecture));
    }

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public void AcceptedArchitectures(Architecture architecture)
    {
        Assert.False(PlatformGuard.IsRejectedArchitecture(architecture));
    }

    [Fact]
    public void ThrowIfUnsupported_NonWindows_Throws()
    {
        var ex = Assert.Throws<PlatformNotSupportedException>(
            () => PlatformGuard.ThrowIfUnsupported(isWindows: false, pointerSize: 8, architecture: Architecture.X64));

        Assert.Contains("Windows", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfUnsupported_X86_Throws_And_MentionsPointerWidth()
    {
        var ex = Assert.Throws<PlatformNotSupportedException>(
            () => PlatformGuard.ThrowIfUnsupported(isWindows: true, pointerSize: 4, architecture: Architecture.X86));

        Assert.Contains("32", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PROPVARIANT", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfUnsupported_X86_Throws_Even_When_Architecture_Looks_Accepted()
    {
        // x86 进程报告 X86，但即便架构字段伪装成 X64，指针宽度检查也必须先拦住
        Assert.Throws<PlatformNotSupportedException>(
            () => PlatformGuard.ThrowIfUnsupported(isWindows: true, pointerSize: 4, architecture: Architecture.X64));
    }

    [Fact]
    public void ThrowIfUnsupported_RejectedArchitecture_Throws()
    {
        var ex = Assert.Throws<PlatformNotSupportedException>(
            () => PlatformGuard.ThrowIfUnsupported(isWindows: true, pointerSize: 8, architecture: Architecture.Wasm));

        Assert.Contains("Wasm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfUnsupported_WindowsX64_DoesNotThrow()
    {
        PlatformGuard.ThrowIfUnsupported(isWindows: true, pointerSize: 8, architecture: Architecture.X64);
    }

    [Fact]
    public void RequiredPointerSize_IsEight()
    {
        Assert.Equal(8, PlatformGuard.RequiredPointerSize);
    }
}

using System.Runtime.InteropServices;

namespace WinAudioRoute.Platform;

/// <summary>
/// 运行平台守护：在触碰任何原生音频互操作之前 fail fast。
/// <para>
/// 为什么需要它：本库的互操作层对进程架构有**硬性假设** ——
/// <c>PROPVARIANT</c> 被声明为 <c>LayoutKind.Explicit, Size = 24</c>（x64/ARM64 布局，
/// 见 <c>Interop/ComInterop.cs</c>），并且 per-app 路由使用手写 vtable 调用。
/// 在 x86 进程下 <c>PropVariantClear</c> 会读越界，属于内存破坏而非普通异常，
/// 因此必须拒绝而不是"尽力运行"。
/// </para>
/// <para>
/// 本类只包含纯逻辑 + 运行时读值，全部判定都可单测（见 <c>PlatformGuardTests</c>）。
/// </para>
/// </summary>
internal static class PlatformGuard
{
    /// <summary>本库要求的指针宽度（x64 / ARM64 均为 8）。</summary>
    internal const int RequiredPointerSize = 8;

    /// <summary>判断给定运行环境是否受支持。</summary>
    /// <param name="isWindows">是否运行在 Windows 上。</param>
    /// <param name="pointerSize"><see cref="IntPtr.Size"/> 的值。</param>
    /// <returns>受支持返回 <see langword="true"/>。</returns>
    internal static bool IsSupported(bool isWindows, int pointerSize) =>
        isWindows && pointerSize == RequiredPointerSize;

    /// <summary>判断给定进程架构是否被明确拒绝。</summary>
    /// <param name="architecture">当前进程架构。</param>
    /// <returns>必须拒绝返回 <see langword="true"/>。</returns>
    internal static bool IsRejectedArchitecture(Architecture architecture) =>
        architecture is Architecture.X86 or Architecture.Arm
            or Architecture.Wasm or Architecture.S390x
            or Architecture.LoongArch64 or Architecture.Armv6
            or Architecture.Ppc64le;

    /// <summary>按要求校验当前运行环境，不受支持时抛出异常。</summary>
    /// <exception cref="PlatformNotSupportedException">非 Windows、指针宽度非 8 字节，或架构被明确拒绝。</exception>
    internal static void ThrowIfUnsupported()
    {
        ThrowIfUnsupported(
            isWindows: OperatingSystem.IsWindows(),
            pointerSize: IntPtr.Size,
            architecture: RuntimeInformation.ProcessArchitecture);
    }

    /// <summary>按要求校验指定运行环境，不受支持时抛出异常。可直接单测。</summary>
    /// <param name="isWindows">是否运行在 Windows 上。</param>
    /// <param name="pointerSize"><see cref="IntPtr.Size"/> 的值。</param>
    /// <param name="architecture">当前进程架构。</param>
    /// <exception cref="PlatformNotSupportedException">非 Windows、指针宽度非 8 字节，或架构被明确拒绝。</exception>
    internal static void ThrowIfUnsupported(bool isWindows, int pointerSize, Architecture architecture)
    {
        if (!isWindows)
        {
            throw new PlatformNotSupportedException(
                "WinAudioRoute 仅支持 Windows。当前操作系统不是 Windows，音频互操作层不可用。");
        }

        if (pointerSize != RequiredPointerSize)
        {
            throw new PlatformNotSupportedException(
                $"WinAudioRoute 仅支持 64 位进程（x64 / ARM64）。当前进程指针宽度为 {pointerSize * 8} 位：" +
                "x86 下 PROPVARIANT 的 24 字节布局假设不成立，互操作会读越界，因此明确拒绝运行。");
        }

        if (IsRejectedArchitecture(architecture))
        {
            throw new PlatformNotSupportedException(
                $"WinAudioRoute 不支持当前进程架构 {architecture}。已支持的平台为 win-x64（已验证）与 win-arm64（实验）。");
        }
    }
}

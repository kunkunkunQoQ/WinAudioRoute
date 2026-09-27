using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinAudioRoute.Platform;

namespace WinAudioRoute;

/// <summary>
/// 运行环境信息与平台校验。
/// <para>
/// 提取来源：SonicRoute.Core/Compat/WindowsVersion.cs（MIT License,
/// Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）。
/// <c>RtlGetVersion</c> 的真实 Build 检测逻辑原样保留；纯判定逻辑抽成可注入的静态方法以便单测。
/// </para>
/// <para>
/// <b>为什么必须用 <c>RtlGetVersion</c> 而不是 <see cref="Environment.OSVersion"/></b>：
/// .NET Framework 4.8 的 <see cref="Environment.OSVersion"/> 走 <c>GetVersionEx</c>，
/// 受 app.manifest 的 <c>&lt;supportedOS&gt;</c> 声明限制——若未声明 Windows 10，
/// 在 Win10/Win11 上会返回兼容性版本（6.2 / Build 9200），导致按应用音频路由的
/// Win11 分支判断静默失效。.NET 8 内部已改用 <c>RtlGetVersion</c>，此处仍显式调用，
/// 使行为在所有目标框架与宿主下完全一致，且不依赖 manifest。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsAudioEnvironment
{
    /// <summary>Windows 11 的首个 Build 号（21H2 = 22000）。</summary>
    public const int Windows11Build = 22000;

    private static readonly Lazy<RawOsVersion> RawVersion = new(QueryRawVersion, isThreadSafe: true);
    private static readonly Lazy<int> ResolvedBuild = new(ResolveBuildCoreFallback, isThreadSafe: true);

    /// <summary>当前进程是否运行在 Windows 上。</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// 真实操作系统 Build 号（例如 Windows 11 24H2 = 26100）。取不到时返回 0。
    /// </summary>
    public static int OsBuild => ResolvedBuild.Value;

    /// <summary>是否为 Windows 11 或更高版本（<see cref="OsBuild"/> ≥ <see cref="Windows11Build"/>）。</summary>
    public static bool IsWindows11OrLater => IsWindows11OrLaterCore(OsBuild);

    /// <summary>当前进程指针宽度（字节）。受支持平台恒为 8。</summary>
    public static int PointerSize => IntPtr.Size;

    /// <summary>当前进程架构。</summary>
    public static Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;

    /// <summary>
    /// 当前平台是否受支持：必须是 Windows，且为 64 位进程（x64 / ARM64）。
    /// </summary>
    public static bool IsSupportedPlatform =>
        PlatformGuard.IsSupported(IsWindows, IntPtr.Size);

    /// <summary>
    /// 当前进程是否为 ARM64。ARM64 保留为目标平台，但<b>尚未完成真机完整验证</b>，
    /// 因此标记为实验状态；x64 为已验证的主要平台。
    /// </summary>
    public static bool IsArm64 =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary>
    /// 校验当前平台。任何后续会触碰原生音频互操作的操作都应先通过本校验。
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// 非 Windows、32 位进程，或架构被明确拒绝时抛出。
    /// </exception>
    public static void EnsureSupportedPlatform() => PlatformGuard.ThrowIfUnsupported();

    /// <summary>
    /// 由原始版本信息解析出真实 Build 号（纯逻辑，可单测）。
    /// <para>
    /// 规则：<c>dwBuildNumber &gt; 0</c> 时采用；否则视为"取不到"返回 0。
    /// 调用方据此决定是否回退到 <see cref="Environment.OSVersion"/>。
    /// </para>
    /// </summary>
    /// <param name="raw">原始版本信息。</param>
    /// <returns>Build 号；无法取得时返回 0。</returns>
    internal static int ResolveBuildCore(RawOsVersion raw) =>
        raw.BuildNumber > 0 ? raw.BuildNumber : 0;

    /// <summary>判断给定 Build 号是否达到 Windows 11 阈值（纯逻辑，可单测）。</summary>
    /// <param name="build">Build 号。</param>
    /// <returns>达到阈值返回 <see langword="true"/>。</returns>
    internal static bool IsWindows11OrLaterCore(int build) => build >= Windows11Build;

    /// <summary>取得原始版本信息（原生调用，失败时返回全 0）。</summary>
    private static RawOsVersion QueryRawVersion()
    {
        try
        {
            var vi = new RTL_OSVERSIONINFOEXW
            {
                dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOEXW>(),
            };

            // STATUS_SUCCESS == 0
            if (RtlGetVersion(ref vi) == 0)
            {
                return new RawOsVersion(vi.dwMajorVersion, vi.dwMinorVersion, (int)vi.dwBuildNumber, vi.dwPlatformId);
            }
        }
        catch
        {
            // ntdll 不可用等异常情况：退回全 0，由调用方走 BCL 回退
        }

        return default;
    }

    /// <summary>
    /// 解析 Build 号：优先采用 <c>RtlGetVersion</c> 结果，取不到时回退 <see cref="Environment.OSVersion"/>。
    /// </summary>
    private static int ResolveBuildCoreFallback()
    {
        int resolved = ResolveBuildCore(RawVersion.Value);
        if (resolved > 0)
        {
            return resolved;
        }

        try
        {
            return Environment.OSVersion.Version.Build;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>原始版本信息（与 <c>RTL_OSVERSIONINFOEXW</c> 对应的纯数据，便于单测）。</summary>
    /// <param name="MajorVersion">主版本号。</param>
    /// <param name="MinorVersion">次版本号。</param>
    /// <param name="BuildNumber">Build 号。</param>
    /// <param name="PlatformId">平台 ID。</param>
    internal readonly record struct RawOsVersion(
        uint MajorVersion,
        uint MinorVersion,
        int BuildNumber,
        uint PlatformId);

    /// <summary>
    /// <c>RTL_OSVERSIONINFOEXW</c>（winternl.h）：5 个 ULONG + WCHAR[128]。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RTL_OSVERSIONINFOEXW
    {
        /// <summary>结构体大小，调用前必须赋值。</summary>
        public uint dwOSVersionInfoSize;

        /// <summary>主版本号。</summary>
        public uint dwMajorVersion;

        /// <summary>次版本号。</summary>
        public uint dwMinorVersion;

        /// <summary>Build 号。</summary>
        public uint dwBuildNumber;

        /// <summary>平台 ID。</summary>
        public uint dwPlatformId;

        /// <summary>CSD 版本字符串（固定 128 个宽字符）。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }

    /// <summary><c>RtlGetVersion</c>（ntdll.dll）：返回真实 OS 版本，不受 manifest 影响。</summary>
    /// <param name="lpVersionInformation">版本信息结构。</param>
    /// <returns>NTSTATUS（0 = STATUS_SUCCESS）。</returns>
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOEXW lpVersionInformation);
}

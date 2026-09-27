using System.Runtime.InteropServices;
using WinAudioRoute.Interop;
using WinAudioRoute.Routing;

namespace WinAudioRoute.Internal;

/// <summary>
/// 系统默认设备切换后端（<c>IPolicyConfig::SetDefaultEndpoint</c>）。
/// <para>
/// 提取来源：SonicRoute.Core/Interop/PolicyConfigClient.cs
/// （MIT License, Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）。
/// </para>
/// <para>
/// <b>接口语义</b>：<c>SetDefaultEndpoint(PCWSTR wszDeviceId, ERole eRole)</c>。
/// 第二参数是<b>端点角色</b>（Console / Multimedia / Communications），
/// 输出/输入方向由 device ID 本身决定。SonicRoute 把它声明为 <c>EDataFlow</c> 是类型命名错误
/// （数值恰好重合：<c>eRender = 0 = eConsole</c>、<c>eCapture = 1 = eMultimedia</c>），
/// 因此 SonicRoute 实际只设置过 Console 与 Multimedia。
/// </para>
/// <para>
/// <b>公开性</b>：<c>IPolicyConfig</c> 是未公开 COM 接口（CLSID / IID 为社区通用），
/// Microsoft 不保证其存在性与行为稳定性。
/// </para>
/// <para>
/// <b>vtable 编号（重要）</b>：<c>IPolicyConfig</c> 是 <c>InterfaceIsIUnknown</c>，
/// 绝对 vtable 前 3 个槽位被 <c>IUnknown</c> 占用。因此 <c>SetDefaultEndpoint</c> 位于：
/// <list type="bullet">
///   <item><description>绝对 COM vtable 槽位 <b>13</b></description></item>
///   <item><description>1-based 接口方法序号 <b>11</b></description></item>
///   <item><description>0-based 接口方法索引 <b>10</b></description></item>
/// </list>
/// 声明时<b>必须保证前面 10 个方法的数量与顺序正确</b>，否则会用错误的签名调用到别的方法
/// （历史上 WinAudioRoute 的裸探针就因此把 <c>GetPropertyValue</c> 当成 <c>SetDefaultEndpoint</c> 调用，
/// 产生"看起来像崩溃缺陷"的假象）。逐方法对照见 <c>docs/IPOLICYCONFIG_CROSSCHECK.md</c>。
/// </para>
/// <para>
/// <b>已知运行约束</b>：执行该方法时，进程内<b>不能有已注册的 <c>IMMNotificationClient</c></b>，
/// 否则原生侧会崩溃（且崩溃发生在托管回调之前）。
/// <c>WindowsAudioManager</c> 已在写入窗口内进程级串行化并临时注销设备通知；
/// 直接使用本后端的调用方必须自行保证该前提。
/// </para>
/// </summary>
internal sealed class PolicyConfigBackend
{
    private const int HResultClassNotRegistered = unchecked((int)0x80040154u);

    /// <summary>
    /// 激活 <c>IPolicyConfig</c> 并验证接口可用。
    /// <para>
    /// <b>只读探测</b>：只做 <c>CoCreateInstance</c> 与接口转换，<b>不调用任何写入方法</b>，
    /// 因此不会触发已知的访问冲突。
    /// </para>
    /// </summary>
    /// <returns>探测结果。</returns>
    internal static RoutingProbeResult Probe()
    {
        try
        {
            _ = CreateClient();
            return RoutingProbeResult.Supported();
        }
        catch (AudioRoutingNotSupportedException ex)
        {
            return RoutingProbeResult.Failed(ex.Message, ex.HResult);
        }
        catch (Exception ex)
        {
            return RoutingProbeResult.Failed(ex.Message, ex.HResult);
        }
    }

    /// <summary>
    /// 把一个设备设为指定角色的系统默认设备。
    /// </summary>
    /// <param name="deviceId">设备 ID（<c>IMMDevice.GetId()</c> 的短 ID；方向由该 ID 决定）。</param>
    /// <param name="role">端点角色。</param>
    /// <param name="failFast">
    /// 为 <see langword="true"/>（默认）时，失败直接抛异常；
    /// 为 <see langword="false"/> 时返回失败结果而不抛（供批量操作收集全部失败）。
    /// </param>
    /// <returns>操作结果。</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> 为空。</exception>
    /// <exception cref="AudioRoutingNotSupportedException">策略配置 COM 类不可用。</exception>
    /// <exception cref="AudioOperationFailedException">COM 调用返回失败 HRESULT。</exception>
    public AudioOperationResult SetDefaultEndpoint(string deviceId, AudioRole role, bool failFast = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ValidateRole(role);

        IPolicyConfig client = CreateClient();

        int hr = client.SetDefaultEndpoint(deviceId, MapRole(role));

        if (hr >= 0)
        {
            return AudioOperationResult.AllSucceeded(1);
        }

        if (!failFast)
        {
            return AudioOperationResult.AllFailed(
            [
                new AudioOperationFailure(
                    $"{role}",
                    hr,
                    $"SetDefaultEndpoint failed for device '{deviceId}' and role {role}."),
            ]);
        }

        throw new AudioOperationFailedException(
            $"Failed to set device '{deviceId}' as the default endpoint for role {role}.", hr);
    }

    /// <summary>
    /// 依次把设备设为三个角色（Console / Multimedia / Communications）的默认设备。
    /// <para>
    /// <b>不会</b>假设"设置一次就自动覆盖全部角色"——实现在此显式按顺序调用三次，
    /// 并返回逐角色结果，以便暴露部分失败。
    /// </para>
    /// </summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <returns>逐角色结果。</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> 为空。</exception>
    /// <exception cref="AudioRoutingNotSupportedException">策略配置 COM 类不可用。</exception>
    public AudioOperationResult SetDefaultEndpointForAllRoles(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        int succeeded = 0;
        List<AudioOperationFailure> failures = [];

        foreach (AudioRole role in (AudioRole[])[AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications])
        {
            try
            {
                AudioOperationResult result = SetDefaultEndpoint(deviceId, role, failFast: false);
                if (result.IsSuccess)
                {
                    succeeded++;
                }
                else
                {
                    failures.AddRange(result.Failures);
                }
            }
            catch (AudioRoutingNotSupportedException)
            {
                // 后端本身不可用：这不是"某个角色失败"，必须向调用方传播
                throw;
            }
        }

        return failures.Count == 0
            ? AudioOperationResult.AllSucceeded(succeeded)
            : succeeded > 0
                ? AudioOperationResult.Partial(succeeded, failures)
                : AudioOperationResult.AllFailed(failures);
    }

    /// <summary>创建 <c>PolicyConfigClient</c> 实例，并在不可用时给出可诊断的异常。</summary>
    private static IPolicyConfig CreateClient()
    {
        try
        {
            return (IPolicyConfig)new PolicyConfigClientComObject();
        }
        catch (COMException ex) when (ex.HResult == HResultClassNotRegistered)
        {
            throw new AudioRoutingNotSupportedException(
                "The system default-endpoint COM class (PolicyConfigClient) is not registered on this system.",
                ex.HResult);
        }
        catch (Exception ex)
        {
            throw new AudioRoutingNotSupportedException(
                "Failed to activate the system default-endpoint COM class (PolicyConfigClient).", ex.HResult);
        }
    }

    private static ERole MapRole(AudioRole role) => role switch
    {
        AudioRole.Console => ERole.eConsole,
        AudioRole.Multimedia => ERole.eMultimedia,
        AudioRole.Communications => ERole.eCommunications,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported audio role."),
    };

    private static void ValidateRole(AudioRole role)
    {
        if (role is not (AudioRole.Console or AudioRole.Multimedia or AudioRole.Communications))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported audio role.");
        }
    }

    // =================================================================
    // 未公开 COM 接口声明（CLSID / IID 为社区通用，方法顺序即 vtable 契约）
    //
    // 与 SonicRoute.Core/Interop/PolicyConfigClient.cs 的差异仅一处：
    // SetDefaultEndpoint 的第二参数由 EDataFlow 更正为 ERole（语义修正，vtable 布局不变）。
    // =================================================================

    /// <summary><c>PolicyConfigClient</c> 的 COM 可创建类（CLSID 870af99c-171d-4f9e-af0d-e63df40c2bc9）。</summary>
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClientComObject
    {
    }

    /// <summary>
    /// <c>IPolicyConfig</c>（IID f8679f50-850a-41cf-9c72-430f290290c8）。
    /// 12 个方法，<c>SetDefaultEndpoint</c> 位于 vtable 第 11 槽。
    /// </summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat(IntPtr pstereoid, IntPtr ppformat);

        [PreserveSig]
        int GetDeviceFormat(IntPtr pstereoid, IntPtr pdeviceid, IntPtr ppformat);

        [PreserveSig]
        int ResetDeviceFormat(IntPtr pstereoid, IntPtr pdeviceid);

        [PreserveSig]
        int SetDeviceFormat(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pformat, IntPtr pperiod);

        [PreserveSig]
        int GetProcessingPeriod(IntPtr pstereoid, IntPtr pdeviceid, IntPtr ppdefault, IntPtr ppmin);

        [PreserveSig]
        int SetProcessingPeriod(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pperiod);

        [PreserveSig]
        int GetShareMode(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pmode);

        [PreserveSig]
        int SetShareMode(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pmode);

        [PreserveSig]
        int GetPropertyValue(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pkey, IntPtr pvalue);

        [PreserveSig]
        int SetPropertyValue(IntPtr pstereoid, IntPtr pdeviceid, IntPtr pkey, IntPtr pvalue);

        /// <summary>
        /// 设置默认端点。<b>第二参数是 <see cref="ERole"/>（不是 <c>EDataFlow</c>）</b>；
        /// 方向由 <c>wszDeviceId</c> 指向的端点决定。
        /// </summary>
        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, ERole role);

        [PreserveSig]
        int SetEndpointVisibility(IntPtr pstereoid, IntPtr pdeviceid, int bvisible);
    }
}

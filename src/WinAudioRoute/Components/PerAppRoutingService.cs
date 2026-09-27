using WinAudioRoute.Interop;
using WinAudioRoute.Routing;

namespace WinAudioRoute.Internal;

/// <summary>
/// 按应用音频路由服务（per-app routing）。
/// <para>
/// 后端是未公开的 WinRT 内部类 <c>Windows.Media.Internal.AudioPolicyConfig</c>
/// （见 <see cref="AudioPolicyConfigBackend"/> 的文件头警告）。
/// </para>
/// <para>
/// <b>语义（与 Windows 后端一致）</b>：
/// <list type="bullet">
///   <item><description>设备 ID 必须转换为<b>完整设备接口路径</b>才能传给后端
///   （由 <see cref="AudioDeviceIdConverter"/> 完成）。</description></item>
///   <item><description><see langword="null"/> 设备表示<b>删除该应用的持久化端点</b>，
///   即"跟随系统默认设备"。这是后端真实语义，不是本库的发明。</description></item>
///   <item><description>写入后立即回读验证；回读与期望不符视为失败（部分或全部），
///   避免"API 返回成功但实际未生效"被静默吞掉。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>本阶段不提供的危险能力</b>：全局 <c>ClearAllPersistedApplicationDefaultEndpoints</c>
/// 不进入公共 API；注册表 <c>PropertyStore</c> 删除不实现（见 Milestone B 约束）。
/// </para>
/// </summary>
internal sealed class PerAppRoutingService : IDisposable
{
    private readonly AudioPolicyConfigBackend _backend;
    private readonly DeviceService _devices;
    private readonly Lazy<AudioRoutingCapability> _capability;

    /// <summary>创建路由服务。</summary>
    /// <param name="devices">设备服务（用于把持久化 ID 还原为设备对象）。</param>
    /// <param name="backend">策略后端（默认使用真实后端）。</param>
    public PerAppRoutingService(DeviceService devices, AudioPolicyConfigBackend? backend = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _backend = backend ?? new AudioPolicyConfigBackend();
        _capability = new Lazy<AudioRoutingCapability>(ProbeCapability, isThreadSafe: true);
    }

    /// <summary>
    /// 按应用路由能力（首次访问时探测并缓存，之后不再重复探测）。
    /// </summary>
    public AudioRoutingCapability Capability => _capability.Value;

    /// <inheritdoc />
    public void Dispose() => _backend.Dispose();

    /// <summary>
    /// 读取某进程当前持久化的输出/输入设备。
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <param name="flow">方向。</param>
    /// <returns>
    /// 已持久化的设备；未设置或已清除时返回 <see langword="null"/>（= 跟随系统默认）。
    /// </returns>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    public AudioDevice? GetPersistedEndpoint(int processId, AudioDataFlow flow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        EnsureSupported();

        string? fullDeviceId = _backend.GetPersistedDefaultAudioEndpoint(processId, flow);
        if (string.IsNullOrWhiteSpace(fullDeviceId))
        {
            return null;
        }

        string shortDeviceId = AudioDeviceIdConverter.ToShortDeviceId(fullDeviceId);
        if (string.IsNullOrEmpty(shortDeviceId))
        {
            return null;
        }

        // 使用全部状态解析：设备可能已禁用/未插入，此时仍应返回设备对象
        return _devices.GetDeviceWithDefaultFlags(shortDeviceId, flow);
    }

    /// <summary>
    /// 设置或清除某进程的持久化输出/输入设备。
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <param name="flow">方向。</param>
    /// <param name="device"><see langword="null"/> 表示清除（跟随系统默认）。</param>
    /// <returns>逐项结果；<see cref="AudioOperationResult.Total"/> 在写入成功时为 1。</returns>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    /// <exception cref="ArgumentException">设备方向与 <paramref name="flow"/> 不一致。</exception>
    /// <exception cref="AudioOperationFailedException">后端调用失败（携带原始 HRESULT）。</exception>
    public AudioOperationResult SetPersistedEndpoint(int processId, AudioDataFlow flow, AudioDevice? device)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        EnsureSupported();
        ValidateDeviceFlow(device, flow);

        string? fullDeviceId = device is null
            ? null
            : AudioDeviceIdConverter.ToFullDeviceId(device.Id, flow);

        string target = DescribeTarget(processId, flow, device);

        int hr = _backend.SetPersistedDefaultAudioEndpoint(fullDeviceId, processId, flow);
        if (hr < 0)
        {
            throw new AudioOperationFailedException(
                $"Failed to set the persisted audio endpoint for process {processId} " +
                $"({DescribeFlow(flow)}). The per-app routing API returned a failure HRESULT.", hr);
        }

        if (device is null)
        {
            // 清除路径：回读应为"无持久化端点"。若仍能读到，说明清除未生效。
            if (TryReadShortId(processId, flow, out string remaining))
            {
                return BuildResult(
                    succeeded: false,
                    target,
                    hresult: hr,
                    message: $"The persisted endpoint was not cleared (still resolves to '{remaining}').");
            }

            return AudioOperationResult.AllSucceeded(1);
        }

        // 写入路径：回读验证确实指向目标设备，避免"返回成功但未生效"被吞掉
        if (TryReadShortId(processId, flow, out string readBack)
            && string.Equals(readBack, device.Id, StringComparison.Ordinal))
        {
            return AudioOperationResult.AllSucceeded(1);
        }

        return BuildResult(
            succeeded: false,
            target,
            hresult: hr,
            message: $"The route was written but read-back {(string.IsNullOrEmpty(readBack) ? "returned no endpoint" : $"returned '{readBack}'")}, " +
                     $"which does not match the requested device.");
    }

    private void EnsureSupported()
    {
        AudioRoutingCapability capability = Capability;
        if (capability.IsSupported)
        {
            return;
        }

        throw new AudioRoutingNotSupportedException(
            $"Per-app audio routing is not available on this system. {capability.Reason}",
            capability.HResult);
    }

    private static void ValidateDeviceFlow(AudioDevice? device, AudioDataFlow flow)
    {
        if (device is null)
        {
            return;
        }

        if (device.Flow != flow)
        {
            string expected = DescribeFlow(flow);
            string actual = DescribeFlow(device.Flow);
            throw new ArgumentException(
                $"The device '{device.Id}' is a {actual} device and cannot be used as a {expected} route target.",
                nameof(device));
        }
    }

    private bool TryReadShortId(int processId, AudioDataFlow flow, out string shortDeviceId)
    {
        shortDeviceId = string.Empty;

        string? full = _backend.GetPersistedDefaultAudioEndpoint(processId, flow);
        if (string.IsNullOrWhiteSpace(full))
        {
            return false;
        }

        shortDeviceId = AudioDeviceIdConverter.ToShortDeviceId(full);
        return !string.IsNullOrEmpty(shortDeviceId);
    }

    private static AudioOperationResult BuildResult(bool succeeded, string target, int hresult, string message)
    {
        AudioOperationFailure failure = new(target, hresult, message);

        return succeeded
            ? AudioOperationResult.AllSucceeded(1)
            : AudioOperationResult.AllFailed([failure]);
    }

    private static string DescribeTarget(int processId, AudioDataFlow flow, AudioDevice? device) =>
        device is null
            ? $"process {processId} / {DescribeFlow(flow)} / follow-system-default"
            : $"process {processId} / {DescribeFlow(flow)} / {device.Id}";

    private static string DescribeFlow(AudioDataFlow flow) =>
        flow == AudioDataFlow.Capture ? "input" : "output";

    private AudioRoutingCapability ProbeCapability()
    {
        RoutingProbeResult probe = _backend.Probe();

        if (probe.IsSupported)
        {
            return new AudioRoutingCapability(isSupported: true, reason: null, hResult: 0);
        }

        return new AudioRoutingCapability(
            isSupported: false,
            reason: probe.Reason ?? "unknown",
            hResult: probe.HResult);
    }
}

using System.Runtime.Versioning;
using WinAudioRoute.Events;
using WinAudioRoute.Interop;
using WinAudioRoute.Internal;
using WinAudioRoute.Routing;

namespace WinAudioRoute;

/// <summary>
/// WinAudioRoute 的入口门面：Windows 音频设备、会话与默认设备控制的统一访问点。
/// <para>
/// <b>平台要求</b>：Windows 10 2004（Build 19041）或更高版本，且必须是 64 位进程
/// （x64 已验证 / ARM64 交叉编译支持、运行期为实验状态）。
/// 构造时即校验平台，不受支持的环境会立即抛出 <see cref="PlatformNotSupportedException"/>。
/// </para>
/// <para>
/// <b>API 形态</b>：全部为<b>同步</b> API。Windows Core Audio / COM 本质是同步调用，
/// 本库不提供"只是 <c>Task.Run</c> 包装、实际无法取消"的伪异步方法。
/// 需要后台执行时由调用方自行调度。
/// </para>
/// <para>
/// <b>生命周期</b>：本类持有会话缓存与其 COM 引用，因此<b>必须</b>释放
/// （<see cref="Dispose"/> 或 <see cref="IDisposable"/> 的 <c>using</c>）。
/// 释放后调用任何成员都会抛出 <see cref="ObjectDisposedException"/>。
/// </para>
/// <para>
/// <b>音量单位</b>：标量 <see cref="float"/> 0.0–1.0 是核心格式；
/// 百分比变体（0–100 的 <see cref="int"/>）只是便捷辅助。越界值一律<b>钳制</b>，
/// 不抛异常（见 <see cref="AudioVolume"/>）。
/// </para>
/// </summary>
/// <remarks>
/// 派生自 SonicRoute（MIT License, https://github.com/kunkunkunQoQ/SonicRoute）的音频实现；
/// 互操作声明保留原样，仅做 SDK 形态的封装与所有权重构。
/// </remarks>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class WindowsAudioManager : IDisposable
{
    private readonly DeviceService _devices;
    private readonly SessionService _sessions;
    private readonly SessionVolumeService _sessionVolumes;
    private readonly DeviceVolumeService _deviceVolumes;
    private readonly PolicyConfigBackend _policyConfig;
    private readonly PerAppRoutingService _routing;

    private readonly SafeEventDispatcher<AudioDeviceChangedEventArgs> _deviceChangedDispatcher =
        new("DeviceChanged");

    private readonly SafeEventDispatcher<AudioSessionChangedEventArgs> _sessionChangedDispatcher =
        new("SessionChanged");

    private readonly DeviceNotificationClient _deviceNotifications;
    private readonly object _lifecycleGate = new();

    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="WindowsAudioManager"/> 的新实例，并校验当前运行平台。
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// 当前不是 Windows、不是 64 位进程，或进程架构不受支持。
    /// </exception>
    public WindowsAudioManager()
    {
        // 平台守护：x86 / 非 Windows 明确拒绝（PROPVARIANT 布局假设，见 Interop/ComInterop.cs）
        WindowsAudioEnvironment.EnsureSupportedPlatform();

        // 互操作布局断言：布局不符时立即失败，避免后续原生调用读越界
        NativeMethods.AssertInteropLayout();

        _devices = new DeviceService();
        _sessions = new SessionService();
        _sessionVolumes = new SessionVolumeService(_sessions);
        _deviceVolumes = new DeviceVolumeService();
        _policyConfig = new PolicyConfigBackend();
        _routing = new PerAppRoutingService(_devices);

        // 系统默认设备写入：Milestone B.1 起默认启用（真机验证通过）。
        // 探测是只读的（激活接口 + 验证可用），不修改任何状态。
        _ = PrepareDefaultDeviceWrites();

        // 设备通知客户端立即注册：使设备缓存从"TTL 兜底"升级为"事件驱动失效"。
        // 可通过环境变量或 NoDeviceNotificationForDiagnostics 关闭（诊断/隔离用）。
        _noDeviceNotificationForDiagnostics = string.Equals(
            Environment.GetEnvironmentVariable(NoDeviceNotificationDiagnosticsVariable),
            "1",
            StringComparison.Ordinal);

        _deviceNotifications = new DeviceNotificationClient(OnDeviceNotification, OnNotificationRegistrationFailed);

        if (!_noDeviceNotificationForDiagnostics)
        {
            _deviceNotifications.Register();
        }
    }

    /// <summary>
    /// 诊断开关：设为 <c>1</c> 时不注册 <c>IMMNotificationClient</c>。
    /// <para>仅用于隔离诊断（例如排查"默认设备变更时的事件回调"相关问题），不应在正常使用中设置。</para>
    /// </summary>
    public const string NoDeviceNotificationDiagnosticsVariable =
        "WINAUDIOROUTE_DISABLE_DEVICE_NOTIFICATION";

    private readonly bool _noDeviceNotificationForDiagnostics;

    private bool _defaultDeviceWritesEnabled;
    private string? _defaultDeviceWriteBlocker;

    /// <summary>
    /// 当前实例是否已释放。
    /// </summary>
    public bool IsDisposed
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>
    /// 设备变化事件（新增/移除/状态/默认设备/属性变化）。
    /// <para>
    /// <b>线程模型</b>：事件来自 Windows 的 COM 回调线程，<b>不保证</b>是 UI 线程，
    /// 本库不依赖 WPF <c>Dispatcher</c> / WinForms / <c>SynchronizationContext</c>。
    /// 需要 UI 线程的调用方请自行调度（例如 <c>Dispatcher.InvokeAsync</c>）。
    /// </para>
    /// <para>
    /// <b>异常隔离</b>：订阅者抛出的异常不会传播回 COM 边界（否则会变成原生未处理异常），
    /// 而是被吞掉并上报到 <see cref="NotificationHandlerFaulted"/>。
    /// </para>
    /// <para>
    /// <b>回调内不要做重活</b>：不要在事件处理器中做全量设备/会话枚举、路由写入或阻塞等待。
    /// 库内部已保证事件回调本身只做参数捕获与缓存失效。
    /// </para>
    /// </summary>
    public event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged
    {
        add
        {
            _deviceChangedDispatcher.Add(value);

            // 第一次订阅时按需启用会话事件（避免只读枚举场景下的额外 COM 注册开销）
            if (value is not null)
            {
                IsEventSystemEnabled = true;
            }
        }

        remove => _deviceChangedDispatcher.Remove(value);
    }

    /// <summary>
    /// 会话变化事件（创建/状态/断开/音量/显示名/图标/分组）。
    /// <para>线程模型与异常隔离同 <see cref="DeviceChanged"/>。</para>
    /// </summary>
    public event EventHandler<AudioSessionChangedEventArgs>? SessionChanged
    {
        add
        {
            _sessionChangedDispatcher.Add(value);

            if (value is not null)
            {
                IsEventSystemEnabled = true;
            }
        }

        remove => _sessionChangedDispatcher.Remove(value);
    }

    /// <summary>
    /// 事件订阅者抛出异常、或原生通知注册失败时触发。
    /// <para>
    /// 参数为（来源标识、异常/HRESULT 信息）。该事件的存在是为了让宿主能够发现
    /// "订阅者代码有 bug"或"通知注册降级"，同时不让异常破坏进程。
    /// <b>本事件自身的订阅者异常会被忽略</b>（避免递归失败）。
    /// </para>
    /// </summary>
    public event Action<string, Exception>? NotificationHandlerFaulted
    {
        add
        {
            _deviceChangedDispatcher.HandlerFaulted += value;
            _sessionChangedDispatcher.HandlerFaulted += value;
        }

        remove
        {
            _deviceChangedDispatcher.HandlerFaulted -= value;
            _sessionChangedDispatcher.HandlerFaulted -= value;
        }
    }

    /// <summary>
    /// 原生通知注册失败时触发，参数为原始 HRESULT。
    /// <para>该事件自身的订阅者异常会被忽略。</para>
    /// </summary>
    public event Action<int>? NotificationRegistrationFailed
    {
        add
        {
            _deviceChangedDispatcher.RegistrationFailed += value;
            _sessionChangedDispatcher.RegistrationFailed += value;
        }

        remove
        {
            _deviceChangedDispatcher.RegistrationFailed -= value;
            _sessionChangedDispatcher.RegistrationFailed -= value;
        }
    }

    /// <summary>
    /// 设备通知注册是否成功。
    /// <para>
    /// 注册失败不是致命错误：设备缓存仍由 TTL 兜底（退化为轮询语义），但
    /// <see cref="DeviceChanged"/> 不会触发。可通过本属性检测该降级状态。
    /// </para>
    /// </summary>
    public bool IsDeviceNotificationRegistered => _deviceNotifications.IsRegistered;

    /// <summary>
    /// 会话通知注册是否成功。
    /// </summary>
    public bool IsSessionNotificationRegistered { get; private set; }

    /// <summary>
    /// 事件系统是否已启用。
    /// <para>
    /// 默认<b>关闭</b>：只有第一次订阅 <see cref="DeviceChanged"/> 或 <see cref="SessionChanged"/>
    /// 时才注册会话通知与逐会话事件。设备通知则在构造时注册（用于缓存失效，几乎零成本）。
    /// </para>
    /// <para>
    /// 可以显式开启（即使没有订阅者），或关闭以回到纯 TTL 兜底行为。
    /// </para>
    /// </summary>
    public bool IsEventSystemEnabled
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _eventSystemEnabled;
            }
        }

        set
        {
            ThrowIfDisposed();

            lock (_lifecycleGate)
            {
                if (value == _eventSystemEnabled)
                {
                    return;
                }
            }

            if (value)
            {
                EnableSessionNotifications();
            }
            else
            {
                DisableSessionNotifications();
            }

            lock (_lifecycleGate)
            {
                _eventSystemEnabled = value;
            }
        }
    }

    private bool _eventSystemEnabled;

    /// <summary>
    /// 系统默认设备<b>写入</b>是否可用。
    /// <para>
    /// Milestone B.1 结论：<b>可用且默认启用</b>。写入路径使用半文档化 COM 接口
    /// <c>IPolicyConfig::SetDefaultEndpoint</c>（接口内第 11 个方法 / 绝对 vtable 槽位 13），
    /// 已在本验证环境（Windows 11 Build 26100 x64）真机验证通过。
    /// </para>
    /// <para>
    /// 唯一已知的运行约束：执行写入时<b>不能同时注册 <c>IMMNotificationClient</c></b>，
    /// 否则进程会在原生侧崩溃。本类已在写入窗口内自动临时注销设备通知、写入后立即恢复
    /// （见 <c>SuspendDeviceNotifications</c>），因此调用方无需关心。
    /// </para>
    /// <para>
    /// 该值表示"接口探测是否成功"：接口不可用时为 <see langword="false"/>，
    /// 此时调用 <see cref="SetDefaultDevice"/> 会抛
    /// <see cref="AudioRoutingNotSupportedException"/>。
    /// </para>
    /// </summary>
    public bool IsDefaultDeviceWriteEnabled
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _defaultDeviceWritesEnabled;
            }
        }
    }

    /// <summary>
    /// 当系统默认设备写入不可用时，说明原因（英文、中性）。可用时为 <see langword="null"/>。
    /// </summary>
    public string? DefaultDeviceWriteBlocker
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _defaultDeviceWritesEnabled ? null : _defaultDeviceWriteBlocker;
            }
        }
    }

    /// <summary>
    /// 重新探测系统默认设备写入能力（只读：激活接口 + 验证可用，不调用写入方法）。
    /// <para>构造函数已自动执行一次；接口在运行期不可用时此方法可刷新状态。</para>
    /// </summary>
    /// <returns>接口是否可用。</returns>
    public bool PrepareDefaultDeviceWrites()
    {
        ThrowIfDisposed();

        RoutingProbeResult probe = PolicyConfigBackend.Probe();

        lock (_lifecycleGate)
        {
            _defaultDeviceWritesEnabled = probe.IsSupported;
            _defaultDeviceWriteBlocker = probe.IsSupported ? null : probe.Reason;
        }

        return probe.IsSupported;
    }

    /// <summary>
    /// 临时禁用系统默认设备写入（用于诊断/受控环境；默认启用）。
    /// </summary>
    /// <param name="disabled">为 <see langword="true"/> 时禁用。</param>
    internal void SetDefaultDeviceWritesDisabledForTesting(bool disabled)
    {
        lock (_lifecycleGate)
        {
            _defaultDeviceWritesEnabled = !disabled;
            _defaultDeviceWriteBlocker = disabled
                ? "Writes were disabled explicitly through the diagnostics path."
                : null;
        }
    }

    private static void LogDiagnostic(string message)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("WINAUDIOROUTE_DIAG"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "winaudioroute-manager-diag.log"),
                $"{DateTime.UtcNow:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志失败不影响功能
        }
    }

    private void ThrowIfDefaultDeviceWritesNotEnabled()
    {
        if (IsDefaultDeviceWriteEnabled)
        {
            return;
        }

        throw new AudioRoutingNotSupportedException(
            $"System default device writes are not available on this system. {DefaultDeviceWriteBlocker}", hresult: 0);
    }

    // ------------------------------------------------------------------
    // 设备
    // ------------------------------------------------------------------

    /// <summary>
    /// 取得播放（输出）设备。
    /// </summary>
    /// <param name="states">要包含的设备状态；默认仅活动设备。</param>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存重新枚举。</param>
    /// <returns>设备列表（含每个设备的三个 Role 默认标记）。</returns>
    public IReadOnlyList<AudioDevice> GetPlaybackDevices(
        AudioDeviceState states = AudioDeviceState.Active,
        bool bypassCache = false)
    {
        ThrowIfDisposed();
        return _devices.GetPlaybackDevices(states, bypassCache);
    }

    /// <summary>
    /// 取得录音（输入）设备。
    /// </summary>
    /// <param name="states">要包含的设备状态；默认仅活动设备。</param>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存重新枚举。</param>
    /// <returns>设备列表。</returns>
    public IReadOnlyList<AudioDevice> GetRecordingDevices(
        AudioDeviceState states = AudioDeviceState.Active,
        bool bypassCache = false)
    {
        ThrowIfDisposed();
        return _devices.GetRecordingDevices(states, bypassCache);
    }

    /// <summary>
    /// 按方向取得设备。
    /// </summary>
    /// <param name="flow">数据流方向（<see cref="AudioDataFlow.All"/> 返回播放 + 录音）。</param>
    /// <param name="states">要包含的设备状态；默认仅活动设备。</param>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存重新枚举。</param>
    /// <returns>设备列表。</returns>
    public IReadOnlyList<AudioDevice> GetDevices(
        AudioDataFlow flow,
        AudioDeviceState states = AudioDeviceState.Active,
        bool bypassCache = false)
    {
        ThrowIfDisposed();
        return _devices.GetDevices(flow, states, bypassCache);
    }

    /// <summary>
    /// 取得指定方向与角色下的默认设备。
    /// <para>覆盖 6 种组合：Render/Capture × Console/Multimedia/Communications。</para>
    /// </summary>
    /// <param name="flow">数据流方向。</param>
    /// <param name="role">端点角色。</param>
    /// <param name="bypassCache">
    /// 保留参数（与其余查询 API 签名一致）；默认设备读取本身不走缓存。
    /// </param>
    /// <returns>
    /// 默认设备；该组合没有默认端点时返回 <see langword="null"/>
    /// （例如某些系统未配置"默认通信设备"）。
    /// </returns>
    public AudioDevice? GetDefaultDevice(
        AudioDataFlow flow,
        AudioRole role = AudioRole.Console,
        bool bypassCache = false)
    {
        ThrowIfDisposed();
        return _devices.GetDefaultDevice(flow, role, bypassCache);
    }

    /// <summary>
    /// 在活动播放设备中按 ID 或名称解析一个设备。
    /// </summary>
    /// <param name="idOrName">设备 ID（精确）或名称（先精确、再唯一子串，忽略大小写）。</param>
    /// <returns>唯一命中的设备。</returns>
    /// <exception cref="AudioDeviceNotFoundException">没有命中任何设备。</exception>
    /// <exception cref="AmbiguousAudioDeviceException">命中多个设备；异常中列出全部候选。</exception>
    public AudioDevice ResolvePlaybackDevice(string idOrName) =>
        ResolveDevice(AudioDataFlow.Render, idOrName);

    /// <summary>
    /// 在活动录音设备中按 ID 或名称解析一个设备。
    /// </summary>
    /// <param name="idOrName">设备 ID（精确）或名称（先精确、再唯一子串，忽略大小写）。</param>
    /// <returns>唯一命中的设备。</returns>
    /// <exception cref="AudioDeviceNotFoundException">没有命中任何设备。</exception>
    /// <exception cref="AmbiguousAudioDeviceException">命中多个设备；异常中列出全部候选。</exception>
    public AudioDevice ResolveRecordingDevice(string idOrName) =>
        ResolveDevice(AudioDataFlow.Capture, idOrName);

    /// <summary>
    /// 按方向解析设备（先 ID 精确，再名称精确，再唯一子串）。
    /// </summary>
    /// <param name="flow">在哪个方向的设备集合中解析。</param>
    /// <param name="idOrName">设备 ID 或名称。</param>
    /// <returns>唯一命中的设备。</returns>
    /// <exception cref="AudioDeviceNotFoundException">没有命中任何设备。</exception>
    /// <exception cref="AmbiguousAudioDeviceException">命中多个设备。</exception>
    public AudioDevice ResolveDevice(AudioDataFlow flow, string idOrName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrName);
        ThrowIfDisposed();

        IReadOnlyList<AudioDevice> devices = _devices.GetDevices(flow);
        DeviceResolutionResult resolution = DeviceResolver.Resolve(devices, idOrName);

        if (resolution.IsUnique)
        {
            return resolution.Device!;
        }

        if (resolution.IsAmbiguous)
        {
            throw new AmbiguousAudioDeviceException(
                $"Device query '{idOrName}' matched {resolution.Candidates.Count} devices in flow {flow}. " +
                "Specify a device ID or a more specific name.",
                resolution.Candidates)
            {
                Query = idOrName,
                Flow = flow,
            };
        }

        throw new AudioDeviceNotFoundException(
            $"No audio device in flow {flow} matched '{idOrName}'.")
        {
            Query = idOrName,
            Flow = flow,
        };
    }

    // ------------------------------------------------------------------
    // 会话枚举与解析
    // ------------------------------------------------------------------

    /// <summary>
    /// 枚举当前全部音频会话，<b>会话粒度</b>（同一 PID 的多个会话是多条记录）。
    /// </summary>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存重新枚举。</param>
    /// <returns>会话快照。</returns>
    public IReadOnlyList<AudioSession> GetSessions(bool bypassCache = false)
    {
        ThrowIfDisposed();
        return _sessions.GetSessions(bypassCache);
    }

    /// <summary>
    /// 枚举指定方向的音频会话。
    /// </summary>
    /// <param name="flow">数据流方向。</param>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存重新枚举。</param>
    /// <returns>该方向的会话快照。</returns>
    public IReadOnlyList<AudioSession> GetSessions(AudioDataFlow flow, bool bypassCache = false)
    {
        ThrowIfDisposed();
        return _sessions.GetSessions(flow, bypassCache);
    }

    /// <summary>
    /// 取得指定进程的全部音频会话。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存重新枚举。</param>
    /// <returns>该进程的会话（可能为空，不会抛异常）。</returns>
    public IReadOnlyList<AudioSession> GetSessionsForProcess(int processId, bool bypassCache = false)
    {
        ThrowIfDisposed();
        return _sessions.GetSessionsForProcess(processId, bypassCache);
    }

    /// <summary>
    /// 按进程名取得音频会话（支持 <c>chrome</c> / <c>chrome.exe</c> / <c>CHROME.EXE</c>，
    /// 忽略大小写）。
    /// </summary>
    /// <param name="processName">进程名。</param>
    /// <returns>会话集合；该名称可能对应多个不同 PID。</returns>
    /// <exception cref="AudioSessionNotFoundException">没有任何会话匹配该名称。</exception>
    /// <exception cref="AmbiguousAudioSessionException">
    /// 多个不同 PID 使用同一名称；异常中包含全部候选 PID 与会话。
    /// </exception>
    public IReadOnlyList<AudioSession> GetSessionsForProcess(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ThrowIfDisposed();

        return ResolveSessionsByProcessName(processName).Sessions;
    }

    // ------------------------------------------------------------------
    // 应用（会话）音量与静音
    // ------------------------------------------------------------------

    /// <summary>
    /// 读取应用的当前音量标量。
    /// <para><b>聚合规则</b>：同一 PID 有多个会话时，取<b>首个会话</b>（枚举顺序）的值。</para>
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <returns>0.0–1.0 的标量音量。</returns>
    /// <exception cref="AudioSessionNotFoundException">该 PID 没有任何音频会话。</exception>
    public float GetApplicationVolume(int processId)
    {
        ThrowIfDisposed();
        return _sessionVolumes.GetVolume(processId);
    }

    /// <summary>
    /// 读取应用的当前音量百分比（0–100）。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <returns>0–100 的百分比。</returns>
    /// <exception cref="AudioSessionNotFoundException">该 PID 没有任何音频会话。</exception>
    public int GetApplicationVolumePercent(int processId)
    {
        ThrowIfDisposed();
        return AudioVolume.ToPercent(_sessionVolumes.GetVolume(processId));
    }

    /// <summary>
    /// 设置应用音量：<b>写入该 PID 的全部会话</b>。越界值被钳制到 0.0–1.0。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <param name="volume">标量音量；越界值被钳制。</param>
    /// <returns>逐会话结果（可检测部分失败）。</returns>
    /// <exception cref="AudioSessionNotFoundException">该 PID 没有任何音频会话。</exception>
    public AudioOperationResult SetApplicationVolume(int processId, float volume)
    {
        ThrowIfDisposed();
        return _sessionVolumes.SetVolume(processId, volume);
    }

    /// <summary>
    /// 设置应用音量（百分比，0–100）。越界值被钳制。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <param name="percent">百分比；越界值被钳制。</param>
    /// <returns>逐会话结果。</returns>
    /// <exception cref="AudioSessionNotFoundException">该 PID 没有任何音频会话。</exception>
    public AudioOperationResult SetApplicationVolumePercent(int processId, int percent)
    {
        ThrowIfDisposed();
        return _sessionVolumes.SetVolume(processId, AudioVolume.FromPercent(percent));
    }

    /// <summary>
    /// 读取应用的静音状态。
    /// <para><b>聚合规则</b>：同一 PID 有多个会话时，取<b>首个会话</b>（枚举顺序）的值。</para>
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <returns>是否静音。</returns>
    /// <exception cref="AudioSessionNotFoundException">该 PID 没有任何音频会话。</exception>
    public bool GetApplicationMute(int processId)
    {
        ThrowIfDisposed();
        return _sessionVolumes.GetMute(processId);
    }

    /// <summary>
    /// 设置应用静音：<b>写入该 PID 的全部会话</b>。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <param name="muted">是否静音。</param>
    /// <returns>逐会话结果（可检测部分失败）。</returns>
    /// <exception cref="AudioSessionNotFoundException">该 PID 没有任何音频会话。</exception>
    public AudioOperationResult SetApplicationMute(int processId, bool muted)
    {
        ThrowIfDisposed();
        return _sessionVolumes.SetMute(processId, muted);
    }

    // ------------------------------------------------------------------
    // 设备级音量与静音
    // ------------------------------------------------------------------

    /// <summary>
    /// 读取设备音量标量（同时适用于播放与录音设备）。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <returns>0.0–1.0 的标量音量。</returns>
    /// <exception cref="AudioDeviceNotFoundException">设备已不存在（例如被拔出）。</exception>
    /// <exception cref="AudioOperationFailedException">设备不提供音量接口，或读取失败。</exception>
    public float GetDeviceVolume(AudioDevice device)
    {
        ThrowIfDisposed();
        return _deviceVolumes.GetVolume(device);
    }

    /// <summary>
    /// 设置设备音量。越界值被钳制到 0.0–1.0。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="volume">标量音量；越界值被钳制。</param>
    /// <exception cref="AudioDeviceNotFoundException">设备已不存在。</exception>
    /// <exception cref="AudioOperationFailedException">设备不提供音量接口，或写入失败。</exception>
    public void SetDeviceVolume(AudioDevice device, float volume)
    {
        ThrowIfDisposed();
        _deviceVolumes.SetVolume(device, volume);
    }

    /// <summary>
    /// 读取设备静音状态。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <returns>是否静音。</returns>
    /// <exception cref="AudioDeviceNotFoundException">设备已不存在。</exception>
    /// <exception cref="AudioOperationFailedException">设备不提供音量接口，或读取失败。</exception>
    public bool GetDeviceMute(AudioDevice device)
    {
        ThrowIfDisposed();
        return _deviceVolumes.GetMute(device);
    }

    /// <summary>
    /// 设置设备静音状态。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="muted">是否静音。</param>
    /// <exception cref="AudioDeviceNotFoundException">设备已不存在。</exception>
    /// <exception cref="AudioOperationFailedException">设备不提供音量接口，或写入失败。</exception>
    public void SetDeviceMute(AudioDevice device, bool muted)
    {
        ThrowIfDisposed();
        _deviceVolumes.SetMute(device, muted);
    }

    // ------------------------------------------------------------------
    // 系统默认设备
    // ------------------------------------------------------------------

    /// <summary>
    /// 把设备设为指定角色的系统默认设备。
    /// <para>
    /// <b>不需要传方向</b>：输出/输入由 <paramref name="device"/> 的端点 ID 决定，
    /// <paramref name="role"/> 才是 <c>SetDefaultEndpoint</c> 的第二参数。
    /// </para>
    /// <para>
    /// 对录音设备调用时，所设置的是该录音端点在该角色下的默认设备
    /// （Windows 的"默认通信设备"常见用法）。
    /// </para>
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="role">端点角色（Console / Multimedia / Communications）。</param>
    /// <returns>结果（单目标，成功时为 <see cref="AudioOperationResult.IsSuccess"/>）。</returns>
    /// <exception cref="AudioRoutingNotSupportedException">
    /// 写入路径未启用（默认），或系统默认设备 API 不可用。默认情况下本方法<b>始终</b>抛此异常而不触碰原生调用，
    /// 因为该原生调用可能导致无法捕获的进程崩溃——详见 <see cref="PrepareDefaultDeviceWrites"/>。
    /// </exception>
    /// <exception cref="AudioOperationFailedException">COM 调用失败。</exception>
    public AudioOperationResult SetDefaultDevice(AudioDevice device, AudioRole role)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();
        ThrowIfDefaultDeviceWritesNotEnabled();

        // 已知问题规避：注册了 IMMNotificationClient 时执行默认设备变更会导致 native 崩溃。
        // 因此在进程级串行窗口内临时注销设备通知，写入完成后恢复。
        return WithDefaultDeviceWriteGate(() => _policyConfig.SetDefaultEndpoint(device.Id, role));
    }

    /// <summary>
    /// 依次把设备设为 Console / Multimedia / Communications 三个角色的默认设备。
    /// <para>
    /// <b>不假设一次调用即可覆盖全部角色</b>：实现显式按顺序调用三次，
    /// 并返回逐角色结果以暴露部分失败。
    /// </para>
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <returns>逐角色结果（<see cref="AudioOperationResult.Total"/> 为 3）。</returns>
    /// <exception cref="AudioRoutingNotSupportedException">
    /// 写入路径未启用（默认）或系统默认设备 API 不可用。
    /// </exception>
    public AudioOperationResult SetDefaultDeviceForAllRoles(AudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();
        ThrowIfDefaultDeviceWritesNotEnabled();

        return WithDefaultDeviceWriteGate(() => _policyConfig.SetDefaultEndpointForAllRoles(device.Id));
    }

    /// <summary>
    /// 进程级串行化系统默认设备写入。
    /// <para>
    /// <b>为什么必须是静态的</b>：写入期间必须保证<b>进程内没有任何已注册的
    /// <c>IMMNotificationClient</c></b>。若两个 <see cref="WindowsAudioManager"/> 实例并行写入，
    /// 一个实例注销通知时另一个仍持有注册，就会触发已知的原生崩溃。
    /// 因此该窗口必须在整个进程内串行化，而不只是单个实例内部。
    /// </para>
    /// </summary>
    private static readonly object DefaultDeviceWriteGate = new();

    /// <summary>
    /// 在默认设备写入期间临时注销 <c>IMMNotificationClient</c>。
    /// <para>
    /// <b>为什么需要</b>：实测发现"已注册 <c>IMMNotificationClient</c>"与"执行默认设备变更"
    /// 同时发生时，进程会在原生侧崩溃（且崩溃发生在托管回调代码之前 —— 回调日志为空）。
    /// 因此写入窗口内临时注销通知，写入完成后立即恢复。
    /// </para>
    /// </summary>
    /// <returns>是否确实执行了注销（用于决定是否需要恢复）。</returns>
    private bool SuspendDeviceNotifications()
    {
        if (_noDeviceNotificationForDiagnostics || !_deviceNotifications.IsRegistered)
        {
            return false;
        }

        _deviceNotifications.Unregister();
        return true;
    }

    /// <summary>
    /// 在"进程级串行化 + 临时注销设备通知"的窗口内执行一次默认设备写入。
    /// </summary>
    /// <typeparam name="T">结果类型。</typeparam>
    /// <param name="write">实际的写入动作。</param>
    /// <returns>写入结果。</returns>
    private T WithDefaultDeviceWriteGate<T>(Func<T> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        lock (DefaultDeviceWriteGate)
        {
            bool suspended = SuspendDeviceNotifications();

            try
            {
                LogDiagnostic($"default-device write begin (notificationSuspended={suspended})");
                T result = write();
                LogDiagnostic("default-device write end");
                return result;
            }
            finally
            {
                ResumeDeviceNotifications(suspended);
            }
        }
    }

    /// <summary>恢复被 <see cref="SuspendDeviceNotifications"/> 临时注销的设备通知。</summary>
    /// <param name="wasSuspended">注销是否真的发生过。</param>
    private void ResumeDeviceNotifications(bool wasSuspended)
    {
        if (!wasSuspended)
        {
            return;
        }

        try
        {
            _deviceNotifications.Register();
        }
        catch
        {
            // 恢复失败退化为 TTL 兜底，不影响写入结果
        }
    }

    // ------------------------------------------------------------------
    // 按应用音频路由（per-app routing）
    // ------------------------------------------------------------------

    /// <summary>
    /// 当前系统是否支持按应用音频路由。
    /// <para>
    /// 首次读取时执行<b>只读</b>能力探测（激活内部策略类 + 验证 vtable 槽位），结果被缓存。
    /// 探测不修改任何应用的路由设置。
    /// </para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">实例已释放。</exception>
    public bool IsPerAppRoutingSupported
    {
        get
        {
            ThrowIfDisposed();
            return _routing.Capability.IsSupported;
        }
    }

    /// <summary>
    /// 取得按应用路由的完整能力状态（含不支持的原因与 HRESULT），用于诊断。
    /// </summary>
    /// <exception cref="ObjectDisposedException">实例已释放。</exception>
    public AudioRoutingCapability RoutingCapability
    {
        get
        {
            ThrowIfDisposed();
            return _routing.Capability;
        }
    }

    /// <summary>
    /// 读取应用当前持久化的<b>输出</b>设备。
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <returns>
    /// 已持久化的输出设备；未设置（即跟随系统默认）时返回 <see langword="null"/>。
    /// </returns>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    public AudioDevice? GetApplicationOutput(int processId)
    {
        ThrowIfDisposed();
        return _routing.GetPersistedEndpoint(processId, AudioDataFlow.Render);
    }

    /// <summary>
    /// 读取应用当前持久化的<b>输入</b>设备。
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <returns>
    /// 已持久化的输入设备；未设置（即跟随系统默认）时返回 <see langword="null"/>。
    /// </returns>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    public AudioDevice? GetApplicationInput(int processId)
    {
        ThrowIfDisposed();
        return _routing.GetPersistedEndpoint(processId, AudioDataFlow.Capture);
    }

    /// <summary>
    /// 把应用的<b>输出</b>设备持久化为指定设备。
    /// <para>
    /// <b><paramref name="device"/> 为 <see langword="null"/> 时表示"删除该应用的持久化输出端点"，
    /// 即跟随系统默认设备</b>——这是 Windows 后端的真实语义，不是本库的约定。
    /// 不喜欢 null 语义的调用方请使用 <see cref="ResetApplicationOutput"/>。
    /// </para>
    /// <para>
    /// <b>需要未公开的 Windows 内部 API</b>：见 <see cref="AudioRoutingCapability"/> 与文档说明。
    /// 不支持时会抛出 <see cref="AudioRoutingNotSupportedException"/>（携带原始 HRESULT），
    /// 绝不静默失败。写入后立即回读验证。
    /// </para>
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <param name="device">目标播放设备；<see langword="null"/> 表示跟随系统默认。</param>
    /// <returns>逐项结果。</returns>
    /// <exception cref="ArgumentException"><paramref name="device"/> 不是播放设备。</exception>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    /// <exception cref="AudioOperationFailedException">后端调用失败。</exception>
    public AudioOperationResult SetApplicationOutput(int processId, AudioDevice? device)
    {
        ThrowIfDisposed();
        return _routing.SetPersistedEndpoint(processId, AudioDataFlow.Render, device);
    }

    /// <summary>
    /// 把应用的<b>输入</b>设备持久化为指定设备。
    /// <para>
    /// <b><paramref name="device"/> 为 <see langword="null"/> 时表示"删除该应用的持久化输入端点"，
    /// 即跟随系统默认设备。</b>不喜欢 null 语义的调用方请使用 <see cref="ResetApplicationInput"/>。
    /// </para>
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <param name="device">目标录音设备；<see langword="null"/> 表示跟随系统默认。</param>
    /// <returns>逐项结果。</returns>
    /// <exception cref="ArgumentException"><paramref name="device"/> 不是录音设备。</exception>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    /// <exception cref="AudioOperationFailedException">后端调用失败。</exception>
    public AudioOperationResult SetApplicationInput(int processId, AudioDevice? device)
    {
        ThrowIfDisposed();
        return _routing.SetPersistedEndpoint(processId, AudioDataFlow.Capture, device);
    }

    /// <summary>
    /// 清除应用的持久化<b>输出</b>路由，使其跟随系统默认设备。
    /// <para>等价于 <c>SetApplicationOutput(processId, null)</c>，但语义更明确。</para>
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <returns>逐项结果。</returns>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    public AudioOperationResult ResetApplicationOutput(int processId)
    {
        ThrowIfDisposed();
        return _routing.SetPersistedEndpoint(processId, AudioDataFlow.Render, device: null);
    }

    /// <summary>
    /// 清除应用的持久化<b>输入</b>路由，使其跟随系统默认设备。
    /// <para>等价于 <c>SetApplicationInput(processId, null)</c>，但语义更明确。</para>
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <returns>逐项结果。</returns>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    public AudioOperationResult ResetApplicationInput(int processId)
    {
        ThrowIfDisposed();
        return _routing.SetPersistedEndpoint(processId, AudioDataFlow.Capture, device: null);
    }

    /// <summary>
    /// 同时清除应用的输出与输入持久化路由，使其完全跟随系统默认设备。
    /// <para>
    /// 输入与输出都会被显式处理；任一路由清除失败都会出现在结果的
    /// <see cref="AudioOperationResult.Failures"/> 中（<see cref="AudioOperationResult.Total"/> 为 2）。
    /// 后端整体不可用时抛 <see cref="AudioRoutingNotSupportedException"/>。
    /// </para>
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <returns>逐项结果（输出 + 输入）。</returns>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    public AudioOperationResult ResetApplicationRouting(int processId)
    {
        ThrowIfDisposed();

        AudioOperationResult output;
        try
        {
            output = _routing.SetPersistedEndpoint(processId, AudioDataFlow.Render, device: null);
        }
        catch (AudioRoutingNotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            output = AudioOperationResult.AllFailed(
                [new AudioOperationFailure($"process {processId} / output", ex.HResult, ex.Message)]);
        }

        AudioOperationResult input;
        try
        {
            input = _routing.SetPersistedEndpoint(processId, AudioDataFlow.Capture, device: null);
        }
        catch (AudioRoutingNotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            input = AudioOperationResult.AllFailed(
                [new AudioOperationFailure($"process {processId} / input", ex.HResult, ex.Message)]);
        }

        int succeeded = (output.IsSuccess ? 1 : 0) + (input.IsSuccess ? 1 : 0);

        List<AudioOperationFailure> failures = [];
        failures.AddRange(output.Failures);
        failures.AddRange(input.Failures);

        if (failures.Count == 0)
        {
            return AudioOperationResult.AllSucceeded(succeeded);
        }

        return succeeded > 0
            ? AudioOperationResult.Partial(succeeded, failures)
            : AudioOperationResult.AllFailed(failures);
    }

    // ------------------------------------------------------------------
    // 按应用音频路由：进程名便捷重载（复用 SessionResolver）
    // ------------------------------------------------------------------

    /// <summary>
    /// 按<b>进程名</b>读取应用的持久化输出设备。
    /// </summary>
    /// <param name="processName">进程名（<c>chrome</c> / <c>chrome.exe</c> 等价，忽略大小写）。</param>
    /// <returns>已持久化的输出设备；未设置时返回 <see langword="null"/>。</returns>
    /// <exception cref="AudioSessionNotFoundException">没有任何会话匹配该名称。</exception>
    /// <exception cref="AmbiguousAudioSessionException">多个不同 PID 使用同一名称。</exception>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    public AudioDevice? GetApplicationOutput(string processName)
    {
        ThrowIfDisposed();
        return _routing.GetPersistedEndpoint(ResolveUniqueProcessId(processName), AudioDataFlow.Render);
    }

    /// <summary>
    /// 按<b>进程名</b>读取应用的持久化输入设备。
    /// </summary>
    /// <param name="processName">进程名（<c>chrome</c> / <c>chrome.exe</c> 等价，忽略大小写）。</param>
    /// <returns>已持久化的输入设备；未设置时返回 <see langword="null"/>。</returns>
    /// <exception cref="AudioSessionNotFoundException">没有任何会话匹配该名称。</exception>
    /// <exception cref="AmbiguousAudioSessionException">多个不同 PID 使用同一名称。</exception>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    public AudioDevice? GetApplicationInput(string processName)
    {
        ThrowIfDisposed();
        return _routing.GetPersistedEndpoint(ResolveUniqueProcessId(processName), AudioDataFlow.Capture);
    }

    /// <summary>
    /// 按<b>进程名</b>设置应用的持久化输出设备。
    /// <para>
    /// 名称解析复用 <c>SessionResolver</c>：<c>chrome</c> / <c>chrome.exe</c> / <c>CHROME.EXE</c> 等价。
    /// <b>命中多个不同 PID 时抛出 <see cref="AmbiguousAudioSessionException"/>，绝不自动对全部同名进程生效。</b>
    /// </para>
    /// </summary>
    /// <param name="processName">进程名。</param>
    /// <param name="device">目标播放设备；<see langword="null"/> 表示跟随系统默认。</param>
    /// <returns>逐项结果。</returns>
    /// <exception cref="AudioSessionNotFoundException">没有任何会话匹配该名称。</exception>
    /// <exception cref="AmbiguousAudioSessionException">多个不同 PID 使用同一名称。</exception>
    /// <exception cref="ArgumentException"><paramref name="device"/> 不是播放设备。</exception>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    public AudioOperationResult SetApplicationOutput(string processName, AudioDevice? device)
    {
        ThrowIfDisposed();
        return _routing.SetPersistedEndpoint(ResolveUniqueProcessId(processName), AudioDataFlow.Render, device);
    }

    /// <summary>
    /// 按<b>进程名</b>设置应用的持久化输入设备。
    /// <para>
    /// 名称解析复用 <c>SessionResolver</c>；<b>命中多个不同 PID 时抛出
    /// <see cref="AmbiguousAudioSessionException"/>，绝不自动对全部同名进程生效。</b>
    /// </para>
    /// </summary>
    /// <param name="processName">进程名。</param>
    /// <param name="device">目标录音设备；<see langword="null"/> 表示跟随系统默认。</param>
    /// <returns>逐项结果。</returns>
    /// <exception cref="AudioSessionNotFoundException">没有任何会话匹配该名称。</exception>
    /// <exception cref="AmbiguousAudioSessionException">多个不同 PID 使用同一名称。</exception>
    /// <exception cref="ArgumentException"><paramref name="device"/> 不是录音设备。</exception>
    /// <exception cref="AudioRoutingNotSupportedException">当前系统不支持按应用路由。</exception>
    public AudioOperationResult SetApplicationInput(string processName, AudioDevice? device)
    {
        ThrowIfDisposed();
        return _routing.SetPersistedEndpoint(ResolveUniqueProcessId(processName), AudioDataFlow.Capture, device);
    }

    /// <summary>
    /// 把进程名解析为唯一的 PID；不唯一或不存在时抛出对应异常。
    /// </summary>
    private int ResolveUniqueProcessId(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        SessionResolutionResult resolution = ResolveSessionsByProcessName(processName);
        return resolution.ProcessIds[0];
    }

    /// <summary>
    /// 注入一个会话变化事件（仅用于测试：验证"事件 → 缓存失效 → 重新枚举"的完整链路，
    /// 无需真的制造系统会话事件）。
    /// </summary>
    /// <param name="args">事件参数。</param>
    internal void RaiseSessionChangedForTesting(AudioSessionChangedEventArgs args) =>
        OnSessionNotification(args);

    /// <summary>
    /// 注入一个设备变化事件（仅用于测试，同 <see cref="RaiseSessionChangedForTesting"/>）。
    /// </summary>
    /// <param name="args">事件参数。</param>
    internal void RaiseDeviceChangedForTesting(AudioDeviceChangedEventArgs args) =>
        OnDeviceNotification(args);

    // ------------------------------------------------------------------
    // 释放
    // ------------------------------------------------------------------

    /// <summary>
    /// 释放本实例持有的全部资源。幂等。
    /// <para>
    /// 释放顺序（不可颠倒）：
    /// <list type="number">
    ///   <item><description>标记已释放 → 后续回调不再分发事件。</description></item>
    ///   <item><description>注销设备通知（<c>UnregisterEndpointNotificationCallback</c>）。</description></item>
    ///   <item><description>注销会话通知与会话事件，并释放全部会话 COM 引用。</description></item>
    ///   <item><description>释放按应用路由的策略工厂引用。</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _eventSystemEnabled = false;
        }

        // 1) 先撤销原生回调注册，避免释放过程中仍有回调进入
        _deviceNotifications.Dispose();
        IsSessionNotificationRegistered = false;

        // 2) 会话缓存是唯一长期持有会话 COM 引用的地方：释放它会释放全部句柄并撤销事件注册
        _sessions.Dispose();

        // 3) 路由后端持有进程内复用的策略工厂指针（裸 COM 引用）
        _routing.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>终结器：调用方忘记 <see cref="Dispose"/> 时的兜底。</summary>
    ~WindowsAudioManager() => Dispose();

    /// <summary>在公开操作前确认实例未被释放。</summary>
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>按进程名解析会话并处理歧义/未命中的统一路径。</summary>
    private SessionResolutionResult ResolveSessionsByProcessName(string processName)
    {
        IReadOnlyList<AudioSession> all = _sessions.GetSessions();
        SessionResolutionResult resolution = SessionResolver.ResolveByProcessName(all, processName);

        if (resolution.IsAmbiguous)
        {
            throw new AmbiguousAudioSessionException(
                $"Process name '{processName}' matched {resolution.ProcessIds.Count} distinct processes " +
                $"({resolution.Sessions.Count} sessions). Use a process ID instead.",
                resolution.ProcessIds)
            {
                Query = processName,
                CandidateSessionCount = resolution.Sessions.Count,
            };
        }

        if (resolution.IsNotFound)
        {
            throw new AudioSessionNotFoundException(
                $"No audio sessions were found for process name '{processName}'.")
            {
                Query = processName,
            };
        }

        return resolution;
    }

    // ------------------------------------------------------------------
    // 事件基础设施
    // ------------------------------------------------------------------

    /// <summary>
    /// 设备通知回调（在 COM 回调线程上执行）。
    /// <para>
    /// <b>只做两件事</b>：使设备/会话缓存失效、分发托管事件。
    /// 不在此处枚举设备或会话、不做路由写入、不阻塞。
    /// </para>
    /// </summary>
    private void OnDeviceNotification(AudioDeviceChangedEventArgs args)
    {
        // 缓存失效必须最先做，且不得因为已释放而抛异常
        try
        {
            _devices.InvalidateCache();

            // 设备变化可能改变会话到设备的映射（尤其设备移除），因此会话快照同样失效
            _sessions.Invalidate();
        }
        catch (ObjectDisposedException)
        {
            // 释放与回调竞争：安全忽略
        }

        _deviceChangedDispatcher.Raise(this, args);
    }

    /// <summary>会话通知回调（在 COM 回调线程上执行）。</summary>
    private void OnSessionNotification(AudioSessionChangedEventArgs args)
    {
        try
        {
            // 会话创建/断开/状态变化都会让会话快照过期；音量变化只影响值，不影响成员集合，
            // 因而同样失效以保持"读到的值是最新的"这一契约。
            _sessions.Invalidate();
        }
        catch (ObjectDisposedException)
        {
            // 释放与回调竞争：安全忽略
        }

        _sessionChangedDispatcher.Raise(this, args);
    }

    private void OnNotificationRegistrationFailed(int hresult)
    {
        // 注册失败不抛异常：退化为 TTL 兜底，并通过 NotificationRegistrationFailed 暴露原因
        _deviceChangedDispatcher.RaiseRegistrationFailure(hresult);
        _sessionChangedDispatcher.RaiseRegistrationFailure(hresult);
    }

    private void EnableSessionNotifications()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || IsSessionNotificationRegistered)
            {
                return;
            }
        }

        try
        {
            bool registered = _sessions.EnableEventNotifications(OnSessionNotification, OnNotificationRegistrationFailed);
            IsSessionNotificationRegistered = registered;
        }
        catch (ObjectDisposedException)
        {
            IsSessionNotificationRegistered = false;
        }
    }

    private void DisableSessionNotifications()
    {
        lock (_lifecycleGate)
        {
            if (!IsSessionNotificationRegistered)
            {
                return;
            }
        }

        // 由 SessionService 统一撤销会话通知与逐会话事件注册
        _sessions.DisableEventNotifications();
        IsSessionNotificationRegistered = false;
    }
}

using System.Runtime.InteropServices;
using WinAudioRoute.Events;
using WinAudioRoute.Interop;

namespace WinAudioRoute.Internal;

/// <summary>
/// 设备变化通知客户端（<c>IMMNotificationClient</c> 的托管实现）。
/// <para>
/// <b>生命周期铁律</b>：
/// <list type="bullet">
///   <item><description>原生侧只持有<b>裸接口指针</b>，不持有托管引用。
///   因此本对象必须被 <see cref="WindowsAudioManager"/> 强引用，直到注销成功为止；
///   否则 GC 回收它会让原生侧指针悬空并导致进程崩溃。</description></item>
///   <item><description>注册与注销严格配对，且<b>幂等</b>。</description></item>
///   <item><description>每个回调方法体都在 <c>try/catch</c> 内，异常绝不穿越 COM 边界。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>回调只做最少的事</b>：捕获参数、构造事件参数、分发托管事件。
/// 不在回调内枚举设备/会话、不做路由写入、不阻塞。
/// </para>
/// <para>
/// <b>诊断开关</b>：<see cref="DisabledCallbacksVariable"/> 可让指定回调直接返回，
/// 用于隔离"某个回调的封送有问题导致进程崩溃"这类问题。
/// </para>
/// </summary>
internal sealed class DeviceNotificationClient : IMMNotificationClient, IDisposable
{
    /// <summary>
    /// 诊断开关：逗号分隔的回调名，命中的回调不执行任何托管代码（只返回 <c>S_OK</c>）。
    /// 可用值：<c>state</c>、<c>added</c>、<c>removed</c>、<c>default</c>、<c>property</c>。
    /// </summary>
    internal const string DisabledCallbacksVariable = "WINAUDIOROUTE_DISABLE_DEVICE_CALLBACKS";

    private readonly object _gate = new();
    private readonly Action<AudioDeviceChangedEventArgs> _onChanged;
    private readonly Action<int> _onRegistrationFailed;
    private readonly HashSet<string> _disabledCallbacks;

    private IMMDeviceEnumerator? _registeringEnumerator;
    private bool _isRegistered;
    private bool _disposed;

    /// <summary>创建客户端。</summary>
    /// <param name="onChanged">事件回调（由 <see cref="SafeEventDispatcher{TEventArgs}"/> 包装，异常已被隔离）。</param>
    /// <param name="onRegistrationFailed">注册失败回调（参数为 HRESULT）。</param>
    public DeviceNotificationClient(
        Action<AudioDeviceChangedEventArgs> onChanged,
        Action<int> onRegistrationFailed)
    {
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onRegistrationFailed = onRegistrationFailed ?? throw new ArgumentNullException(nameof(onRegistrationFailed));

        string raw = Environment.GetEnvironmentVariable(DisabledCallbacksVariable) ?? string.Empty;
        _disabledCallbacks = new HashSet<string>(
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>该回调是否被诊断开关禁用。</summary>
    /// <param name="name">回调名。</param>
    /// <returns>被禁用返回 <see langword="true"/>。</returns>
    private bool IsDisabled(string name) => _disabledCallbacks.Contains(name);

    /// <summary>当前是否已成功注册。</summary>
    public bool IsRegistered
    {
        get
        {
            lock (_gate)
            {
                return _isRegistered;
            }
        }
    }

    /// <summary>
    /// 注册设备通知。
    /// <para>注册失败时不留"半注册"状态：内部状态保持未注册，并回报 HRESULT。</para>
    /// </summary>
    public void Register()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_isRegistered)
            {
                // 重复注册是幂等的：不重复调用原生 API
                return;
            }

            IMMDeviceEnumerator? enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                int hr = enumerator.RegisterEndpointNotificationCallback(this);

                if (hr < 0)
                {
                    _onRegistrationFailed(hr);
                    return;
                }

                _registeringEnumerator = enumerator;
                _isRegistered = true;
                enumerator = null; // 成功：保留枚举器引用以确保注册存活，不在此释放
            }
            catch (Exception ex)
            {
                _onRegistrationFailed(ex.HResult);
            }
            finally
            {
                // 失败路径：立即释放临时枚举器，避免泄漏
                if (enumerator is not null)
                {
                    ReleaseEnumerator(enumerator);
                }
            }
        }
    }

    /// <summary>
    /// 注销设备通知。幂等；未注册时不做任何事。
    /// </summary>
    public void Unregister()
    {
        IMMDeviceEnumerator? held;
        lock (_gate)
        {
            if (!_isRegistered || _registeringEnumerator is null)
            {
                return;
            }

            held = _registeringEnumerator;
            _registeringEnumerator = null;
            _isRegistered = false;
        }

        // 在锁外做原生调用，避免持有锁进入 COM
        try
        {
            _ = held.UnregisterEndpointNotificationCallback(this);
        }
        catch
        {
            // 注销失败不得抛出；引用保持到进程结束，宁可不释放也不能悬空
            return;
        }

        ReleaseEnumerator(held);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Unregister();
    }

    // ------------------------------------------------------------------
    // IMMNotificationClient 实现（vtable 顺序与头文件一致）
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public int OnDeviceStateChanged(string deviceId, DeviceState newState)
    {

        if (IsDisabled("state"))
        {
            return 0;
        }

        try
        {
            if (IsDisposed())
            {
                return 0;
            }

            AudioDeviceState? state = TryMapState(newState);

            _onChanged(new AudioDeviceChangedEventArgs(
                AudioDeviceChangeKind.StateChanged,
                Normalize(deviceId),
                state: state));
        }
        catch
        {
            // 异常绝不穿越 COM 边界
        }

        return 0;
    }

    /// <inheritdoc />
    public int OnDeviceAdded(string deviceId)
    {

        if (IsDisabled("added"))
        {
            return 0;
        }

        try
        {
            if (!IsDisposed())
            {
                _onChanged(new AudioDeviceChangedEventArgs(
                    AudioDeviceChangeKind.Added, Normalize(deviceId)));
            }
        }
        catch
        {
        }

        return 0;
    }

    /// <inheritdoc />
    public int OnDeviceRemoved(string deviceId)
    {

        if (IsDisabled("removed"))
        {
            return 0;
        }

        try
        {
            if (!IsDisposed())
            {
                _onChanged(new AudioDeviceChangedEventArgs(
                    AudioDeviceChangeKind.Removed, Normalize(deviceId)));
            }
        }
        catch
        {
        }

        return 0;
    }

    /// <inheritdoc />
    public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
    {

        if (IsDisabled("default"))
        {
            return 0;
        }

        try
        {
            if (!IsDisposed())
            {
                _onChanged(new AudioDeviceChangedEventArgs(
                    AudioDeviceChangeKind.DefaultChanged,
                    Normalize(defaultDeviceId),
                    flow: TryMapFlow(flow),
                    role: TryMapRole(role)));
            }
        }
        catch
        {
        }

        return 0;
    }

    /// <inheritdoc />
    public int OnPropertyValueChanged(string deviceId, InteropVariant properties)
    {

        if (IsDisabled("property"))
        {
            return 0;
        }

        try
        {
            if (!IsDisposed())
            {
                // 属性内容不解析：本库只把它当作"设备属性已变化"的信号使用
                _onChanged(new AudioDeviceChangedEventArgs(
                    AudioDeviceChangeKind.PropertyChanged, Normalize(deviceId)));
            }
        }
        catch
        {
        }

        return 0;
    }

    // ------------------------------------------------------------------
    // 映射辅助：未知值一律返回 null，绝不猜测
    // ------------------------------------------------------------------

    private static string Normalize(string? deviceId) => deviceId ?? string.Empty;

    private static AudioDataFlow? TryMapFlow(EDataFlow flow) => flow switch
    {
        EDataFlow.eRender => AudioDataFlow.Render,
        EDataFlow.eCapture => AudioDataFlow.Capture,
        _ => null,
    };

    private static AudioRole? TryMapRole(ERole role) => role switch
    {
        ERole.eConsole => AudioRole.Console,
        ERole.eMultimedia => AudioRole.Multimedia,
        ERole.eCommunications => AudioRole.Communications,
        _ => null,
    };

    private static AudioDeviceState? TryMapState(DeviceState state)
    {
        var mapped = (AudioDeviceState)(uint)state;

        // 只接受已知的四个状态位的任意组合；其余情况不猜测
        const AudioDeviceState valid =
            AudioDeviceState.Active | AudioDeviceState.Disabled
            | AudioDeviceState.NotPresent | AudioDeviceState.Unplugged;

        return (mapped & ~valid) != 0 || mapped == AudioDeviceState.None
            ? null
            : mapped;
    }

    private static void ReleaseEnumerator(IMMDeviceEnumerator enumerator)
    {
        try
        {
            Marshal.ReleaseComObject(enumerator);
        }
        catch
        {
            // 忽略释放异常
        }
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }
}

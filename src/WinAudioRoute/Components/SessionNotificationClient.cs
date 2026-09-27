using System.Runtime.InteropServices;
using WinAudioRoute.Events;
using WinAudioRoute.Interop;

namespace WinAudioRoute.Internal;

/// <summary>
/// 新会话通知客户端（<c>IAudioSessionNotification</c> 的托管实现）。
/// <para>
/// 与 <see cref="DeviceNotificationClient"/> 相同的生命周期铁律：托管强引用 + 配对注销 +
/// 异常绝不穿越 COM 边界。
/// </para>
/// </summary>
internal sealed class SessionNotificationClient : IAudioSessionNotification, IDisposable
{
    private readonly object _gate = new();
    private readonly Action<AudioSessionChangedEventArgs> _onChanged;
    private readonly Action<int> _onRegistrationFailed;

    private IAudioSessionManager2? _registeringManager;
    private bool _isRegistered;
    private bool _disposed;

    /// <summary>创建客户端。</summary>
    /// <param name="onChanged">事件回调。</param>
    /// <param name="onRegistrationFailed">注册失败回调（参数为 HRESULT）。</param>
    public SessionNotificationClient(
        Action<AudioSessionChangedEventArgs> onChanged,
        Action<int> onRegistrationFailed)
    {
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onRegistrationFailed = onRegistrationFailed ?? throw new ArgumentNullException(nameof(onRegistrationFailed));
    }

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
    /// 在指定设备上注册新会话通知。注册失败时不留半注册状态。
    /// <para>
    /// 调用方传入的设备对象由本方法负责在结束后释放（无论成功或失败）。
    /// </para>
    /// </summary>
    /// <param name="deviceId">用于打开会话管理器的设备 ID。</param>
    /// <returns>注册是否成功。</returns>
    public bool Register(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_isRegistered)
            {
                return true;
            }
        }

        IMMDevice? device = null;
        object? managerObject = null;

        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                if (enumerator.GetDevice(deviceId, out IMMDevice found) < 0 || found is null)
                {
                    _onRegistrationFailed(0);
                    return false;
                }

                device = found;
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }

            Guid iid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"); // IAudioSessionManager2
            int hr = device.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out managerObject);

            if (hr < 0 || managerObject is null)
            {
                _onRegistrationFailed(hr);
                return false;
            }

            var manager = (IAudioSessionManager2)managerObject;
            hr = manager.RegisterSessionNotification(this);

            if (hr < 0)
            {
                _onRegistrationFailed(hr);
                return false;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    // 注册过程中被释放：立即撤销，避免遗留注册
                    try
                    {
                        manager.UnregisterSessionNotification(this);
                    }
                    catch
                    {
                        // 忽略
                    }

                    return false;
                }

                _registeringManager = manager;
                _isRegistered = true;
                managerObject = null; // 成功：保留管理器引用
            }

            return true;
        }
        catch (Exception ex)
        {
            _onRegistrationFailed(ex.HResult);
            return false;
        }
        finally
        {
            if (managerObject is not null)
            {
                Release(managerObject);
            }

            if (device is not null)
            {
                Release(device);
            }
        }
    }

    /// <summary>注销新会话通知。幂等。</summary>
    public void Unregister()
    {
        IAudioSessionManager2? manager;
        lock (_gate)
        {
            if (!_isRegistered || _registeringManager is null)
            {
                return;
            }

            manager = _registeringManager;
            _registeringManager = null;
            _isRegistered = false;
        }

        try
        {
            _ = manager.UnregisterSessionNotification(this);
        }
        catch
        {
            // 注销失败时保留引用直到进程结束，宁可不释放也不能悬空
            return;
        }

        Release(manager);
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

    /// <inheritdoc />
    public int OnSessionCreated(object newSession)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return 0;
                }
            }

            if (newSession is not IAudioSessionControl2 session)
            {
                return 0;
            }

            int processId = 0;
            _ = session.GetProcessId(out uint rawPid);
            processId = (int)rawPid;

            string identifier = ReadString(session.GetSessionIdentifier);
            string instanceIdentifier = ReadString(session.GetSessionInstanceIdentifier);

            _onChanged(new AudioSessionChangedEventArgs(
                AudioSessionChangeKind.Created,
                identifier,
                instanceIdentifier,
                processId));
        }
        catch
        {
            // 异常绝不穿越 COM 边界
        }

        return 0;
    }

    private static string ReadString(ReadSessionString read)
    {
        try
        {
            return read(out string value) >= 0 && value is not null ? value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void Release(object comObject)
    {
        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // 忽略释放异常
        }
    }

    private delegate int ReadSessionString(out string value);
}

/// <summary>
/// 单个会话的事件客户端（<c>IAudioSessionEvents</c> 的托管实现）。
/// <para>
/// 由 <see cref="AudioSessionHandle"/> 拥有并强引用，句柄释放时撤销注册。
/// </para>
/// </summary>
internal sealed class SessionEventClient : IAudioSessionEvents
{
    private readonly object _gate = new();
    private readonly Action<AudioSessionChangedEventArgs> _onChanged;
    private readonly int _processId;

    private bool _disposed;

    /// <summary>创建客户端。</summary>
    /// <param name="processId">所属进程 ID（用于事件参数）。</param>
    /// <param name="sessionIdentifier">会话标识符。</param>
    /// <param name="sessionInstanceIdentifier">会话实例标识符。</param>
    /// <param name="onChanged">事件回调。</param>
    public SessionEventClient(
        int processId,
        string sessionIdentifier,
        string sessionInstanceIdentifier,
        Action<AudioSessionChangedEventArgs> onChanged)
    {
        _processId = processId;
        SessionIdentifier = sessionIdentifier ?? string.Empty;
        SessionInstanceIdentifier = sessionInstanceIdentifier ?? string.Empty;
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
    }

    /// <summary>会话标识符。</summary>
    public string SessionIdentifier { get; }

    /// <summary>会话实例标识符。</summary>
    public string SessionInstanceIdentifier { get; }

    /// <summary>是否已标记为释放（释放后不再分发事件）。</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>标记为已释放，此后不再分发任何事件。</summary>
    public void MarkDisposed()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    /// <inheritdoc />
    public int OnDisplayNameChanged(string? newDisplayName, ref Guid eventContext)
    {
        Raise(AudioSessionChangeKind.DisplayNameChanged);
        return 0;
    }

    /// <inheritdoc />
    public int OnIconPathChanged(string? newIconPath, ref Guid eventContext)
    {
        Raise(AudioSessionChangeKind.IconPathChanged);
        return 0;
    }

    /// <inheritdoc />
    public int OnSimpleVolumeChanged(float volume, bool isMuted, ref Guid eventContext)
    {
        Raise(
            AudioSessionChangeKind.VolumeChanged,
            volume: volume,
            isMuted: isMuted);
        return 0;
    }

    /// <inheritdoc />
    public int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext)
    {
        Raise(AudioSessionChangeKind.GroupingParamChanged);
        return 0;
    }

    /// <inheritdoc />
    public int OnStateChanged(Interop.InteropSessionState newState)
    {
        var state = (AudioSessionState)(int)newState;
        Raise(AudioSessionChangeKind.StateChanged, state: state);
        return 0;
    }

    /// <inheritdoc />
    public int OnSessionDisconnected(Interop.InteropSessionDisconnectReason disconnectReason)
    {
        var reason = (AudioSessionDisconnectReason)(int)disconnectReason;
        Raise(AudioSessionChangeKind.Disconnected, disconnectReason: reason);
        return 0;
    }

    private void Raise(
        AudioSessionChangeKind kind,
        AudioSessionState? state = null,
        AudioSessionDisconnectReason? disconnectReason = null,
        float? volume = null,
        bool? isMuted = null)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            _onChanged(new AudioSessionChangedEventArgs(
                kind,
                SessionIdentifier,
                SessionInstanceIdentifier,
                _processId,
                state: state,
                disconnectReason: disconnectReason,
                volume: volume,
                isMuted: isMuted));
        }
        catch
        {
            // 异常绝不穿越 COM 边界
        }
    }
}

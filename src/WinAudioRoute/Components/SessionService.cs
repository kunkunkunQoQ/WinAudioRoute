using System.Diagnostics;
using System.Runtime.InteropServices;
using WinAudioRoute.Events;
using WinAudioRoute.Interop;

namespace WinAudioRoute.Internal;

/// <summary>
/// 音频会话枚举服务。
/// <para>
/// 提取来源：SonicRoute.Core/AudioService.cs 的 <c>GetApps</c> / <c>CollectSessions</c>
/// 与 SonicRoute.Core/SessionVolumeService.cs 的枚举部分
/// （MIT License, Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）。
/// </para>
/// <para>
/// <b>与 SonicRoute 的关键差异</b>：
/// <list type="bullet">
///   <item><description><b>返回会话粒度而非应用粒度。</b>SonicRoute 的 <c>GetApps</c> 一开始就按 PID
///   合并（一个 PID 一条），SDK 若沿用该形状会不可逆地丢失信息。本服务每个会话一条记录，
///   需要"应用级"时由调用方聚合。</description></item>
///   <item><description>缓存改为实例级、可释放，且不持有静态 COM 引用
///   （见 <see cref="SessionCache"/> 与 <see cref="AudioSessionHandle"/>）。</description></item>
///   <item><description>保留 SonicRoute 已验证的"幽灵会话"过滤经验（见
///   <see cref="CollectSessions"/> 注释）。</description></item>
/// </list>
/// </para>
/// </summary>
internal sealed class SessionService : IDisposable
{
    /// <summary>会话快照的可接受最大年龄（与 SonicRoute 的 5 秒新鲜度一致）。</summary>
    internal static readonly TimeSpan DefaultSessionTtl = TimeSpan.FromSeconds(5);

    /// <summary>缓存刷新节流（与 SonicRoute 的 2 秒节流一致）。</summary>
    internal static readonly TimeSpan DefaultRefreshThrottle = TimeSpan.FromSeconds(2);

    private static readonly Guid IID_IAudioSessionManager2 =
        new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    private readonly Func<IDeviceEnumerator> _enumeratorFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SessionCache _cache = new();
    private readonly object _refreshGate = new();

    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private bool _disposed;
    private SessionNotificationClient? _notificationClient;
    private SessionEventClientFactory? _eventClientFactory;
    private Action<AudioSessionChangedEventArgs>? _eventSink;

    /// <summary>创建服务。</summary>
    /// <param name="enumeratorFactory">设备枚举器工厂（默认使用真实 COM 枚举器）。</param>
    /// <param name="timeProvider">时钟（缓存 TTL 与节流）。</param>
    public SessionService(
        Func<IDeviceEnumerator>? enumeratorFactory = null,
        TimeProvider? timeProvider = null)
    {
        _enumeratorFactory = enumeratorFactory ?? (() => new ComDeviceEnumerator());
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>会话快照的可接受最大年龄。</summary>
    public TimeSpan SessionTtl { get; init; } = DefaultSessionTtl;

    /// <summary>缓存刷新节流窗口。</summary>
    public TimeSpan RefreshThrottle { get; init; } = DefaultRefreshThrottle;

    /// <summary>当前缓存中的会话数量。</summary>
    public int CachedSessionCount => _cache.CachedSessionCount;

    /// <summary>
    /// 枚举当前全部音频会话（播放 + 录音），返回<b>会话粒度</b>列表。
    /// </summary>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存强制重新枚举。</param>
    /// <returns>会话快照；同一 PID 的多个会话会出现多条。</returns>
    public IReadOnlyList<AudioSession> GetSessions(bool bypassCache = false) =>
        GetSnapshot(bypassCache).Sessions;

    /// <summary>枚举指定方向的音频会话。</summary>
    /// <param name="flow">数据流方向。</param>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存强制重新枚举。</param>
    /// <returns>该方向的会话快照。</returns>
    public IReadOnlyList<AudioSession> GetSessions(AudioDataFlow flow, bool bypassCache = false)
    {
        IReadOnlyList<AudioSession> all = GetSnapshot(bypassCache).Sessions;
        return all.Where(s => s.Flow == flow).ToList();
    }

    /// <summary>
    /// 取得指定 PID 的会话句柄（供音量操作使用）。总是强制重新枚举，
    /// 因为写操作必须作用于当前有效的会话（SonicRoute 已验证：流媒体应用的会话会随播放内容重建，
    /// 复用旧句柄会"返回成功但不生效"）。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <returns>该 PID 的全部会话句柄（可能为空）。</returns>
    public IReadOnlyList<AudioSessionHandle> GetHandlesForProcess(int processId) =>
        GetSnapshot(bypassCache: true).Handles.Where(h => h.ProcessId == processId).ToList();

    /// <summary>
    /// 取得指定 PID 的会话快照。使用缓存（TTL 内不重新枚举），适用于读路径。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存。</param>
    /// <returns>该 PID 的会话快照（可能为空）。</returns>
    public IReadOnlyList<AudioSession> GetSessionsForProcess(int processId, bool bypassCache = false) =>
        GetSnapshot(bypassCache).Sessions.Where(s => s.ProcessId == processId).ToList();

    /// <summary>取得全部会话句柄（内部使用，例如全局静音）。总是强制重新枚举。</summary>
    /// <returns>全部会话句柄。</returns>
    public IReadOnlyList<AudioSessionHandle> GetAllHandles() => GetSnapshot(bypassCache: true).Handles;

    /// <summary>丢弃缓存并释放其中的会话句柄。</summary>
    public void Refresh() => _cache.Clear();

    /// <summary>
    /// 丢弃缓存并释放其中的会话句柄（<see cref="Refresh"/> 的语义化别名，
    /// 供事件回调使用：设备/会话事件到达时应立即调用）。
    /// </summary>
    public void Invalidate() => _cache.Clear();

    /// <summary>
    /// 启用会话事件通知。
    /// <para>
    /// 注册两路通知：
    /// <list type="number">
    ///   <item><description><c>IAudioSessionNotification</c>（在默认播放设备上）→ 新会话创建。</description></item>
    ///   <item><description><c>IAudioSessionEvents</c>（对每个已知会话，在其句柄上）→ 状态/断开/音量变化。</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// 当前缓存中的会话会立即注册；之后每次重新枚举时，新会话自动补注册，
    /// 已消失的会话由 <see cref="AudioSessionHandle.Dispose"/> 撤销注册。
    /// </para>
    /// </summary>
    /// <param name="eventSink">托管事件分发回调（异常应已在上游隔离）。</param>
    /// <param name="onRegistrationFailed">注册失败回调（参数为 HRESULT）；可为 null。</param>
    /// <returns><c>IAudioSessionNotification</c> 是否注册成功。</returns>
    public bool EnableEventNotifications(
        Action<AudioSessionChangedEventArgs> eventSink,
        Action<int>? onRegistrationFailed = null)
    {
        ArgumentNullException.ThrowIfNull(eventSink);

        lock (_refreshGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_notificationClient is not null)
            {
                return _notificationClient.IsRegistered;
            }

            _eventSink = eventSink;
            _eventClientFactory = new SessionEventClientFactory(eventSink);

            var client = new SessionNotificationClient(
                eventSink,
                onRegistrationFailed ?? (_ => { }));

            // 选一个当前活动的播放设备作为通知锚点；没有活动设备时仍然可以注册逐会话事件
            string? anchorDeviceId = TryGetDefaultRenderDeviceId();
            bool registered = anchorDeviceId is not null && client.Register(anchorDeviceId);

            _notificationClient = registered ? client : null;

            if (!registered)
            {
                client.Dispose();
            }

            RegisterExistingSessionEvents();
            return registered;
        }
    }

    private void RegisterExistingSessionEvents()
    {
        if (_eventClientFactory is null)
        {
            return;
        }

        foreach (AudioSessionHandle handle in _cache.TryGet(TimeSpan.MaxValue, _timeProvider.GetUtcNow())?.Handles ?? [])
        {
            TryRegisterHandleEvents(handle);
        }
    }

    private void TryRegisterHandleEvents(AudioSessionHandle handle)
    {
        if (_eventClientFactory is null || handle.IsDisposed)
        {
            return;
        }

        SessionEventClient client = _eventClientFactory.Create(handle);
        if (!handle.RegisterEvents(client))
        {
            // 注册失败（例如会话不提供该接口）：不影响会话本身的使用
            client.MarkDisposed();
        }
    }

    private string? TryGetDefaultRenderDeviceId()
    {
        try
        {
            using IDeviceEnumerator enumerator = _enumeratorFactory();
            return enumerator.GetDefaultDeviceId(AudioDataFlow.Render, AudioRole.Console)
                ?? enumerator.GetPlaybackDeviceIdForNotifications();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 关闭会话事件通知：撤销 <c>IAudioSessionNotification</c> 注册并释放全部会话句柄
    /// （句柄释放时会一并撤销各自的 <c>IAudioSessionEvents</c> 注册）。幂等。
    /// </summary>
    public void DisableEventNotifications()
    {
        SessionNotificationClient? client;
        lock (_refreshGate)
        {
            client = _notificationClient;
            _notificationClient = null;
            _eventClientFactory = null;
            _eventSink = null;
        }

        client?.Dispose();
        _cache.Clear();
    }

    /// <summary>释放缓存中的全部会话句柄。幂等。</summary>
    public void Dispose()
    {
        SessionNotificationClient? client;
        lock (_refreshGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            client = _notificationClient;
            _notificationClient = null;
            _eventClientFactory = null;
            _eventSink = null;
        }

        // 先撤销通知注册，再释放句柄（句柄释放时会撤销各自的会话事件注册）
        client?.Dispose();
        _cache.Dispose();
    }

    /// <summary>为每个会话创建事件客户端的工厂。</summary>
    private sealed class SessionEventClientFactory
    {
        private readonly Action<AudioSessionChangedEventArgs> _eventSink;

        public SessionEventClientFactory(Action<AudioSessionChangedEventArgs> eventSink)
        {
            _eventSink = eventSink;
        }

        public SessionEventClient Create(AudioSessionHandle handle) => new(
            handle.ProcessId,
            handle.SessionIdentifier,
            handle.SessionInstanceIdentifier,
            _eventSink);
    }

    private AudioSessionSnapshot GetSnapshot(bool bypassCache)
    {
        if (!bypassCache)
        {
            AudioSessionSnapshot? cached = _cache.TryGet(SessionTtl, _timeProvider.GetUtcNow());
            if (cached is not null)
            {
                return cached;
            }
        }

        // 节流：短时间内重复枚举直接复用现有快照，避免高频 COM 枚举
        // （对应 SonicRoute 的 2 秒节流，用于音量滑块高频拖动场景）。
        // 注意：过期（超过 SessionTtl）的快照不会因为节流而被继续使用，
        // 因为过期快照本身在上面的 TryGet 已返回 null，此处只在"新鲜"时复用。
        lock (_refreshGate)
        {
            AudioSessionSnapshot? current = _cache.TryGet(TimeSpan.MaxValue, _timeProvider.GetUtcNow());
            if (current is null)
            {
                _lastRefresh = _timeProvider.GetUtcNow();
            }
            else
            {
                DateTimeOffset now = _timeProvider.GetUtcNow();
                if (now - _lastRefresh < RefreshThrottle)
                {
                    return current;
                }

                _lastRefresh = now;
            }
        }

        AudioSessionSnapshot fresh = Capture();
        _cache.Set(fresh);
        return fresh;
    }

    /// <summary>
    /// 执行一次完整枚举，构造新快照。
    /// <para>
    /// <b>所有权</b>：本方法返回的每个 <see cref="AudioSessionHandle"/> 都独占一个会话 COM 引用；
    /// 构造失败时由中间列表负责释放，构造成功后交给 <see cref="SessionCache"/>。
    /// </para>
    /// </summary>
    private AudioSessionSnapshot Capture()
    {
        List<AudioSession> sessions = [];
        List<AudioSessionHandle> handles = [];

        try
        {
            foreach (AudioDataFlow flow in (AudioDataFlow[])[AudioDataFlow.Render, AudioDataFlow.Capture])
            {
                using IDeviceEnumerator enumerator = _enumeratorFactory();
                IReadOnlyList<AudioDevice> devices = enumerator.GetDevices(flow, AudioDeviceState.Active);

                foreach (AudioDevice device in devices)
                {
                    CollectSessions(enumerator, device, flow, sessions, handles);
                }
            }
        }
        catch
        {
            foreach (AudioSessionHandle handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }

        // 事件系统已启用时，为新捕获的会话补注册 IAudioSessionEvents
        if (_eventClientFactory is not null)
        {
            foreach (AudioSessionHandle handle in handles)
            {
                TryRegisterHandleEvents(handle);
            }
        }

        return new AudioSessionSnapshot(sessions, handles, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// 收集单个设备上的全部会话。
    /// <para>
    /// <b>幽灵会话过滤（沿用 SonicRoute 已验证的经验）</b>：
    /// 用户注销后进程已不存在（任务管理器也搜不到），但会话可能残留且未被标记 <c>Expired</c>。
    /// 这类会话无法操作，若保留会在调用方列表里出现无法处理的空项，因此丢弃。
    /// 判定规则：进程名读取失败<b>且</b>进程确实不存在 → 丢弃；
    /// 若进程存在但读不到名字（权限受限），仍然保留。
    /// </para>
    /// </summary>
    private static void CollectSessions(
        IDeviceEnumerator enumerator,
        AudioDevice device,
        AudioDataFlow flow,
        List<AudioSession> sessions,
        List<AudioSessionHandle> handles)
    {
        IMMDevice? deviceObject = null;

        try
        {
            // 打开底层 IMMDevice：IDeviceEnumerator 只暴露快照，激活会话管理器需要真正的设备对象。
            deviceObject = enumerator.OpenDeviceObject(device.Id);
            if (deviceObject is null)
            {
                return;
            }

            Guid iid = IID_IAudioSessionManager2;
            if (deviceObject.Activate(ref iid, ComConstants.CLSCTX_ALL, IntPtr.Zero, out object managerObject) < 0
                || managerObject is null)
            {
                return;
            }

            using ComScope managerScope = ComScope.Own(managerObject);
            var manager = (IAudioSessionManager2)managerObject;

            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator) < 0
                || sessionEnumerator is null)
            {
                return;
            }

            using ComScope enumeratorScope = ComScope.Own(sessionEnumerator);

            _ = sessionEnumerator.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                if (sessionEnumerator.GetSession(i, out IAudioSessionControl2 session) < 0 || session is null)
                {
                    continue;
                }

                using ComScope sessionScope = ComScope.Own(session);

                if (session.GetProcessId(out uint rawPid) < 0 || rawPid == 0)
                {
                    continue;
                }

                int pid = (int)rawPid;

                if (session.GetState(out InteropSessionState rawState) >= 0
                    && rawState == InteropSessionState.Expired)
                {
                    continue;
                }

                AudioSessionState state = (AudioSessionState)(int)rawState;

                string? displayName = null;
                if (session.GetDisplayName(out string rawDisplayName) >= 0 && !string.IsNullOrWhiteSpace(rawDisplayName))
                {
                    displayName = rawDisplayName;
                }

                bool processAlive = IsProcessAlive(pid);
                string? processName = processAlive ? GetProcessName(pid) : null;

                if (!processAlive)
                {
                    // 幽灵会话：进程不存在 → 丢弃
                    continue;
                }

                string sessionIdentifier = ReadString(session.GetSessionIdentifier);
                string sessionInstanceIdentifier = ReadString(session.GetSessionInstanceIdentifier);
                bool isSystemSounds = session.IsSystemSoundsSession() >= 0;

                // 所有权从 sessionScope 移交给句柄：Detach 之后 sessionScope 不再释放
                _ = sessionScope.Detach();

                var handle = new AudioSessionHandle(session, device.Id, flow)
                {
                    ProcessId = pid,
                    DisplayName = displayName,
                    SessionIdentifier = sessionIdentifier,
                    SessionInstanceIdentifier = sessionInstanceIdentifier,
                    State = state,
                    IsSystemSoundsSession = isSystemSounds,
                };

                try
                {
                    float? volume = handle.TryGetVolumeScalar();
                    bool? muted = handle.TryGetMute();

                    sessions.Add(handle.ToSnapshot(processName, volume, muted));
                    handles.Add(handle);
                }
                catch
                {
                    // 句柄构造后一旦尚未进入 handles，必须在此释放，避免泄漏
                    handle.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            if (deviceObject is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(deviceObject);
                }
                catch
                {
                    // 忽略释放异常
                }
            }
        }
    }

    private static string ReadString(ReadStringDelegate read)
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

    /// <summary>读取进程名（不含扩展名）；失败返回 <see langword="null"/>。</summary>
    private static string? GetProcessName(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 进程是否真实存在。
    /// <c>ArgumentException</c> 表示进程已不存在（幽灵会话）；
    /// 其他异常（如权限受限）表示进程存在但读不到信息，视为存活。
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary><c>IAudioSessionControl2</c> 上读取字符串的方法签名。</summary>
    /// <param name="value">读取到的字符串。</param>
    /// <returns>HRESULT。</returns>
    private delegate int ReadStringDelegate(out string value);
}

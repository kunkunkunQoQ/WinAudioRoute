using System.Runtime.InteropServices;
using WinAudioRoute.Interop;

namespace WinAudioRoute.Internal;

/// <summary>
/// 一个已打开音频会话的元数据 + COM 引用持有者。
/// <para>
/// <b>所有权模型（本类型的核心职责）</b>：
/// <list type="bullet">
///   <item><description>由 <see cref="SessionService"/> 在枚举时创建，从此独占
///   <see cref="IAudioSessionControl2"/> 的一个 COM 引用。</description></item>
///   <item><description>释放责任完全在本类型：<see cref="Dispose"/> 或终结器，二者只生效一次。</description></item>
///   <item><description>移交给缓存时不再有"入容器前抛异常"的窗口：构造成功后立即由缓存持有，
///   缓存释放时统一释放，因此不存在 SonicRoute 里 <c>owned=true</c> 却未入容器的泄漏路径。</description></item>
/// </list>
/// </para>
/// <para>
/// <see cref="ISimpleAudioVolume"/> 引用取自同一 RCW（<c>QueryInterface</c>），
/// 在首次使用时按需获取并缓存；它不新增独立的 COM 引用所有权，
/// 只要本对象仍持有会话引用就有效。
/// </para>
/// </summary>
internal sealed class AudioSessionHandle : IDisposable
{
    private readonly IAudioSessionControl2? _session;
    private ISimpleAudioVolume? _volume;
    private SessionEventClient? _eventClient;
    private bool _volumeResolved;
    private bool _disposed;

    /// <summary>打开一个由本对象独占的会话引用。</summary>
    /// <param name="session">会话控制接口。</param>
    /// <param name="deviceId">会话所在端点设备 ID。</param>
    /// <param name="flow">数据流方向。</param>
    public AudioSessionHandle(IAudioSessionControl2 session, string deviceId, AudioDataFlow flow)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        DeviceId = deviceId;
        Flow = flow;
    }

    /// <summary>会话实例标识符。</summary>
    public string SessionInstanceIdentifier { get; init; } = string.Empty;

    /// <summary>会话标识符。</summary>
    public string SessionIdentifier { get; init; } = string.Empty;

    /// <summary>所属进程 ID。</summary>
    public int ProcessId { get; init; }

    /// <summary>会话显示名。</summary>
    public string? DisplayName { get; init; }

    /// <summary>会话状态。</summary>
    public AudioSessionState State { get; init; }

    /// <summary>是否为系统提示音会话。</summary>
    public bool IsSystemSoundsSession { get; init; }

    /// <summary>会话所在端点设备 ID。</summary>
    public string DeviceId { get; }

    /// <summary>会话数据流方向。</summary>
    public AudioDataFlow Flow { get; }

    /// <summary>本对象是否已释放。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 尝试取得会话音量接口。返回 <see langword="null"/> 表示该会话不提供
    /// <c>ISimpleAudioVolume</c>（例如系统提示音会话）。
    /// </summary>
    public ISimpleAudioVolume? TryGetVolume()
    {
        if (_disposed)
        {
            return null;
        }

        if (_volumeResolved)
        {
            return _volume;
        }

        _volumeResolved = true;
        try
        {
            _volume = _session as ISimpleAudioVolume;
        }
        catch
        {
            // 会话不支持该接口（E_NOINTERFACE）时视为不可用
            _volume = null;
        }

        return _volume;
    }

    /// <summary>读取当前音量标量；不可用或失败时返回 <see langword="null"/>。</summary>
    public float? TryGetVolumeScalar()
    {
        ISimpleAudioVolume? volume = TryGetVolume();
        if (volume is null)
        {
            return null;
        }

        try
        {
            return volume.GetMasterVolume(out float level) >= 0 ? level : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取当前静音状态；不可用或失败时返回 <see langword="null"/>。</summary>
    public bool? TryGetMute()
    {
        ISimpleAudioVolume? volume = TryGetVolume();
        if (volume is null)
        {
            return null;
        }

        try
        {
            return volume.GetMute(out int muted) >= 0 ? muted != 0 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>创建该会话的公开快照。</summary>
    /// <param name="processName">进程名（不含扩展名）；不可用时为 <see langword="null"/>。</param>
    /// <param name="volume">音量标量；不可用时为 <see langword="null"/>。</param>
    /// <param name="isMuted">静音状态；不可用时为 <see langword="null"/>。</param>
    /// <returns>不可变会话快照。</returns>
    public AudioSession ToSnapshot(string? processName, float? volume, bool? isMuted) => new()
    {
        ProcessId = ProcessId,
        ProcessName = processName,
        DisplayName = DisplayName,
        SessionIdentifier = SessionIdentifier,
        SessionInstanceIdentifier = SessionInstanceIdentifier,
        State = State,
        Flow = Flow,
        DeviceId = DeviceId,
        Volume = volume,
        IsMuted = isMuted,
        IsSystemSoundsSession = IsSystemSoundsSession,
    };

    /// <summary>
    /// 在本会话上注册事件通知。
    /// <para>
    /// <b>所有权</b>：注册所需的是本对象已经独占的那个
    /// <see cref="IAudioSessionControl2"/> RCW 引用（<c>IAudioSessionEvents</c> 由该 RCW 的
    /// <c>QueryInterface</c> 提供），因此<b>不产生额外的会话引用</b>。
    /// 客户端由本对象强引用，并在 <see cref="Dispose"/> 时撤销注册。
    /// </para>
    /// </summary>
    /// <param name="client">事件客户端。</param>
    /// <returns>注册成功返回 <see langword="true"/>。</returns>
    public bool RegisterEvents(SessionEventClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (_disposed || _session is null)
        {
            return false;
        }

        IAudioSessionEvents events;
        try
        {
            events = (IAudioSessionEvents)_session;
        }
        catch
        {
            // 会话不提供该接口
            return false;
        }

        try
        {
            if (_session.RegisterAudioSessionNotification(events) < 0)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        _eventClient = client;
        return true;
    }

    /// <summary>
    /// 撤销本会话的事件通知。幂等；未注册时不做任何事。
    /// </summary>
    public void UnregisterEvents()
    {
        SessionEventClient? client = _eventClient;
        _eventClient = null;

        if (client is null)
        {
            return;
        }

        client.MarkDisposed();

        if (_session is null)
        {
            return;
        }

        try
        {
            _ = _session.UnregisterAudioSessionNotification((IAudioSessionEvents)_session);
        }
        catch
        {
            // 撤销失败不得抛出：句柄即将释放，原生侧注册会被系统在会话销毁时清理
        }
    }

    /// <summary>释放本对象独占的会话 COM 引用。幂等。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _volume = null;
        _volumeResolved = true;

        // 先撤销事件注册，再释放会话引用（顺序不可颠倒）
        UnregisterEvents();

        IAudioSessionControl2? session = _session;
        if (session is not null)
        {
            try
            {
                Marshal.ReleaseComObject(session);
            }
            catch
            {
                // 释放异常不得向外传播（可能发生在 Dispose 或终结器路径上）
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 终结器：调用方忘记 <c>Dispose</c> 时的兜底，避免 RCW 永久泄漏。
    /// </summary>
    ~AudioSessionHandle() => Dispose();
}

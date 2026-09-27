using System.Runtime.Versioning;

namespace WinAudioRoute.Events;

/// <summary>
/// 设备变化种类。
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public enum AudioDeviceChangeKind
{
    /// <summary>新设备加入（<c>IMMNotificationClient::OnDeviceAdded</c>）。</summary>
    Added,

    /// <summary>设备被移除（<c>OnDeviceRemoved</c>）。</summary>
    Removed,

    /// <summary>设备状态变化（<c>OnDeviceStateChanged</c>）。</summary>
    StateChanged,

    /// <summary>默认设备变化（<c>OnDefaultDeviceChanged</c>）。</summary>
    DefaultChanged,

    /// <summary>设备属性变化，例如友好名称（<c>OnPropertyValueChanged</c>）。</summary>
    PropertyChanged,
}

/// <summary>
/// 设备变化事件参数。
/// <para>
/// <b>可为 null 的字段表示"原生回调没有提供该信息"</b>，本库不会为了填满模型而猜测取值。
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class AudioDeviceChangedEventArgs : EventArgs
{
    internal AudioDeviceChangedEventArgs(
        AudioDeviceChangeKind kind,
        string deviceId,
        AudioDataFlow? flow = null,
        AudioRole? role = null,
        AudioDeviceState? state = null)
    {
        Kind = kind;
        DeviceId = deviceId;
        Flow = flow;
        Role = role;
        State = state;
    }

    /// <summary>变化种类。</summary>
    public AudioDeviceChangeKind Kind { get; }

    /// <summary>受影响的设备 ID（默认设备变化时为新的默认设备 ID；可能为空串）。</summary>
    public string DeviceId { get; }

    /// <summary>方向；仅 <see cref="AudioDeviceChangeKind.DefaultChanged"/> 提供。</summary>
    public AudioDataFlow? Flow { get; }

    /// <summary>端点角色；仅 <see cref="AudioDeviceChangeKind.DefaultChanged"/> 提供。</summary>
    public AudioRole? Role { get; }

    /// <summary>设备新状态；仅 <see cref="AudioDeviceChangeKind.StateChanged"/> 提供。</summary>
    public AudioDeviceState? State { get; }

    /// <summary>诊断字符串，英文、中性。</summary>
    public override string ToString() => Kind switch
    {
        AudioDeviceChangeKind.DefaultChanged => $"{Kind}: {Flow}/{Role} -> '{DeviceId}'",
        AudioDeviceChangeKind.StateChanged => $"{Kind}: '{DeviceId}' -> {State}",
        _ => $"{Kind}: '{DeviceId}'",
    };
}

/// <summary>
/// 会话变化种类。
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public enum AudioSessionChangeKind
{
    /// <summary>新会话被创建（<c>IAudioSessionNotification::OnSessionCreated</c>）。</summary>
    Created,

    /// <summary>会话断开（<c>IAudioSessionEvents::OnSessionDisconnected</c>）。</summary>
    Disconnected,

    /// <summary>会话状态变化（<c>OnStateChanged</c>）。</summary>
    StateChanged,

    /// <summary>会话显示名变化（<c>OnDisplayNameChanged</c>）。</summary>
    DisplayNameChanged,

    /// <summary>会话图标路径变化（<c>OnIconPathChanged</c>）。</summary>
    IconPathChanged,

    /// <summary>会话音量或静音变化（<c>OnSimpleVolumeChanged</c>）。</summary>
    VolumeChanged,

    /// <summary>会话分组参数变化（<c>OnGroupingParamChanged</c>）。</summary>
    GroupingParamChanged,
}

/// <summary>
/// 会话变化事件参数。
/// <para>
/// <b>可为 null 的字段表示"原生回调没有提供该信息"</b>，本库不会为了填满模型而猜测取值。
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class AudioSessionChangedEventArgs : EventArgs
{
    internal AudioSessionChangedEventArgs(
        AudioSessionChangeKind kind,
        string sessionIdentifier,
        string sessionInstanceIdentifier,
        int processId,
        AudioDataFlow? flow = null,
        AudioSessionState? state = null,
        AudioSessionDisconnectReason? disconnectReason = null,
        float? volume = null,
        bool? isMuted = null)
    {
        Kind = kind;
        SessionIdentifier = sessionIdentifier;
        SessionInstanceIdentifier = sessionInstanceIdentifier;
        ProcessId = processId;
        Flow = flow;
        State = state;
        DisconnectReason = disconnectReason;
        Volume = volume;
        IsMuted = isMuted;
    }

    /// <summary>变化种类。</summary>
    public AudioSessionChangeKind Kind { get; }

    /// <summary>会话标识符（<c>GetSessionIdentifier</c>）；创建事件中可能为空串。</summary>
    public string SessionIdentifier { get; }

    /// <summary>会话实例标识符（<c>GetSessionInstanceIdentifier</c>）；创建事件中可能为空串。</summary>
    public string SessionInstanceIdentifier { get; }

    /// <summary>所属进程 ID；无法取得时为 0。</summary>
    public int ProcessId { get; }

    /// <summary>方向；本库当前不填充（会话通知不携带方向），因此通常为 <see langword="null"/>。</summary>
    public AudioDataFlow? Flow { get; }

    /// <summary>会话新状态；仅 <see cref="AudioSessionChangeKind.StateChanged"/> 提供。</summary>
    public AudioSessionState? State { get; }

    /// <summary>断开原因；仅 <see cref="AudioSessionChangeKind.Disconnected"/> 提供。</summary>
    public AudioSessionDisconnectReason? DisconnectReason { get; }

    /// <summary>音量标量；仅 <see cref="AudioSessionChangeKind.VolumeChanged"/> 提供。</summary>
    public float? Volume { get; }

    /// <summary>静音状态；仅 <see cref="AudioSessionChangeKind.VolumeChanged"/> 提供。</summary>
    public bool? IsMuted { get; }

    /// <summary>诊断字符串，英文、中性。</summary>
    public override string ToString()
    {
        string id = string.IsNullOrEmpty(SessionInstanceIdentifier)
            ? (string.IsNullOrEmpty(SessionIdentifier) ? "(unknown)" : SessionIdentifier)
            : SessionInstanceIdentifier;

        return Kind switch
        {
            AudioSessionChangeKind.Disconnected => $"{Kind}: '{id}' (PID {ProcessId}, {DisconnectReason})",
            AudioSessionChangeKind.StateChanged => $"{Kind}: '{id}' (PID {ProcessId}) -> {State}",
            AudioSessionChangeKind.VolumeChanged => $"{Kind}: '{id}' (PID {ProcessId}) volume={Volume} muted={IsMuted}",
            _ => $"{Kind}: '{id}' (PID {ProcessId})",
        };
    }
}

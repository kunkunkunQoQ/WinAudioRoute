namespace WinAudioRoute;

/// <summary>
/// 单个音频会话的不可变快照。
/// <para>
/// <b>这是会话粒度，不是应用粒度。</b>一个进程（PID）可以有多个会话
/// （例如浏览器每个标签页一个、同一进程在多个端点设备上各一个）。
/// 库不会在底层按 PID 去重，因为那会不可逆地丢失信息；
/// 需要"应用级"语义时，由调用方或 <c>SessionResolver</c> 在上层聚合。
/// </para>
/// </summary>
public sealed record AudioSession
{
    /// <summary>拥有该会话的进程 ID。</summary>
    public required int ProcessId { get; init; }

    /// <summary>
    /// 进程名（<b>不含</b>扩展名，例如 <c>chrome</c>）。
    /// 进程已退出或权限受限时为 <see langword="null"/>。
    /// </summary>
    public string? ProcessName { get; init; }

    /// <summary>
    /// 会话显示名（<c>IAudioSessionControl::GetDisplayName</c>）。
    /// 未设置时为 <see langword="null"/>（Windows 对很多应用返回空串）。
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// 会话标识符（<c>IAudioSessionControl2::GetSessionIdentifier</c>）。
    /// 同一应用跨进程/跨启动时相对稳定，用于跨会话关联。
    /// </summary>
    public string SessionIdentifier { get; init; } = string.Empty;

    /// <summary>
    /// 会话实例标识符（<c>IAudioSessionControl2::GetSessionInstanceIdentifier</c>）。
    /// 每个会话实例唯一，用于精确定位单个会话。
    /// </summary>
    public string SessionInstanceIdentifier { get; init; } = string.Empty;

    /// <summary>会话状态。</summary>
    public AudioSessionState State { get; init; }

    /// <summary>该会话所属的数据流方向（播放 / 录音）。</summary>
    public AudioDataFlow Flow { get; init; }

    /// <summary>该会话所在端点设备的 ID（与 <see cref="AudioDevice.Id"/> 同格式）。</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>
    /// 会话音量标量（0.0–1.0）。设备未提供 <c>ISimpleAudioVolume</c> 时为 <see langword="null"/>。
    /// </summary>
    public float? Volume { get; init; }

    /// <summary>
    /// 会话是否静音。设备未提供 <c>ISimpleAudioVolume</c> 时为 <see langword="null"/>。
    /// </summary>
    public bool? IsMuted { get; init; }

    /// <summary>
    /// 是否为系统提示音会话（<c>IAudioSessionControl2::IsSystemSoundsSession</c>）。
    /// </summary>
    public bool IsSystemSoundsSession { get; init; }

    /// <summary>
    /// 是否为正在发声/录音的会话（<see cref="State"/> 为 <see cref="AudioSessionState.Active"/>）。
    /// </summary>
    public bool IsActive => State == AudioSessionState.Active;

    /// <summary>
    /// 返回 <c>ProcessName/DisplayName [Flow] (PID)</c> 形式的诊断字符串，不含 UI 装饰。
    /// </summary>
    public override string ToString()
    {
        string label = !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName!
            : !string.IsNullOrWhiteSpace(ProcessName) ? ProcessName!
            : $"PID {ProcessId}";
        return $"{label} [{Flow}] (PID {ProcessId})";
    }
}

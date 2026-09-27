namespace WinAudioRoute;

/// <summary>
/// 端点设备的不可变快照。只包含 Windows 音频 API 的原始系统信息。
/// <para>
/// <b>不含</b>任何表现层内容：没有 emoji、没有本地化文案、没有 UI 排序文本、
/// 没有用户自定义设备名、没有"隐藏设备"配置。这些都属于调用方（例如 SonicRoute）。
/// </para>
/// </summary>
public sealed record AudioDevice
{
    /// <summary>
    /// 设备 ID（<c>IMMDevice::GetId()</c> 返回的短 ID，形如
    /// <c>{0.0.0.00000000}.{guid}</c>）。唯一标识本端点。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 友好名称（<c>PKEY_Device_FriendlyName</c>）。属性缺失或不可读时为 <see cref="string.Empty"/>。
    /// </summary>
    public required string FriendlyName { get; init; }

    /// <summary>数据流方向（播放 / 录音）。</summary>
    public required AudioDataFlow Flow { get; init; }

    /// <summary>设备的当前状态（<c>IMMDevice::GetState()</c>）。</summary>
    public AudioDeviceState State { get; init; }

    /// <summary>是否为 <see cref="AudioRole.Console"/> 的默认设备（系统"默认设备"）。</summary>
    public bool IsDefaultConsole { get; init; }

    /// <summary>是否为 <see cref="AudioRole.Multimedia"/> 的默认设备。</summary>
    public bool IsDefaultMultimedia { get; init; }

    /// <summary>是否为 <see cref="AudioRole.Communications"/> 的默认设备（"默认通信设备"）。</summary>
    public bool IsDefaultCommunications { get; init; }

    /// <summary>
    /// 是否在任一端点角色下为默认设备。
    /// </summary>
    public bool IsDefault => IsDefaultConsole || IsDefaultMultimedia || IsDefaultCommunications;

    /// <summary>
    /// 是否为活动设备（可用于播放/录音）。
    /// </summary>
    public bool IsActive => (State & AudioDeviceState.Active) == AudioDeviceState.Active;

    /// <summary>
    /// 返回一个只含系统原始信息的字符串（<c>FriendlyName [Flow]</c>），不含 UI 装饰。
    /// </summary>
    public override string ToString() =>
        string.IsNullOrEmpty(FriendlyName) ? $"{Id} [{Flow}]" : $"{FriendlyName} [{Flow}]";
}

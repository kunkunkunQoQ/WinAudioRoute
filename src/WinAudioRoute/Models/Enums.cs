namespace WinAudioRoute;

/// <summary>
/// 音频端点数据流方向。数值与 Windows WASAPI 的 <c>EDataFlow</c>（mmdeviceapi.h）一致，
/// 该数值属于公开且稳定的接口契约，不得重排或改值。
/// </summary>
public enum AudioDataFlow
{
    /// <summary>播放（输出）端点。<c>EDataFlow.eRender = 0</c>。</summary>
    Render = 0,

    /// <summary>录音（输入）端点。<c>EDataFlow.eCapture = 1</c>。</summary>
    Capture = 1,

    /// <summary>全部方向。仅用于枚举，不作为单设备方向。</summary>
    All = 2,
}

/// <summary>
/// 音频端点角色。数值与 Windows WASAPI 的 <c>ERole</c>（mmdeviceapi.h）一致。
/// <para>
/// 注意：数值 <b>恰好</b> 与 <see cref="AudioDataFlow"/> 的前两项重合
/// （Console = Render = 0、Multimedia = Capture = 1）。两者语义完全不同：
/// <see cref="AudioDataFlow"/> 选择"哪个方向"，<see cref="AudioRole"/> 选择"哪一组系统默认设备"。
/// 该重合是此前实现中出现类型误用的直接原因，本库把二者严格区分。
/// </para>
/// </summary>
public enum AudioRole
{
    /// <summary>系统默认（"默认设备"）。<c>ERole.eConsole = 0 = eRender</c>。</summary>
    Console = 0,

    /// <summary>多媒体默认（"默认通信设备"之外的常规媒体角色）。<c>ERole.eMultimedia = 1 = eCapture</c>。</summary>
    Multimedia = 1,

    /// <summary>通信默认（"默认通信设备"）。<c>ERole.eCommunications = 2</c>。</summary>
    Communications = 2,
}

/// <summary>
/// 音频端点设备状态。数值与 Windows WASAPI 的 <c>DEVICE_STATE_*</c>（mmdeviceapi.h）一致。
/// </summary>
/// <remarks>
/// <b>关于 <see cref="All"/></b>：WinAudioRoute 使用 Windows 官方常量
/// <c>DEVICE_STATEMASK_ALL = 0x0000000F</c>（= <see cref="Active"/> | <see cref="Disabled"/>
/// | <see cref="NotPresent"/> | <see cref="Unplugged"/>）。
/// 若调用者传入非设备状态位（例如 <c>unchecked((int)0xFFFFFFFF)</c>），
/// 由 SDK 前置校验归一化为 0x0F 或直接拒绝——详见 <c>DeviceStateMask</c>。
/// </remarks>
[Flags]
public enum AudioDeviceState
{
    /// <summary>无状态（不是合法查询掩码；查询时会抛 <see cref="ArgumentOutOfRangeException"/>）。</summary>
    None = 0x0,

    /// <summary>设备处于活动状态（已启用且已插入）。</summary>
    Active = 0x1,

    /// <summary>设备已被禁用。</summary>
    Disabled = 0x2,

    /// <summary>设备当前不存在。</summary>
    NotPresent = 0x4,

    /// <summary>设备存在但未插入。</summary>
    Unplugged = 0x8,

    /// <summary>
    /// 全部真实状态（对应 Windows <c>DEVICE_STATEMASK_ALL = 0x0000000F</c>）。
    /// </summary>
    All = Active | Disabled | NotPresent | Unplugged,
}

/// <summary>
/// 音频会话状态。数值与 <c>AudioSessionState</c>（audiopolicy.h）一致。
/// </summary>
public enum AudioSessionState
{
    /// <summary>会话存在但没有音频流。</summary>
    Inactive = 0,

    /// <summary>会话正在播放或录音。</summary>
    Active = 1,

    /// <summary>会话已过期，即将被销毁。</summary>
    Expired = 2,
}

/// <summary>
/// 会话断开原因。数值与 <c>AudioSessionDisconnectReason</c>（audiopolicy.h）一致。
/// </summary>
public enum AudioSessionDisconnectReason
{
    /// <summary>用户从音量合成器断开了会话。</summary>
    DeviceRemoval = 0,

    /// <summary>音频服务已停止。</summary>
    ServerShutdown = 1,

    /// <summary>会话格式发生变化。</summary>
    FormatChanged = 2,

    /// <summary>用户注销。</summary>
    SessionLogoff = 3,

    /// <summary>会话被断开。</summary>
    SessionDisconnected = 4,

    /// <summary>独占模式会话抢占了端点。</summary>
    ExclusiveModeOverride = 5,
}

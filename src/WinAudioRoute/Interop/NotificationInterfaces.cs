using System.Runtime.InteropServices;

namespace WinAudioRoute.Interop;

// =====================================================================
// 音频通知回调 COM 接口（客户端实现，由系统调用）。
//
// IID / vtable 顺序全部取自公开 Windows SDK 头文件：
//   - IMMNotificationClient  -> mmdeviceapi.h
//   - IAudioSessionNotification / IAudioSessionEvents -> audiopolicy.h
//
// 这些接口必须由托管侧实现并交给原生侧持有，因此：
//   1. 使用 [ComImport, InterfaceType(InterfaceIsIUnknown)] + [PreserveSig]
//   2. 方法顺序 = vtable 顺序，不得调整
//   3. 字符串参数使用 LPWStr（Marshal 负责分配与释放）
//   4. 实现类必须被托管强引用直到注销完成，否则原生侧指针悬空
// =====================================================================

/// <summary>设备状态变化通知。<c>IMMNotificationClient</c>（mmdeviceapi.h）。</summary>
[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    /// <summary>设备状态变化（启用/禁用/插入/拔出/不存在）。</summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <param name="newState">新状态。</param>
    [PreserveSig]
    int OnDeviceStateChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        DeviceState newState);

    /// <summary>新设备加入。</summary>
    /// <param name="deviceId">设备 ID。</param>
    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    /// <summary>设备移除。</summary>
    /// <param name="deviceId">设备 ID。</param>
    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    /// <summary>默认设备变化。</summary>
    /// <param name="flow">数据流方向。</param>
    /// <param name="role">端点角色。</param>
    /// <param name="defaultDeviceId">新的默认设备 ID（可能为 null）。</param>
    [PreserveSig]
    int OnDefaultDeviceChanged(
        EDataFlow flow,
        ERole role,
        [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    /// <summary>设备属性变化（友好名称等）。</summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <param name="properties">
    /// 发生变化的属性（原生以 <c>VARIANT</c> 传值）。本库不解析其内容，仅作为变化信号使用。
    /// </param>
    [PreserveSig]
    int OnPropertyValueChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        InteropVariant properties);
}

/// <summary>新会话创建通知。<c>IAudioSessionNotification</c>（audiopolicy.h）。</summary>
[ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionNotification
{
    /// <summary>新会话已创建。</summary>
    /// <param name="newSession">新建会话的控制接口。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int OnSessionCreated([MarshalAs(UnmanagedType.IUnknown)] object newSession);
}

/// <summary>会话事件通知。<c>IAudioSessionEvents</c>（audiopolicy.h）。</summary>
[ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEvents
{
    /// <summary>会话显示名变化。</summary>
    /// <param name="newDisplayName">新显示名。</param>
    /// <param name="eventContext">事件上下文。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string? newDisplayName, ref Guid eventContext);

    /// <summary>会话图标路径变化。</summary>
    /// <param name="newIconPath">新图标路径。</param>
    /// <param name="eventContext">事件上下文。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string? newIconPath, ref Guid eventContext);

    /// <summary>会话音量变化。</summary>
    /// <param name="volume">新音量标量（0.0–1.0）。</param>
    /// <param name="isMuted">是否静音。</param>
    /// <param name="eventContext">事件上下文。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);

    /// <summary>会话分组参数变化。</summary>
    /// <param name="newGroupingParam">新分组 GUID。</param>
    /// <param name="eventContext">事件上下文。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext);

    /// <summary>会话状态变化。</summary>
    /// <param name="newState">新状态。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int OnStateChanged(InteropSessionState newState);

    /// <summary>会话断开（应用退出、设备移除等）。</summary>
    /// <param name="disconnectReason">断开原因（<c>InteropSessionDisconnectReason</c>）。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int OnSessionDisconnected(InteropSessionDisconnectReason disconnectReason);
}

/// <summary>
/// 会话断开原因（audiopolicy.h <c>InteropSessionDisconnectReason</c>）。
/// </summary>
internal enum InteropSessionDisconnectReason
{
    /// <summary>用户从音量合成器断开。</summary>
    DisconnectReasonDeviceRemoval = 0,

    /// <summary>音频服务停止。</summary>
    DisconnectReasonServerShutdown = 1,

    /// <summary>会话被格式化。</summary>
    DisconnectReasonFormatChanged = 2,

    /// <summary>用户注销。</summary>
    DisconnectReasonSessionLogoff = 3,

    /// <summary>会话被断开。</summary>
    DisconnectReasonSessionDisconnected = 4,

    /// <summary>独占模式会话抢占了端点。</summary>
    DisconnectReasonExclusiveModeOverride = 5,
}

/// <summary>
/// <c>VARIANT</c> 的最小声明，用于接收 <c>IMMNotificationClient::OnPropertyValueChanged</c>
/// 传入的 <c>PROPERTYKEY</c>（调用约定上按 VARIANT 传递）。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct InteropVariant
{
    /// <summary>类型标签。</summary>
    [FieldOffset(0)]
    public ushort vt;
}

using System.Runtime.InteropServices;

namespace WinAudioRoute.Interop;

// =====================================================================
// WASAPI COM 接口定义
//
// 提取来源：SonicRoute.Core/Interop/WasapiInterfaces.cs（MIT License,
// Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）
//
// 提取原则（Phase 1 约束，逐条遵守）：
//   1. 保留 GUID
//   2. 保留接口方法顺序（vtable 顺序 = 行为契约）
//   3. 保留 [PreserveSig]（全部方法返回 HRESULT，由调用方判断 < 0）
//   4. 保留 [StructLayout]（见 ComInterop.cs）
//   5. 保留现有 P/Invoke（见 NativeMethods.cs）
//   6. 保留 provenance / 参考来源注释（见下方各接口的 IID 出处）
//   7. 不重新实现 COM 接口；不用 CsWinRT 替代；不用第三方音频库替代
//   8. 不做"现代化重写"
//   仅允许：namespace 调整、public→internal、XML 文档、平台保护
//
// IID 全部取自公开的 Windows SDK 头文件定义（已核对）。
// =====================================================================

/// <summary>
/// <c>MMDeviceEnumerator</c> 的 COM 可创建类
/// （CLSID <c>BCDE0395-E52F-467C-8E3D-C4579291692E</c>，mmdeviceapi.h）。
/// </summary>
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

/// <summary>
/// 端点设备枚举器。<c>IMMDeviceEnumerator</c> = {A95664D2-9614-4F35-A746-DE8DB63617E6}。
/// </summary>
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    /// <summary>按方向与状态掩码枚举端点设备。</summary>
    /// <param name="dataFlow">数据流方向。</param>
    /// <param name="dwStateMask">状态掩码。</param>
    /// <param name="ppDevices">接收设备集合。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState dwStateMask, out IMMDeviceCollection ppDevices);

    /// <summary>取得指定方向的角色默认设备。</summary>
    /// <param name="dataFlow">数据流方向。</param>
    /// <param name="role">角色（Console / Multimedia / Communications）。</param>
    /// <param name="ppEndpoint">接收端点设备。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);

    /// <summary>按设备 ID 取得端点设备。</summary>
    /// <param name="pwstrId">设备 ID（<c>IMMDevice.GetId()</c> 返回的短 ID）。</param>
    /// <param name="ppDevice">接收端点设备。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

    /// <summary>注册端点变化通知回调。</summary>
    /// <param name="pClient">实现 <c>IMMNotificationClient</c> 的回调对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] object pClient);

    /// <summary>注销端点变化通知回调。</summary>
    /// <param name="pClient">先前注册的回调对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] object pClient);
}

/// <summary>
/// 端点设备。<c>IMMDevice</c> = {D666063F-1587-4E43-81F1-B948E807363F}。
/// </summary>
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    /// <summary>激活设备上的指定接口。</summary>
    /// <param name="iid">目标接口 IID。</param>
    /// <param name="dwClsCtx">CLSCTX。</param>
    /// <param name="pActivationParams">激活参数（通常为 <see cref="IntPtr.Zero"/>）。</param>
    /// <param name="ppInterface">接收接口对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

    /// <summary>打开设备属性存储。</summary>
    /// <param name="stgmAccess">访问模式（STGM_*）。</param>
    /// <param name="ppProperties">接收属性存储。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);

    /// <summary>取得设备 ID。</summary>
    /// <param name="ppstrId">接收设备 ID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

    /// <summary>取得设备状态。</summary>
    /// <param name="pdwState">接收状态。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetState(out DeviceState pdwState);
}

/// <summary>
/// 端点设备集合。<c>IMMDeviceCollection</c> = {0BD7A1BE-7A1A-44DB-8397-CC5392387B5E}。
/// </summary>
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    /// <summary>取得设备数量。</summary>
    /// <param name="pcDevices">接收数量。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetCount(out uint pcDevices);

    /// <summary>按索引取得设备。</summary>
    /// <param name="nDevice">索引。</param>
    /// <param name="ppDevice">接收设备。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int Item(uint nDevice, out IMMDevice ppDevice);
}

/// <summary>
/// 属性存储。<c>IPropertyStore</c> = {886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99}。
/// </summary>
[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    /// <summary>取得属性数量。</summary>
    /// <param name="cProps">接收数量。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetCount(out uint cProps);

    /// <summary>按索引取得属性键。</summary>
    /// <param name="iProp">索引。</param>
    /// <param name="pkey">接收属性键。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetAt(uint iProp, out PROPERTYKEY pkey);

    /// <summary>读取属性值。</summary>
    /// <param name="key">属性键。</param>
    /// <param name="pv">接收属性值。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

    /// <summary>写入属性值。</summary>
    /// <param name="key">属性键。</param>
    /// <param name="propvar">属性值。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);

    /// <summary>提交改动。</summary>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int Commit();
}

/// <summary>
/// 音频会话管理器（v2）。<c>IAudioSessionManager2</c> = {77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F}。
/// </summary>
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    /// <summary>取得会话控制接口。</summary>
    /// <param name="audioSessionGuid">会话 GUID。</param>
    /// <param name="streamFlags">流标志。</param>
    /// <param name="sessionControl">接收会话控制对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags, [MarshalAs(UnmanagedType.Interface)] out object sessionControl);

    /// <summary>取得简单音量接口。</summary>
    /// <param name="audioSessionGuid">会话 GUID。</param>
    /// <param name="streamFlags">流标志。</param>
    /// <param name="audioVolume">接收音量对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, [MarshalAs(UnmanagedType.Interface)] out object audioVolume);

    /// <summary>取得会话枚举器。</summary>
    /// <param name="sessionEnum">接收枚举器。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

    /// <summary>注册会话通知回调。</summary>
    /// <param name="sessionNotification">实现 <c>IAudioSessionNotification</c> 的对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int RegisterSessionNotification([MarshalAs(UnmanagedType.Interface)] object sessionNotification);

    /// <summary>注销会话通知回调。</summary>
    /// <param name="sessionNotification">先前注册的对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int UnregisterSessionNotification([MarshalAs(UnmanagedType.Interface)] object sessionNotification);

    /// <summary>注册 duck 通知回调。</summary>
    /// <param name="sessionID">会话 ID。</param>
    /// <param name="duckNotification">实现 <c>IAudioVolumeDuckNotification</c> 的对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, [MarshalAs(UnmanagedType.Interface)] object duckNotification);

    /// <summary>注销 duck 通知回调。</summary>
    /// <param name="duckNotification">先前注册的对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int UnregisterDuckNotification([MarshalAs(UnmanagedType.Interface)] object duckNotification);
}

/// <summary>
/// 音频会话枚举器。<c>IAudioSessionEnumerator</c> = {E2F5BB11-0570-40CA-ACDD-3AA01277DEE8}。
/// </summary>
[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    /// <summary>取得会话数量。</summary>
    /// <param name="sessionCount">接收数量。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetCount(out int sessionCount);

    /// <summary>按索引取得会话。</summary>
    /// <param name="index">索引。</param>
    /// <param name="session">接收会话控制对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetSession(int index, [MarshalAs(UnmanagedType.Interface)] out IAudioSessionControl2 session);
}

/// <summary>
/// 音频会话控制（v2）。<c>IAudioSessionControl2</c> = {BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D}。
/// <para>
/// <b>扁平声明 14 个方法</b>（9 个 <c>IAudioSessionControl</c> + 5 个 <c>IAudioSessionControl2</c>）。
/// 顺序即 vtable 契约，与公开头文件 <c>audiopolicy.h</c> 一致，不得调整。
/// </para>
/// </summary>
[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // ---- IAudioSessionControl ----

    /// <summary>取得会话状态。</summary>
    /// <param name="state">接收状态。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetState(out InteropSessionState state);

    /// <summary>取得会话显示名。</summary>
    /// <param name="displayName">接收显示名。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    /// <summary>设置会话显示名。</summary>
    /// <param name="value">显示名。</param>
    /// <param name="eventContext">事件上下文 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

    /// <summary>取得图标路径。</summary>
    /// <param name="iconPath">接收图标路径。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    /// <summary>设置图标路径。</summary>
    /// <param name="value">图标路径。</param>
    /// <param name="eventContext">事件上下文 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

    /// <summary>取得分组参数。</summary>
    /// <param name="groupingParam">接收分组 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    /// <summary>设置分组参数。</summary>
    /// <param name="override_">分组 GUID。</param>
    /// <param name="eventContext">事件上下文 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetGroupingParam(ref Guid override_, ref Guid eventContext);

    /// <summary>注册会话事件回调。</summary>
    /// <param name="notifications">实现 <c>IAudioSessionEvents</c> 的对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int RegisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] object notifications);

    /// <summary>注销会话事件回调。</summary>
    /// <param name="notifications">先前注册的对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int UnregisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] object notifications);

    // ---- IAudioSessionControl2 ----

    /// <summary>取得会话标识符。</summary>
    /// <param name="id">接收标识符。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

    /// <summary>取得会话实例标识符。</summary>
    /// <param name="id">接收实例标识符。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

    /// <summary>取得所属进程 ID。</summary>
    /// <param name="processId">接收 PID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetProcessId(out uint processId);

    /// <summary>是否为系统提示音会话。</summary>
    /// <returns>HRESULT（<c>S_OK</c> = 是系统提示音）。</returns>
    [PreserveSig]
    int IsSystemSoundsSession();

    /// <summary>设置 ducking 偏好。</summary>
    /// <param name="optOut">非 0 表示退出 ducking。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetDuckingPreference(int optOut);
}

/// <summary>
/// 端点音量控制。<c>IAudioEndpointVolume</c> = {5CDF2C82-841E-4546-9722-0CF74078229A}。
/// <para>
/// <b>扁平声明 13 个方法</b>（<c>RegisterControlChangeNotify</c> 为 vtable 第 4 个方法）。
/// <c>SetMute</c> 为第 12 个、<c>GetMute</c> 为第 13 个——用于设备级静音。
/// 顺序即 vtable 契约，与公开头文件 <c>endpointvolume.h</c> 一致，不得调整。
/// </para>
/// </summary>
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    /// <summary>注册音量变化通知回调。</summary>
    /// <param name="pNotify">实现 <c>IAudioEndpointVolumeCallback</c> 的对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int RegisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] object pNotify);

    /// <summary>注销音量变化通知回调。</summary>
    /// <param name="pNotify">先前注册的对象。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int UnregisterControlChangeNotify([MarshalAs(UnmanagedType.Interface)] object pNotify);

    /// <summary>取得声道数。</summary>
    /// <param name="pnChannelCount">接收声道数。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetChannelCount(out uint pnChannelCount);

    /// <summary>设置主音量（分贝）。</summary>
    /// <param name="fLevelDB">分贝值。</param>
    /// <param name="pguidEventContext">事件上下文 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);

    /// <summary>设置主音量（标量 0.0–1.0）。</summary>
    /// <param name="fLevel">标量音量。</param>
    /// <param name="pguidEventContext">事件上下文 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);

    /// <summary>取得主音量（分贝）。</summary>
    /// <param name="pfLevelDB">接收分贝值。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetMasterVolumeLevel(out float pfLevelDB);

    /// <summary>取得主音量（标量 0.0–1.0）。</summary>
    /// <param name="pfLevel">接收标量音量。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float pfLevel);

    /// <summary>设置指定声道音量（分贝）。</summary>
    /// <param name="nChannel">声道索引。</param>
    /// <param name="fLevelDB">分贝值。</param>
    /// <param name="pguidEventContext">事件上下文 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);

    /// <summary>设置指定声道音量（标量）。</summary>
    /// <param name="nChannel">声道索引。</param>
    /// <param name="fLevel">标量音量。</param>
    /// <param name="pguidEventContext">事件上下文 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);

    /// <summary>取得指定声道音量（分贝）。</summary>
    /// <param name="nChannel">声道索引。</param>
    /// <param name="pfLevelDB">接收分贝值。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);

    /// <summary>取得指定声道音量（标量）。</summary>
    /// <param name="nChannel">声道索引。</param>
    /// <param name="pfLevel">接收标量音量。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);

    /// <summary>设置静音。</summary>
    /// <param name="bMute">非 0 表示静音。</param>
    /// <param name="pguidEventContext">事件上下文 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetMute(int bMute, ref Guid pguidEventContext);

    /// <summary>取得静音状态。</summary>
    /// <param name="pbMute">接收静音状态（非 0 = 已静音）。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetMute(out int pbMute);
}

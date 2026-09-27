using System.Runtime.InteropServices;

namespace WinAudioRoute.Interop;

// =====================================================================
// 会话级音量/静音接口
//
// 提取来源：SonicRoute.Core/Interop/ISimpleAudioVolume.cs（MIT License,
// Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）
//
// 提取原则：GUID、方法顺序、[PreserveSig] 原样保留；仅调整 namespace 与可见性并补文档。
// 会话对象（IAudioSessionControl2）本身实现了该接口，可直接转换使用。
// =====================================================================

/// <summary>
/// 简单音频音量（会话级音量与静音）。
/// <c>ISimpleAudioVolume</c> = {87CE5498-68D6-44E5-9215-6DA47EF883D8}（audiopolicy.h）。
/// <para>
/// COM 接口按公开定义重新声明（自行实现），未复制第三方代码。
/// 方法顺序即 vtable 契约，不得调整。
/// </para>
/// </summary>
[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    /// <summary>设置会话音量（标量 0.0–1.0）。</summary>
    /// <param name="fLevel">标量音量。</param>
    /// <param name="EventContext">事件上下文 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetMasterVolume(float fLevel, ref Guid EventContext);

    /// <summary>取得会话音量（标量 0.0–1.0）。</summary>
    /// <param name="pfLevel">接收标量音量。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetMasterVolume(out float pfLevel);

    /// <summary>设置会话静音。</summary>
    /// <param name="bMute">非 0 表示静音。</param>
    /// <param name="EventContext">事件上下文 GUID。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int SetMute(int bMute, ref Guid EventContext);

    /// <summary>取得会话静音状态。</summary>
    /// <param name="pbMute">接收静音状态（非 0 = 已静音）。</param>
    /// <returns>HRESULT。</returns>
    [PreserveSig]
    int GetMute(out int pbMute);
}

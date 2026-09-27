using System.Runtime.InteropServices;

namespace WinAudioRoute.Interop;

// =====================================================================
// WASAPI 基础枚举 / COM 常量 / 结构体布局
//
// 提取来源：SonicRoute.Core/Interop/ComInterop.cs（MIT License,
// Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）
//
// 提取原则（Phase 1 约束）：
//   1. GUID / 方法顺序 / [PreserveSig] / [StructLayout] / P/Invoke 一律保留原样
//   2. 不重新实现 COM 接口，不用 CsWinRT 替代，不引入第三方音频库
//   3. 仅做：namespace 调整、public→internal、XML 文档、平台保护
//
// 权威来源：
//   - mmdeviceapi.h / audiopolicy.h / endpointvolume.h（Windows SDK 公开头文件）
//   - functiondiscoverykeys_devpkey.h（PKEY_Device_FriendlyName）
// =====================================================================

/// <summary>
/// 数据流方向。数值与公开头文件 <c>mmdeviceapi.h</c> 的 <c>EDataFlow</c> 一致。
/// </summary>
internal enum EDataFlow
{
    /// <summary>播放（输出）。</summary>
    eRender = 0,

    /// <summary>录音（输入）。</summary>
    eCapture = 1,

    /// <summary>全部方向。</summary>
    eAll = 2,
}

/// <summary>
/// 端点角色。数值与公开头文件 <c>mmdeviceapi.h</c> 的 <c>ERole</c> 一致。
/// <para>
/// 该枚举**不是**方向：输出/输入由端点设备本身决定，角色只区分 Console / Multimedia / Communications。
/// 数值与 <see cref="EDataFlow"/> 前两项重合，调用时必须区分语义。
/// </para>
/// </summary>
internal enum ERole
{
    /// <summary>系统默认设备。</summary>
    eConsole = 0,

    /// <summary>多媒体默认设备。</summary>
    eMultimedia = 1,

    /// <summary>通信默认设备。</summary>
    eCommunications = 2,
}

/// <summary>
/// 端点设备状态掩码。数值与公开头文件 <c>mmdeviceapi.h</c> 的 <c>DEVICE_STATE_*</c> 一致。
/// </summary>
/// <remarks>
/// <c>ALL</c> 为 <b>0x0000000F</b>，与 Windows SDK 的
/// <c>DEVICE_STATEMASK_ALL = 0x0000000f</c> 完全一致
/// （<c>C:\Program Files (x86)\Windows Kits\10\Include\*\um\mmdeviceapi.h</c> 已核对）。
/// <para>
/// <b>勘误记录</b>：SonicRoute 的等价声明曾把 <c>ALL</c> 写成 <c>0xFFFFFFFF</c>，
/// 这不属于任何 Windows 头文件定义，且会被 <c>EnumAudioEndpoints</c> 以
/// <c>E_INVALIDARG</c> 拒绝。本库采用头文件真值。
/// </para>
/// </remarks>
[Flags]
internal enum DeviceState : uint
{
    /// <summary>活动。</summary>
    ACTIVE = 0x1,

    /// <summary>已禁用。</summary>
    DISABLED = 0x2,

    /// <summary>不存在。</summary>
    NOTPRESENT = 0x4,

    /// <summary>未插入。</summary>
    UNPLUGGED = 0x8,

    /// <summary>全部真实状态（<c>DEVICE_STATEMASK_ALL</c> = 0x0000000F）。</summary>
    ALL = 0x0F,
}

/// <summary>
/// 音频会话状态。数值与公开头文件 <c>audiopolicy.h</c> 的 <c>InteropSessionState</c> 一致。
/// </summary>
internal enum InteropSessionState
{
    /// <summary>无音频流。</summary>
    Inactive = 0,

    /// <summary>正在播放/录音。</summary>
    Active = 1,

    /// <summary>已过期。</summary>
    Expired = 2,
}

/// <summary>COM 调用常量。</summary>
internal static class ComConstants
{
    /// <summary><c>CLSCTX_INPROC_SERVER</c>。</summary>
    public const int CLSCTX_INPROC_SERVER = 0x1;

    /// <summary><c>CLSCTX_ALL</c>。</summary>
    public const int CLSCTX_ALL = 0x17;

    /// <summary><c>STGM_READ</c>。</summary>
    public const int STGM_READ = 0x0;

    /// <summary><c>VarEnum.VT_LPWSTR</c>（= 31）。</summary>
    public const ushort VT_LPWSTR = 31;
}

// ---- PROPERTYKEY（functiondiscoverykeys_devpkey.h）----

/// <summary>
/// 属性键。布局与 <c>PROPERTYKEY</c>（wtypes.h）一致：GUID + DWORD，Pack = 4。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PROPERTYKEY
{
    /// <summary>属性集 GUID。</summary>
    public Guid fmtid;

    /// <summary>属性 ID。</summary>
    public uint pid;

    /// <summary>初始化属性键。</summary>
    /// <param name="fmtid">属性集 GUID。</param>
    /// <param name="pid">属性 ID。</param>
    public PROPERTYKEY(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}

/// <summary>本库使用的 <c>PKEY_*</c> 属性键。</summary>
internal static class PropertyKeys
{
    /// <summary>
    /// <c>PKEY_Device_FriendlyName</c> = {a45c254e-df1c-4efd-8020-67d146a850e0}, PID 14。
    /// </summary>
    public static readonly PROPERTYKEY PKEY_Device_FriendlyName =
        new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
}

// ---- PROPVARIANT ----

/// <summary>
/// <c>PROPVARIANT</c>（propidl.h）的最小可用声明。
/// <para>
/// <b>架构假设（重要）</b>：<c>Size = 24</c> 是 64 位布局
/// （8 字节头 <c>vt</c>/<c>wReserved</c>/… + 8 字节对齐的 16 字节联合体）。
/// 该布局在 x64 与 ARM64 上成立，在 x86 上<b>不成立</b>（x86 为 16 字节），
/// 误用会导致 <c>PropVariantClear</c> 读越界。因此本库拒绝在 32 位进程下运行，
/// 并在静态构造时用 <see cref="NativeMethods.AssertInteropLayout"/> 断言实际布局。
/// </para>
/// <para>
/// 这是为了读取 <c>IPropertyStore</c> 中的字符串属性而声明的最小结构；
/// 其余联合体成员（数值/文件时间等）尚未声明，需要时再补齐并同步验证布局。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PROPVARIANT
{
    /// <summary>类型标签（<c>VARTYPE</c>）。</summary>
    [FieldOffset(0)] public ushort vt;

    /// <summary>
    /// <c>VT_LPWSTR</c> 时的宽字符串指针。位于联合体偏移 0 → 结构体偏移 8。
    /// </summary>
    [FieldOffset(8)] public IntPtr pwszVal;
}

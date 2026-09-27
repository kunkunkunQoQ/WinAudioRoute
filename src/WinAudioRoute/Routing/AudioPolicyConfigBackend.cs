using System.Runtime.InteropServices;
using WinAudioRoute.Interop;

namespace WinAudioRoute.Routing;

// =====================================================================
// 按应用持久化音频设备路由的后端。
//
// 提取来源：SonicRoute.Core/Interop/AudioPolicyConfig.cs
// （MIT License, Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）
//
// ⚠️ 本文件使用【未公开的 Windows 内部 API】：
//     WinRT 可激活类 Windows.Media.Internal.AudioPolicyConfig
//   它不是 Microsoft 公开或受支持的 API。Microsoft 从未承诺其存在性、IID、
//   vtable 布局或行为稳定性。任何 Windows 累积更新都可能改变 vtable 槽位或 IID，
//   导致路由静默失效或进程内访问冲突。
//
// 提取原则（严格遵守，不允许重新设计底层接口）：
//   1. 激活类名原样保留
//   2. Win10 / Win11 两组 IID 原样保留
//   3. vtable 槽位号（25 / 26 / 27）原样保留
//   4. HSTRING 手动创建与手动删除原样保留
//       （.NET 8 的 DllImport 不支持 [MarshalAs(UnmanagedType.HString)] string）
//   5. 全程只用 blittable 类型（uint / int / enum / IntPtr）
//       （该 WinRT 激活工厂的 RCW 在 .NET 8 上无法通过 CLR 封送调用自定义 ComImport 接口）
//   6. 设备 ID 必须传完整设备接口路径（由 AudioDeviceIdConverter 生成）
//   7. 同时设置 eMultimedia 与 eConsole（SonicRoute 实测行为）
//
// 本文件为自行重新实现（手写 vtable 调用），仅参考 EarTrumpet 已验证的实现思路，
// 不存在直接复制其代码。
//
// 所有 raw vtable / IntPtr 均为 internal，绝不进入公共 API。
// =====================================================================

/// <summary>
/// 按应用路由后端：直接调用 <c>Windows.Media.Internal.AudioPolicyConfig</c> 的手写 vtable。
/// </summary>
internal sealed class AudioPolicyConfigBackend : IDisposable
{
    /// <summary>激活类 ID（未公开的 WinRT 内部类名）。</summary>
    internal const string ActivatableClassId = "Windows.Media.Internal.AudioPolicyConfig";

    /// <summary>Windows 11 21H2 的 Build 号。</summary>
    internal const int Windows11Build = 22000;

    // vtable 槽位（1-based，SonicRoute 实测）：Set=25、Get=26、ClearAll=27
    private const int VtblSlotSet = 25;
    private const int VtblSlotGet = 26;
    private const int VtblSlotClearAll = 27;

    /// <summary>Win11（21H2+）接口 IID。</summary>
    internal static readonly Guid IidWindows11 = new("ab3d4648-e242-459f-b02f-541c70306324");

    /// <summary>Win10 接口 IID。</summary>
    internal static readonly Guid IidDownlevel = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedDefaultAudioEndpointDelegate(
        IntPtr self, uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPersistedDefaultAudioEndpointDelegate(
        IntPtr self, uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearAllPersistedApplicationDefaultEndpointsDelegate(IntPtr self);

    private readonly object _sync = new();
    private IntPtr _factory;
    private bool _disposed;

    /// <summary>本实例是否已释放。</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_sync)
            {
                return _disposed;
            }
        }
    }

    /// <summary>持有的工厂指针是否为非 null（仅用于内部诊断）。</summary>
    internal bool HasFactory
    {
        get
        {
            lock (_sync)
            {
                return _factory != IntPtr.Zero;
            }
        }
    }

    /// <summary>当前系统应使用的 IID（按真实 Build 号判定）。</summary>
    internal static Guid GetExpectedIid() =>
        WindowsAudioEnvironment.OsBuild >= Windows11Build ? IidWindows11 : IidDownlevel;

    /// <summary>
    /// 只读能力探测：尝试激活策略接口并验证 QueryInterface 成功、vtable 槽位可读。
    /// <para>
    /// <b>不修改任何应用路由、不写入任何设置。</b>
    /// 结果会被句柄缓存（<see cref="EnsureFactory"/> 复用），因此探测本身不会重复激活。
    /// </para>
    /// </summary>
    /// <returns>探测结果（成功时 HRESULT ≥ 0，失败时保留原始 HRESULT）。</returns>
    internal RoutingProbeResult Probe()
    {
        if (!WindowsAudioEnvironment.IsSupportedPlatform)
        {
            return RoutingProbeResult.Failed("platform-not-supported", hresult: 0);
        }

        if (!WindowsAudioEnvironment.IsWindows)
        {
            return RoutingProbeResult.Failed("not-windows", hresult: 0);
        }

        try
        {
            IntPtr factory = EnsureFactory();
            if (factory == IntPtr.Zero)
            {
                return RoutingProbeResult.Failed("activation-returned-null-factory", hresult: 0);
            }

            // 读取 Set 槽的函数指针：为 null 说明 vtable 布局与预期不符
            IntPtr vtbl = Marshal.ReadIntPtr(factory);
            if (vtbl == IntPtr.Zero)
            {
                return RoutingProbeResult.Failed("vtable-pointer-null", hresult: 0);
            }

            IntPtr fn = Marshal.ReadIntPtr(vtbl, VtblSlotSet * IntPtr.Size);
            if (fn == IntPtr.Zero)
            {
                return RoutingProbeResult.Failed(
                    $"vtable-slot-{VtblSlotSet}-null", hresult: 0);
            }

            return RoutingProbeResult.Supported();
        }
        catch (AudioRoutingNotSupportedException ex)
        {
            return RoutingProbeResult.Failed(ex.Message, ex.HResult);
        }
        catch (Exception ex)
        {
            return RoutingProbeResult.Failed(ex.Message, ex.HResult);
        }
    }

    /// <summary>
    /// 把某进程的播放/录音设备持久化为指定设备。
    /// </summary>
    /// <param name="fullDeviceId">
    /// <b>完整设备接口路径</b>（由 <see cref="AudioDeviceIdConverter.ToFullDeviceId"/> 生成）。
    /// 传 <see langword="null"/> 表示清除该应用的持久化路由（跟随系统默认）。
    /// </param>
    /// <param name="processId">目标进程 ID。</param>
    /// <param name="flow">数据流方向（构造时确定）。</param>
    /// <returns>聚合后的 HRESULT 结果（两此调用的较小成功值）。</returns>
    internal int SetPersistedDefaultAudioEndpoint(string? fullDeviceId, int processId, AudioDataFlow flow)
    {
        IntPtr factory = EnsureFactory();

        IntPtr hstring = IntPtr.Zero;
        if (!string.IsNullOrWhiteSpace(fullDeviceId))
        {
            int hrCreate = NativeMethods.WindowsCreateString(
                fullDeviceId, (uint)fullDeviceId.Length, out hstring);
            if (hrCreate < 0)
            {
                throw new AudioOperationFailedException(
                    "WindowsCreateString failed for the device interface path.", hrCreate);
            }
        }

        try
        {
            var fn = GetMethod<SetPersistedDefaultAudioEndpointDelegate>(factory, VtblSlotSet);

            // SonicRoute 实测行为：同时设置 eMultimedia 与 eConsole
            int hrMultimedia = fn(factory, (uint)processId, MapFlow(flow), ERole.eMultimedia, hstring);
            int hrConsole = fn(factory, (uint)processId, MapFlow(flow), ERole.eConsole, hstring);

            return hrMultimedia < 0 ? hrMultimedia : hrConsole;
        }
        finally
        {
            if (hstring != IntPtr.Zero)
            {
                NativeMethods.WindowsDeleteString(hstring);
            }
        }
    }

    /// <summary>
    /// 读取某进程当前持久化的设备完整接口路径；未设置时返回 <see langword="null"/>。
    /// <para>读取的 role 与 SonicRoute 一致：<c>eMultimedia</c>。</para>
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <param name="flow">数据流方向。</param>
    /// <returns>完整设备接口路径；未设置时返回 <see langword="null"/>。</returns>
    internal string? GetPersistedDefaultAudioEndpoint(int processId, AudioDataFlow flow)
    {
        IntPtr factory = EnsureFactory();
        var fn = GetMethod<GetPersistedDefaultAudioEndpointDelegate>(factory, VtblSlotGet);

        int hr = fn(factory, (uint)processId, MapFlow(flow), ERole.eMultimedia, out IntPtr hstring);
        if (hr < 0 || hstring == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            IntPtr raw = NativeMethods.WindowsGetStringRawBuffer(hstring, out uint length);
            if (raw == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(raw, (int)length);
        }
        finally
        {
            NativeMethods.WindowsDeleteString(hstring);
        }
    }

    /// <summary>
    /// 清除<b>全部</b>应用的持久化音频设备（内存策略）。
    /// <para>
    /// <b>本方法刻意不进入公共 API</b>（Milestone B 约束）：它会一次性改写系统上所有应用的
    /// 路由，属于危险操作。仅保留为内部能力，供未来单独设计的 Dangerous/Advanced 能力使用。
    /// 另注：它只清内存策略，不清注册表磁盘条目（SonicRoute 的 <c>ResetAllPersistedEndpoints</c>
    /// 额外删除注册表，本库明确不实现该部分）。
    /// </para>
    /// </summary>
    /// <returns>HRESULT（旧系统返回 <c>E_NOTIMPL</c>）。</returns>
    internal int ClearAllPersistedApplicationDefaultEndpoints()
    {
        IntPtr factory = EnsureFactory();
        var fn = GetMethod<ClearAllPersistedApplicationDefaultEndpointsDelegate>(factory, VtblSlotClearAll);
        return fn(factory);
    }

    /// <summary>
    /// 释放持有的工厂指针（裸 COM 引用，必须用 <see cref="Marshal.Release"/>）。
    /// 幂等。
    /// </summary>
    public void Dispose()
    {
        IntPtr factory;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            factory = _factory;
            _factory = IntPtr.Zero;
        }

        if (factory != IntPtr.Zero)
        {
            try
            {
                Marshal.Release(factory);
            }
            catch
            {
                // 释放异常不得向外传播
            }
        }
    }

    /// <summary>
    /// 取得策略接口指针（QI 后的目标接口，含正确 vtable）。进程内复用，不重复激活。
    /// </summary>
    private IntPtr EnsureFactory()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_factory != IntPtr.Zero)
            {
                return _factory;
            }

            Guid iid = GetExpectedIid();

            int hrCreate = NativeMethods.WindowsCreateString(
                ActivatableClassId, (uint)ActivatableClassId.Length, out IntPtr hClass);
            if (hrCreate < 0)
            {
                throw new AudioRoutingNotSupportedException(
                    "WindowsCreateString failed for the AudioPolicyConfig activatable class id.", hrCreate);
            }

            try
            {
                int hr = NativeMethods.RoGetActivationFactory(hClass, ref iid, out IntPtr factoryUnknown);
                if (hr < 0)
                {
                    throw new AudioRoutingNotSupportedException(
                        "RoGetActivationFactory failed for Windows.Media.Internal.AudioPolicyConfig " +
                        "(this internal WinRT class may not be available on this Windows build).", hr);
                }

                // QI 到目标接口，然后立刻释放 RoGetActivationFactory 返回的那一份引用
                hr = Marshal.QueryInterface(factoryUnknown, ref iid, out _factory);
                Marshal.Release(factoryUnknown);

                if (hr < 0)
                {
                    throw new AudioRoutingNotSupportedException(
                        "QueryInterface failed for the AudioPolicyConfig policy interface " +
                        $"(IID {iid}).", hr);
                }

                return _factory;
            }
            finally
            {
                NativeMethods.WindowsDeleteString(hClass);
            }
        }
    }

    /// <summary>
    /// 从 vtable 读取指定槽位的函数指针并包装为委托。
    /// <para>
    /// 每次调用都会重新读取 vtable（槽位漂移时立即表现为访问冲突或错误 HRESULT，
    /// 而不是悄悄调用到错误的函数）。
    /// </para>
    /// </summary>
    private static T GetMethod<T>(IntPtr factory, int slot)
        where T : Delegate
    {
        IntPtr vtbl = Marshal.ReadIntPtr(factory);
        IntPtr fnPtr = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fnPtr);
    }

    private static EDataFlow MapFlow(AudioDataFlow flow) => flow switch
    {
        AudioDataFlow.Render => EDataFlow.eRender,
        AudioDataFlow.Capture => EDataFlow.eCapture,
        _ => throw new ArgumentOutOfRangeException(
            nameof(flow), flow, "Per-app routing requires a concrete flow (Render or Capture)."),
    };
}

/// <summary>
/// 路由能力探测结果。
/// </summary>
/// <param name="IsSupported">是否受支持。</param>
/// <param name="Reason">失败原因（英文、中性）；成功时为 <see langword="null"/>。</param>
/// <param name="HResult">失败时的原始 HRESULT；无 COM 失败时为 0。</param>
internal readonly record struct RoutingProbeResult(bool IsSupported, string? Reason, int HResult)
{
    /// <summary>构造成功结果。</summary>
    internal static RoutingProbeResult Supported() => new(true, null, 0);

    /// <summary>构造失败结果。</summary>
    internal static RoutingProbeResult Failed(string reason, int hresult) => new(false, reason, hresult);
}

using System.Runtime.Versioning;

namespace WinAudioRoute;

/// <summary>
/// 按应用音频路由的能力状态。
/// <para>
/// <b>为什么需要显式能力状态</b>：按应用路由依赖未公开的 WinRT 内部类
/// <c>Windows.Media.Internal.AudioPolicyConfig</c>。它可能因为 Windows 版本、
/// 累积更新、企业策略或组件缺失而不可用。SDK 不允许"看起来支持、调用时才静默失败"，
/// 因此把能力探测结果作为一等公民暴露出来。
/// </para>
/// <para>
/// 探测本身是<b>只读</b>的：只做 <c>RoGetActivationFactory</c> + <c>QueryInterface</c> +
/// vtable 槽位可读性检查，不修改任何应用的路由设置。
/// </para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public readonly struct AudioRoutingCapability
{
    internal AudioRoutingCapability(bool isSupported, string? reason, int hResult)
    {
        IsSupported = isSupported;
        Reason = reason;
        HResult = hResult;
    }

    /// <summary>当前系统是否支持按应用音频路由。</summary>
    public bool IsSupported { get; }

    /// <summary>
    /// 不支持的原因（英文、中性）；支持时为 <see langword="null"/>。
    /// 可直接用于诊断日志。
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// 不支持时的原始 HRESULT（例如 <c>REGDB_E_CLASSNOTREG</c> 0x80040154、
    /// <c>E_NOINTERFACE</c> 0x80004002）；无 COM 失败时为 0。
    /// </summary>
    public int HResult { get; }

    /// <summary>
    /// 不支持时 HRESULT 的十六进制形式；为 0 时返回 <see langword="null"/>。
    /// </summary>
    public string? HResultHex => HResult == 0 ? null : $"0x{HResult:X8}";

    /// <summary>诊断字符串，英文、中性。</summary>
    public override string ToString() => IsSupported
        ? "Per-app routing: supported"
        : $"Per-app routing: not supported ({Reason ?? "unknown"}{(HResultHex is null ? "" : $", HRESULT {HResultHex}")})";
}

using WinAudioRoute.Interop;

namespace WinAudioRoute.Routing;

// =====================================================================
// 设备 ID 在"短 ID"与"完整设备接口路径"之间的转换。
//
// 提取来源：SonicRoute.Core/Interop/AudioPolicyConfig.cs 的
//   GenerateDeviceId / EnsureFullDeviceId / UnpackDeviceId
// （MIT License, Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）
//
// 两个世界的 ID 形式不同，这是本库最容易踩坑的细节之一：
//   1) IMMDevice.GetId()                       -> 短 ID
//        形如 {0.0.0.00000000}.{guid}
//      用于：IMMDeviceEnumerator.GetDevice、IPolicyConfig.SetDefaultEndpoint
//   2) Windows.Media.Internal.AudioPolicyConfig -> 完整设备接口路径
//        形如 \\?\SWD#MMDEVAPI#{短ID}#{方向接口 GUID}
//      用于：SetPersistedDefaultAudioEndpoint / GetPersistedDefaultAudioEndpoint
//
// 实测结论（沿用 SonicRoute 的验证）：把短 ID 直接传给按应用路由 API 无效；
// 把完整路径传给 IPolicyConfig.SetDefaultEndpoint 会返回 E_INVALIDARG。
// =====================================================================

/// <summary>
/// 音频设备 ID 的打包/解包工具。
/// <para>
/// <b>纯字符串逻辑</b>，不涉及 COM，因此完全可单测（见 <c>AudioDeviceIdConverterTests</c>）。
/// </para>
/// </summary>
internal static class AudioDeviceIdConverter
{
    /// <summary>MMDevice 完整设备接口路径的固定前缀。</summary>
    internal const string MmdevapiToken = @"\\?\SWD#MMDEVAPI#";

    /// <summary>播放（Render）端点接口 GUID 后缀。</summary>
    internal const string RenderInterfaceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    /// <summary>录音（Capture）端点接口 GUID 后缀。</summary>
    internal const string CaptureInterfaceSuffix = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    /// <summary>
    /// 该 ID 是否已经是完整设备接口路径。
    /// </summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <returns>已是完整路径返回 <see langword="true"/>。</returns>
    internal static bool IsFullDeviceId(string? deviceId) =>
        !string.IsNullOrEmpty(deviceId)
        && deviceId.StartsWith(MmdevapiToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把短 ID 包装成完整设备接口路径。已是完整路径时原样返回。
    /// </summary>
    /// <param name="deviceId">短 ID 或完整路径。</param>
    /// <param name="flow">方向，决定路径尾部的接口 GUID。</param>
    /// <returns>完整设备接口路径。</returns>
    /// <exception cref="ArgumentException"><paramref name="deviceId"/> 为空。</exception>
    internal static string ToFullDeviceId(string deviceId, AudioDataFlow flow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (IsFullDeviceId(deviceId))
        {
            return deviceId;
        }

        return MmdevapiToken + deviceId + GetInterfaceSuffix(flow);
    }

    /// <summary>
    /// 从完整设备接口路径还原短 ID；传入的已是短 ID 时原样返回。
    /// </summary>
    /// <param name="deviceId">短 ID 或完整路径。</param>
    /// <returns>短 ID；输入为空时返回 <see cref="string.Empty"/>。</returns>
    internal static string ToShortDeviceId(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return string.Empty;
        }

        string value = deviceId;

        if (value.StartsWith(MmdevapiToken, StringComparison.OrdinalIgnoreCase))
        {
            value = value[MmdevapiToken.Length..];
        }

        value = StripSuffix(value, RenderInterfaceSuffix);
        value = StripSuffix(value, CaptureInterfaceSuffix);

        return value;
    }

    /// <summary>
    /// 取得完整路径中隐含的方向；无法判定时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="deviceId">设备 ID（短或完整）。</param>
    /// <returns>方向，无法判定时为 <see langword="null"/>。</returns>
    internal static AudioDataFlow? TryGetFlow(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        if (deviceId.EndsWith(RenderInterfaceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return AudioDataFlow.Render;
        }

        if (deviceId.EndsWith(CaptureInterfaceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return AudioDataFlow.Capture;
        }

        // 短 ID 本身不含方向信息
        return null;
    }

    /// <summary>
    /// 判断完整路径携带的方向是否与期望方向一致。
    /// 短 ID（无方向信息）视为兼容。
    /// </summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <param name="expected">期望方向。</param>
    /// <returns>兼容返回 <see langword="true"/>。</returns>
    internal static bool IsFlowCompatible(string? deviceId, AudioDataFlow expected)
    {
        AudioDataFlow? actual = TryGetFlow(deviceId);
        return actual is null || actual == expected;
    }

    private static string GetInterfaceSuffix(AudioDataFlow flow) => flow switch
    {
        AudioDataFlow.Render => RenderInterfaceSuffix,
        AudioDataFlow.Capture => CaptureInterfaceSuffix,
        _ => throw new ArgumentOutOfRangeException(
            nameof(flow), flow, "Per-app routing requires a concrete flow (Render or Capture)."),
    };

    private static string StripSuffix(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? value[..^suffix.Length]
            : value;
}

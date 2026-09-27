namespace WinAudioRoute;

/// <summary>
/// 标量音量（0.0–1.0）与百分比的转换与钳制。
/// <para>
/// <b>SDK 的核心存储与传输格式是标量 <see cref="float"/>（0.0–1.0）</b>，
/// 与 Windows <c>ISimpleAudioVolume</c> / <c>IAudioEndpointVolume</c> 的
/// <c>SetMasterVolume</c> / <c>SetMasterVolumeLevelScalar</c> 一致。
/// 百分比（0–100 的 <see cref="int"/>）只作为便捷辅助，用于贴近 UI 与 CLI 的表达。
/// </para>
/// </summary>
public static class AudioVolume
{
    /// <summary>最小标量音量。</summary>
    public const float MinScalar = 0.0f;

    /// <summary>最大标量音量。</summary>
    public const float MaxScalar = 1.0f;

    /// <summary>最小百分比。</summary>
    public const int MinPercent = 0;

    /// <summary>最大百分比。</summary>
    public const int MaxPercent = 100;

    /// <summary>
    /// 把任意标量钳制到 [<see cref="MinScalar"/>, <see cref="MaxScalar"/>]。
    /// <see cref="float.NaN"/> 会被钳制为 <see cref="MinScalar"/>。
    /// </summary>
    /// <param name="scalar">输入标量。</param>
    /// <returns>钳制后的标量。</returns>
    public static float Clamp(float scalar)
    {
        if (float.IsNaN(scalar))
        {
            return MinScalar;
        }

        return scalar < MinScalar ? MinScalar : scalar > MaxScalar ? MaxScalar : scalar;
    }

    /// <summary>
    /// 把百分比钳制到 [<see cref="MinPercent"/>, <see cref="MaxPercent"/>]。
    /// </summary>
    /// <param name="percent">输入百分比。</param>
    /// <returns>钳制后的百分比。</returns>
    public static int ClampPercent(int percent) =>
        percent < MinPercent ? MinPercent : percent > MaxPercent ? MaxPercent : percent;

    /// <summary>
    /// 百分比 → 标量（先钳制到 0–100，再除以 100）。
    /// </summary>
    /// <param name="percent">百分比。</param>
    /// <returns>0.0–1.0 的标量。</returns>
    public static float FromPercent(int percent) => ClampPercent(percent) / 100f;

    /// <summary>
    /// 标量 → 百分比（先钳制到 0.0–1.0，再四舍五入）。
    /// </summary>
    /// <param name="scalar">标量。</param>
    /// <returns>0–100 的百分比。</returns>
    public static int ToPercent(float scalar) =>
        (int)Math.Round(Clamp(scalar) * 100f, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 校验标量是否在 [<see cref="MinScalar"/>, <see cref="MaxScalar"/>] 内。
    /// </summary>
    /// <param name="scalar">待校验的标量。</param>
    /// <returns>在范围内返回 <see langword="true"/>。</returns>
    public static bool IsValidScalar(float scalar) =>
        !float.IsNaN(scalar) && scalar >= MinScalar && scalar <= MaxScalar;
}

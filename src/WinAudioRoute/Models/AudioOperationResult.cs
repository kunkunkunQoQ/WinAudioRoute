namespace WinAudioRoute;

/// <summary>
/// 多目标音频操作的逐项结果。
/// <para>
/// 写入类操作（应用音量/静音、按 Role 设置默认设备）会作用于多个目标
/// （同一应用的多个会话、或三个 Role），因此不能只返回 <see langword="bool"/> 而丢掉部分失败信息。
/// </para>
/// <para>
/// <b>判断语义</b>：
/// <list type="bullet">
///   <item><description><see cref="IsSuccess"/>：全部目标成功。</description></item>
///   <item><description><see cref="IsPartialSuccess"/>：部分成功、部分失败。</description></item>
///   <item><description><see cref="IsTotalFailure"/>：有目标但全部失败。</description></item>
/// </list>
/// 「没有目标」是另一种情况（<see cref="Total"/> 为 0），此时三项均为 <see langword="false"/>；
/// 调用方应先检查 <see cref="Total"/>。
/// </para>
/// </summary>
public sealed record AudioOperationResult
{
    /// <summary>目标总数。</summary>
    public int Total { get; init; }

    /// <summary>成功数。</summary>
    public int Succeeded { get; init; }

    /// <summary>失败数。</summary>
    public int Failed { get; init; }

    /// <summary>失败目标的明细（含 HRESULT 与目标标识）。</summary>
    public IReadOnlyList<AudioOperationFailure> Failures { get; init; } = [];

    /// <summary>全部目标是否都成功（要求至少有一个目标）。</summary>
    public bool IsSuccess => Total > 0 && Failed == 0;

    /// <summary>是否部分成功（至少一个成功、至少一个失败）。</summary>
    public bool IsPartialSuccess => Succeeded > 0 && Failed > 0;

    /// <summary>是否全部失败（至少有一个目标且无一成功）。</summary>
    public bool IsTotalFailure => Total > 0 && Succeeded == 0;

    /// <summary>构造"全部成功"的结果。</summary>
    /// <param name="succeeded">成功的目标数。</param>
    /// <returns>结果对象。</returns>
    public static AudioOperationResult AllSucceeded(int succeeded) => new()
    {
        Total = succeeded,
        Succeeded = succeeded,
        Failed = 0,
    };

    /// <summary>构造"部分成功"的结果。</summary>
    /// <param name="succeeded">成功的目标数。</param>
    /// <param name="failures">失败明细。</param>
    /// <returns>结果对象。</returns>
    public static AudioOperationResult Partial(int succeeded, IReadOnlyList<AudioOperationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return new AudioOperationResult
        {
            Total = succeeded + failures.Count,
            Succeeded = succeeded,
            Failed = failures.Count,
            Failures = failures,
        };
    }

    /// <summary>构造"全部失败"的结果。</summary>
    /// <param name="failures">失败明细。</param>
    /// <returns>结果对象。</returns>
    public static AudioOperationResult AllFailed(IReadOnlyList<AudioOperationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return new AudioOperationResult
        {
            Total = failures.Count,
            Succeeded = 0,
            Failed = failures.Count,
            Failures = failures,
        };
    }

    /// <summary>
    /// 出现次数最多的失败 HRESULT；没有失败时返回 0。
    /// </summary>
    public int DominantHResult => Failures.Count == 0
        ? 0
        : Failures.GroupBy(f => f.HResult).OrderByDescending(g => g.Count()).First().Key;

    /// <summary>诊断字符串，英文、中性。</summary>
    public override string ToString() =>
        $"Total={Total}, Succeeded={Succeeded}, Failed={Failed}";
}

/// <summary>
/// 单个失败目标的明细。
/// </summary>
/// <param name="Target">失败目标的标识（会话实例 ID、设备 ID 或 Role 名）。</param>
/// <param name="HResult">失败的 HRESULT；无 COM 失败时为 0。</param>
/// <param name="Message">英文、中性的失败描述。</param>
public readonly record struct AudioOperationFailure(string Target, int HResult, string Message);

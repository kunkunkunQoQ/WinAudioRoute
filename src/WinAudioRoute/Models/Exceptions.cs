namespace WinAudioRoute;

/// <summary>
/// WinAudioRoute 抛出的所有异常的基类。
/// <para>
/// <b>HRESULT 保留</b>：任何源自 Windows COM / WASAPI 调用的失败都会把原始 HRESULT
/// 保留在 <see cref="HResult"/> 中（非 COM 失败时为 <c>0</c>），便于调用方精确诊断，
/// 例如按 <c>AUDCLNT_E_DEVICE_INVALIDATED</c> (0x88890004) 决定是否重试。
/// </para>
/// <para>
/// <b>消息语言</b>：所有异常消息为英文、中性、面向机器与开发者，不含任何 UI 文案或本地化字符串。
/// 本地化是调用方的职责。
/// </para>
/// </summary>
public class WinAudioException : Exception
{
    /// <summary>
    /// 初始化 <see cref="WinAudioException"/> 的新实例。
    /// </summary>
    public WinAudioException()
    {
    }

    /// <summary>
    /// 使用消息初始化 <see cref="WinAudioException"/> 的新实例。
    /// </summary>
    /// <param name="message">英文、中性的错误描述。</param>
    public WinAudioException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 使用消息与内部异常初始化 <see cref="WinAudioException"/> 的新实例。
    /// </summary>
    /// <param name="message">英文、中性的错误描述。</param>
    /// <param name="innerException">导致本异常的底层异常。</param>
    public WinAudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// 使用消息与原始 HRESULT 初始化 <see cref="WinAudioException"/> 的新实例。
    /// </summary>
    /// <param name="message">英文、中性的错误描述。</param>
    /// <param name="hresult">原始 HRESULT；无 COM 失败时为 0。</param>
    public WinAudioException(string message, int hresult)
        : base(message)
    {
        this.HResult = hresult;
    }

    /// <summary>
    /// 原始 HRESULT。非 COM 失败时为 <c>0</c>。
    /// </summary>
    /// <remarks>
    /// 刻意遮蔽 <see cref="Exception.HResult"/>，使其成为可读的公开属性；
    /// 同时通过受保护访问器把同一个值写回基类字段，因此
    /// <c>((Exception)this).HResult</c> 与本属性始终一致。
    /// </remarks>
    public new int HResult
    {
        get => base.HResult;
        protected set => base.HResult = value;
    }

    /// <summary>
    /// 以十六进制形式返回 HRESULT（例如 <c>0x88890004</c>），便于日志记录。
    /// </summary>
    public string HResultHex => $"0x{HResult:X8}";
}

/// <summary>
/// 未能解析到音频设备（ID 无匹配、名称无匹配，或枚举结果中没有该设备）。
/// </summary>
public sealed class AudioDeviceNotFoundException : WinAudioException
{
    /// <summary>初始化新实例。</summary>
    /// <param name="message">英文、中性的错误描述。</param>
    public AudioDeviceNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>初始化新实例。</summary>
    /// <param name="message">英文、中性的错误描述。</param>
    /// <param name="hresult">原始 HRESULT；无 COM 失败时为 0。</param>
    public AudioDeviceNotFoundException(string message, int hresult)
        : base(message, hresult)
    {
    }

    /// <summary>调用方提供的查询文本（设备 ID 或名称）。</summary>
    public string? Query { get; init; }

    /// <summary>查询时使用的数据流方向；未限定时为 <see langword="null"/>。</summary>
    public AudioDataFlow? Flow { get; init; }
}

/// <summary>
/// 设备查询命中了多个设备；调用方必须自行消歧，库不会替调用方"选第一个"。
/// </summary>
public sealed class AmbiguousAudioDeviceException : WinAudioException
{
    /// <summary>初始化新实例。</summary>
    /// <param name="message">英文、中性的错误描述。</param>
    /// <param name="candidates">命中的全部候选设备。</param>
    public AmbiguousAudioDeviceException(string message, IReadOnlyList<AudioDevice> candidates)
        : base($"{message} Candidates: {candidates?.Count ?? 0}.")
    {
        Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
    }

    /// <summary>调用方提供的查询文本（设备 ID 或名称）。</summary>
    public string? Query { get; init; }

    /// <summary>查询时使用的数据流方向；未限定时为 <see langword="null"/>。</summary>
    public AudioDataFlow? Flow { get; init; }

    /// <summary>保序的全部候选设备。</summary>
    public IReadOnlyList<AudioDevice> Candidates { get; }
}

/// <summary>
/// 未能解析到音频会话（指定 PID 或进程名没有任何会话，或目标会话已消失）。
/// </summary>
public sealed class AudioSessionNotFoundException : WinAudioException
{
    /// <summary>初始化新实例。</summary>
    /// <param name="message">英文、中性的错误描述。</param>
    public AudioSessionNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>初始化新实例。</summary>
    /// <param name="message">英文、中性的错误描述。</param>
    /// <param name="hresult">原始 HRESULT；无 COM 失败时为 0。</param>
    public AudioSessionNotFoundException(string message, int hresult)
        : base(message, hresult)
    {
    }

    /// <summary>调用方提供的查询文本（PID 字符串或进程名）。</summary>
    public string? Query { get; init; }
}

/// <summary>
/// 进程名查询命中了多个不同 PID 的进程；调用方必须自行消歧。
/// </summary>
public sealed class AmbiguousAudioSessionException : WinAudioException
{
    /// <summary>初始化新实例。</summary>
    /// <param name="message">英文、中性的错误描述。</param>
    /// <param name="candidateProcessIds">命中的全部候选 PID（升序）。</param>
    public AmbiguousAudioSessionException(string message, IReadOnlyList<int> candidateProcessIds)
        : base($"{message} Candidates: {candidateProcessIds?.Count ?? 0} process(es).")
    {
        CandidateProcessIds = candidateProcessIds ?? throw new ArgumentNullException(nameof(candidateProcessIds));
    }

    /// <summary>调用方提供的进程名。</summary>
    public string? Query { get; init; }

    /// <summary>保序的全部候选 PID（升序）。</summary>
    public IReadOnlyList<int> CandidateProcessIds { get; }

    /// <summary>
    /// 命中的会话总数（可能大于候选 PID 数，因为一个 PID 可以有多个会话）。
    /// </summary>
    public int CandidateSessionCount { get; init; }
}

/// <summary>
/// 音频操作在底层失败（COM 调用返回失败 HRESULT，或全部目标会话/设备都写入失败）。
/// </summary>
public sealed class AudioOperationFailedException : WinAudioException
{
    /// <summary>初始化新实例。</summary>
    /// <param name="message">英文、中性的错误描述。</param>
    /// <param name="hresult">原始 HRESULT；无 COM 失败时为 0。</param>
    public AudioOperationFailedException(string message, int hresult)
        : base(message, hresult)
    {
    }

    /// <summary>初始化新实例。</summary>
    /// <param name="message">英文、中性的错误描述。</param>
    /// <param name="hresult">原始 HRESULT；无 COM 失败时为 0。</param>
    /// <param name="operationResult">失败操作的逐项结果。</param>
    public AudioOperationFailedException(string message, int hresult, AudioOperationResult operationResult)
        : base(message, hresult)
    {
        OperationResult = operationResult;
    }

    /// <summary>失败操作的逐项结果；不可用时为 <see langword="null"/>。</summary>
    public AudioOperationResult? OperationResult { get; }
}

/// <summary>
/// 所请求的能力在当前系统上不受支持（例如未来阶段的按应用路由 API 不可用，
/// 或设备不支持所请求的接口）。
/// </summary>
public sealed class AudioRoutingNotSupportedException : WinAudioException
{
    /// <summary>初始化新实例。</summary>
    /// <param name="message">英文、中性的错误描述。</param>
    public AudioRoutingNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>初始化新实例。</summary>
    /// <param name="message">英文、中性的错误描述。</param>
    /// <param name="hresult">原始 HRESULT；无 COM 失败时为 0。</param>
    public AudioRoutingNotSupportedException(string message, int hresult)
        : base(message, hresult)
    {
    }
}

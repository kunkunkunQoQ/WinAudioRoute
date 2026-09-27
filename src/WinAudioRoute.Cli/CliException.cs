namespace WinAudioRoute.Cli;

/// <summary>
/// CLI 退出码。数值一旦发布即视为契约，不得随意改动。
/// </summary>
internal enum ExitCode
{
    /// <summary>成功。</summary>
    Success = 0,

    /// <summary>一般错误（未分类的 <c>WinAudioException</c> 或意外异常）。</summary>
    GeneralError = 1,

    /// <summary>参数或用法错误。</summary>
    UsageError = 2,

    /// <summary>设备未找到（<c>AudioDeviceNotFoundException</c>）。</summary>
    DeviceNotFound = 3,

    /// <summary>设备名歧义（<c>AmbiguousAudioDeviceException</c>）。</summary>
    AmbiguousDevice = 4,

    /// <summary>会话未找到（<c>AudioSessionNotFoundException</c>）。</summary>
    SessionNotFound = 5,

    /// <summary>功能不受支持（<c>AudioRoutingNotSupportedException</c>）。</summary>
    NotSupported = 6,

    /// <summary>访问被拒绝（HRESULT <c>E_ACCESSDENIED</c>）。</summary>
    AccessDenied = 7,
}

/// <summary>
/// 表示一次可预期的 CLI 失败：携带退出码与面向 stderr 的消息。
/// </summary>
internal sealed class CliException : Exception
{
    /// <summary>创建 CLI 异常。</summary>
    /// <param name="exitCode">应返回的退出码。</param>
    /// <param name="message">面向用户的消息（英文、中性）。</param>
    public CliException(ExitCode exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    /// <summary>创建 CLI 异常。</summary>
    /// <param name="exitCode">应返回的退出码。</param>
    /// <param name="message">面向用户的消息。</param>
    /// <param name="inner">内部异常。</param>
    public CliException(ExitCode exitCode, string message, Exception inner)
        : base(message, inner)
    {
        ExitCode = exitCode;
    }

    /// <summary>应返回的退出码。</summary>
    public ExitCode ExitCode { get; }

    /// <summary>可选的候选列表（用于歧义错误）。</summary>
    public IReadOnlyList<string> Candidates { get; init; } = [];
}

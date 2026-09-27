namespace WinAudioRoute.Internal;

/// <summary>
/// 进程名 / PID → 会话列表的解析（纯逻辑，不触碰 COM）。
/// <para>
/// <b>进程名规范化</b>：<c>chrome</c>、<c>chrome.exe</c>、<c>CHROME.EXE</c> 视为同一名称。
/// 规则是把比较双方都去掉可选的 <c>.exe</c> 后缀后做序数忽略大小写比较。
/// 之所以同时容忍带与不带 <c>.exe</c>：Windows 的会话进程名（<c>Process.ProcessName</c>）
/// <b>不含</b>扩展名，而命令行/配置文件的使用者习惯写 <c>.exe</c>。
/// </para>
/// <para>
/// <b>歧义必须显式失败</b>：多个不同 PID 使用同一进程名时，本类返回全部候选；
/// 由调用方决定是抛 <see cref="AmbiguousAudioSessionException"/> 还是列出候选让用户选择。
/// 本类绝不"取第一个 PID"。
/// </para>
/// </summary>
internal static class SessionResolver
{
    /// <summary>
    /// 去掉可选的 <c>.exe</c> 后缀（忽略大小写）并去除首尾空白。
    /// <para>
    /// <b>保留原始大小写</b>：规范化只处理扩展名与空白，不做大小写转换；
    /// 大小写不敏感由比较点（<see cref="ProcessNameMatches"/>）负责。
    /// 这样规范化结果可以安全地用于显示或日志。
    /// </para>
    /// </summary>
    /// <param name="name">进程名或带扩展名的可执行文件名。</param>
    /// <returns>不带扩展名的名称；输入为空时返回 <see cref="string.Empty"/>。</returns>
    public static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    /// <summary>给定的会话进程名是否等于目标进程名（规范化后，忽略大小写）。</summary>
    /// <param name="candidate">会话中的进程名。</param>
    /// <param name="target">目标进程名。</param>
    /// <returns>相等返回 <see langword="true"/>。</returns>
    public static bool ProcessNameMatches(string? candidate, string? target)
    {
        string normalizedTarget = NormalizeProcessName(target);
        if (normalizedTarget.Length == 0)
        {
            return false;
        }

        return string.Equals(
            NormalizeProcessName(candidate),
            normalizedTarget,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按 PID 解析会话。</summary>
    /// <param name="sessions">全部会话。</param>
    /// <param name="processId">进程 ID。</param>
    /// <returns>解析结果。</returns>
    public static SessionResolutionResult ResolveByProcessId(
        IEnumerable<AudioSession> sessions,
        int processId)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        List<AudioSession> matches = sessions.Where(s => s.ProcessId == processId).ToList();

        return matches.Count == 0
            ? SessionResolutionResult.NotFound(processId.ToString())
            : SessionResolutionResult.Found([processId], matches).WithQuery(processId.ToString());
    }

    /// <summary>按进程名解析会话（支持带/不带 <c>.exe</c>，忽略大小写）。</summary>
    /// <param name="sessions">全部会话。</param>
    /// <param name="processName">进程名。</param>
    /// <returns>解析结果。</returns>
    public static SessionResolutionResult ResolveByProcessName(
        IEnumerable<AudioSession> sessions,
        string processName)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        string normalized = NormalizeProcessName(processName);
        if (normalized.Length == 0)
        {
            return SessionResolutionResult.NotFound(processName ?? string.Empty);
        }

        List<AudioSession> matches = sessions
            .Where(s => ProcessNameMatches(s.ProcessName, normalized))
            .ToList();

        if (matches.Count == 0)
        {
            return SessionResolutionResult.NotFound(processName);
        }

        List<int> pids = [.. matches.Select(s => s.ProcessId).Distinct().Order()];

        SessionResolutionResult result = pids.Count == 1
            ? SessionResolutionResult.Found(pids, matches)
            : SessionResolutionResult.Ambiguous(pids, matches);

        return result.WithQuery(processName);
    }
}

/// <summary>
/// 会话解析的结果。
/// </summary>
internal sealed class SessionResolutionResult
{
    private SessionResolutionResult(
        string query,
        IReadOnlyList<int> processIds,
        IReadOnlyList<AudioSession> sessions,
        bool isAmbiguous)
    {
        Query = query;
        ProcessIds = processIds;
        Sessions = sessions;
        IsAmbiguous = isAmbiguous;
    }

    /// <summary>调用方提供的查询文本。</summary>
    public string Query { get; }

    /// <summary>命中的 PID（升序）；无命中时为空。</summary>
    public IReadOnlyList<int> ProcessIds { get; }

    /// <summary>命中的会话（会话粒度）。</summary>
    public IReadOnlyList<AudioSession> Sessions { get; }

    /// <summary>是否命中多个不同 PID。</summary>
    public bool IsAmbiguous { get; }

    /// <summary>是否完全没有命中。</summary>
    public bool IsNotFound => ProcessIds.Count == 0;

    /// <summary>是否唯一命中（恰好一个 PID）。</summary>
    public bool IsUnique => !IsAmbiguous && ProcessIds.Count == 1;

    /// <summary>构造唯一命中结果。</summary>
    /// <param name="processIds">命中 PID。</param>
    /// <param name="sessions">命中会话。</param>
    /// <returns>结果。</returns>
    public static SessionResolutionResult Found(IReadOnlyList<int> processIds, IReadOnlyList<AudioSession> sessions) =>
        new(string.Empty, processIds, sessions, isAmbiguous: false);

    /// <summary>构造多 PID 命中结果。</summary>
    /// <param name="processIds">候选 PID（升序）。</param>
    /// <param name="sessions">候选会话。</param>
    /// <returns>结果。</returns>
    public static SessionResolutionResult Ambiguous(IReadOnlyList<int> processIds, IReadOnlyList<AudioSession> sessions) =>
        new(string.Empty, processIds, sessions, isAmbiguous: true);

    /// <summary>构造无命中结果。</summary>
    /// <param name="query">原始查询文本。</param>
    /// <returns>结果。</returns>
    public static SessionResolutionResult NotFound(string query) =>
        new(query, [], [], isAmbiguous: false);

    /// <summary>附加查询文本（便于在异常里携带原始输入）。</summary>
    /// <param name="query">查询文本。</param>
    /// <returns>带查询文本的新结果。</returns>
    public SessionResolutionResult WithQuery(string query) =>
        new(query, ProcessIds, Sessions, IsAmbiguous);
}

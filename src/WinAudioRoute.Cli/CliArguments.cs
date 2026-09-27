namespace WinAudioRoute.Cli;

/// <summary>
/// 解析后的命令行参数。
/// <para>
/// <b>设计取舍</b>：刻意不引入 <c>System.CommandLine</c> 等框架——CLI 只需支持
/// 少量固定命令，手写解析可以精确控制退出码、stdout/stderr 分离与错误措辞，
/// 且让库保持零第三方运行时依赖。
/// </para>
/// </summary>
internal sealed class CliArguments
{
    /// <summary>命令名（已小写）。</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>位置参数（不含命令名、不含任何选项）。</summary>
    public List<string> Positional { get; } = [];

    /// <summary>开关与取值选项。</summary>
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否请求 JSON 输出。</summary>
    public bool Json => Has("json");

    /// <summary>选项是否存在（开关或带值皆算）。</summary>
    /// <param name="name">选项名（不带前缀）。</param>
    /// <returns>存在返回 <see langword="true"/>。</returns>
    public bool Has(string name) => Options.ContainsKey(name);

    /// <summary>取得选项值；不存在或未带值时返回 <see langword="null"/>。</summary>
    /// <param name="name">选项名。</param>
    /// <returns>选项值。</returns>
    public string? Value(string name) => Options.TryGetValue(name, out string? value) ? value : null;

    /// <summary>取得必填选项值，缺失时报用法错误。</summary>
    /// <param name="name">选项名。</param>
    /// <returns>选项值。</returns>
    /// <exception cref="CliException">缺失或为空。</exception>
    public string RequiredValue(string name)
    {
        string? value = Value(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliException(ExitCode.UsageError, $"Option --{name} requires a value.");
        }

        return value;
    }

    /// <summary>取得第 <paramref name="index"/> 个位置参数；缺失时报用法错误。</summary>
    /// <param name="index">从 0 开始的索引。</param>
    /// <param name="name">用于错误消息的名称。</param>
    /// <returns>位置参数值。</returns>
    /// <exception cref="CliException">缺失。</exception>
    public string RequiredPositional(int index, string name)
    {
        if (Positional.Count <= index)
        {
            throw new CliException(ExitCode.UsageError, $"Missing required argument <{name}>.");
        }

        return Positional[index];
    }

    /// <summary>解析命令行。</summary>
    /// <param name="args">原始参数（不含可执行文件名）。</param>
    /// <returns>解析结果；无命令时 <see cref="Command"/> 为空串。</returns>
    /// <exception cref="CliException">参数形态非法（例如选项缺少取值）。</exception>
    public static CliArguments Parse(string[] args)
    {
        var parsed = new CliArguments();

        int index = 0;

        // 跳过硬性全局开关；命令是第一个非选项参数
        while (index < args.Length && args[index].StartsWith('-'))
        {
            string global = StripPrefix(args[index]);
            if (IsKnownSwitch(global))
            {
                parsed.Options[global] = null;
                index++;
                continue;
            }

            throw new CliException(ExitCode.UsageError, $"Unknown global option '--{global}'. Use --help.");
        }

        if (index >= args.Length)
        {
            return parsed;
        }

        parsed.Command = args[index].ToLowerInvariant();
        index++;

        for (; index < args.Length; index++)
        {
            string current = args[index];

            if (current.StartsWith('-'))
            {
                string name = StripPrefix(current);

                // 支持 --option=value 与 --option value
                int equals = name.IndexOf('=');
                if (equals > 0)
                {
                    parsed.Options[name[..equals]] = name[(equals + 1)..];
                    continue;
                }

                if (IsKnownSwitch(name))
                {
                    parsed.Options[name] = null;
                    continue;
                }

                // --output / --input 有双重语义：
                //   devices --output              → 过滤开关（无取值）
                //   route X --output "Speakers"   → 取值选项
                // 判定规则：若存在下一个非选项参数，则视为取值选项；否则视为开关。
                bool hasFollowingValue = index + 1 < args.Length && !args[index + 1].StartsWith('-');

                if (RequiresValue(name) && !hasFollowingValue)
                {
                    throw new CliException(ExitCode.UsageError, $"Option --{name} requires a value.");
                }

                if (hasFollowingValue)
                {
                    parsed.Options[name] = args[index + 1];
                    index++;
                    continue;
                }

                parsed.Options[name] = null;
                continue;
            }

            parsed.Positional.Add(current);
        }

        return parsed;
    }

    private static string StripPrefix(string token)
    {
        string value = token;
        while (value.StartsWith('-'))
        {
            value = value[1..];
        }

        return value;
    }

    /// <summary>
    /// 无取值的开关选项。注意：<c>--output</c> / <c>--input</c> 既可能是过滤开关
    /// （<c>devices --output</c>），也可能是取值选项（<c>route X --output "Name"</c>），
    /// 因此它们**不在**此列表里，由解析器按"后面是否紧跟非选项取值"决定。
    /// </summary>
    private static bool IsKnownSwitch(string name) =>
        name.Equals("json", StringComparison.OrdinalIgnoreCase)
        || name.Equals("help", StringComparison.OrdinalIgnoreCase)
        || name.Equals("version", StringComparison.OrdinalIgnoreCase)
        || name.Equals("all", StringComparison.OrdinalIgnoreCase)
        || name.Equals("yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 一定需要取值的选项。这些选项出现时，下一个参数必须存在且不是选项。
    /// </summary>
    private static bool RequiresValue(string name) =>
        name.Equals("role", StringComparison.OrdinalIgnoreCase);
}

namespace WinAudioRoute.Tests;

/// <summary>
/// 测试运行环境开关与"环境绕过"登记。
/// <para>
/// <b>设计取舍（重要且必须如实报告）</b>：本仓库使用 xUnit 2.5.3，该版本<b>没有</b>
/// <c>Assert.Skip</c> / <c>SkipUnless</c>（2.9.0 才引入），且当前环境无法从 nuget.org
/// 稳定拉取新版本。因此"跳过"以显式登记的方式实现：测试在缺少设备/会话时提前返回。
/// <b>结果上它表现为"通过"而非"已跳过"</b>——这是已知限制。
/// </para>
/// <para>
/// 为了让脚本与报告能区分"上报通过"与"真正执行了断言"，每次绕过都会：
/// <list type="number">
///   <item><description>写入控制台（<c>[ENVIRONMENT-BYPASS]</c>）</description></item>
///   <item><description>当设置 <see cref="BypassLogVariable"/> 时追加写入该文件（每行一条）</description></item>
/// </list>
/// 落盘是为了让 <c>scripts/test-real-audio.ps1</c> 能精确统计绕过数量，
/// 而不必依赖 xUnit 的输出捕获（2.5.3 的自定义 <c>IMessageSink</c> 需要额外的
/// <c>LongLivedMarshalByRefObject</c> 继承，维护成本高于收益）。
/// </para>
/// </summary>
internal static class TestEnvironment
{
    /// <summary>允许修改系统音频状态的环境变量。</summary>
    internal const string MutationSwitch = "RUN_AUDIO_MUTATION_TESTS";

    /// <summary>把环境绕过写入文件的路径（由测试脚本设置）。</summary>
    internal const string BypassLogVariable = "WINAUDIOROUTE_TEST_BYPASS_LOG";

    private static readonly object Gate = new();
    private static readonly List<string> SkipLog = [];

    private static readonly string? BypassLogPath =
        Environment.GetEnvironmentVariable(BypassLogVariable) is { Length: > 0 } path ? path : null;

    /// <summary>本次运行中被跳过的集成测试说明。</summary>
    internal static IReadOnlyList<string> Skips
    {
        get
        {
            lock (Gate)
            {
                return [.. SkipLog];
            }
        }
    }

    /// <summary>是否允许执行会修改系统音频状态的测试。</summary>
    internal static bool MutationTestsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(MutationSwitch),
            "1",
            StringComparison.Ordinal);

    /// <summary>登记一次跳过（缺少设备/会话等环境原因），并写入控制台与（可选）日志文件。</summary>
    /// <param name="reason">跳过原因。</param>
    internal static void Skip(string reason)
    {
        lock (Gate)
        {
            SkipLog.Add(reason);

            // 原因必须出现在测试输出中，否则"提前 return"会被误读为"已完整执行"
            Console.WriteLine($"[ENVIRONMENT-BYPASS] {reason}");

            if (BypassLogPath is not null)
            {
                try
                {
                    File.AppendAllText(BypassLogPath, reason + Environment.NewLine);
                }
                catch
                {
                    // 日志失败不得影响测试
                }
            }
        }
    }

    /// <summary>把本次运行的绕过数量写入日志文件（供脚本读取；xUnit 中无进程内汇总钩子）。</summary>
    internal static void FlushBypassCount()
    {
        if (BypassLogPath is null)
        {
            return;
        }

        try
        {
            int count;
            lock (Gate)
            {
                count = SkipLog.Count;
            }

            File.AppendAllText(BypassLogPath, $"[COUNT] {count}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不得影响测试
        }
    }

    /// <summary>当前进程是否为 64 位（库的硬性要求）。</summary>
    internal static bool Is64BitProcess => Environment.Is64BitProcess;
}

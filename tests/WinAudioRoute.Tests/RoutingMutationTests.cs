namespace WinAudioRoute.Tests;

/// <summary>
/// 按应用路由的<b>修改性</b>测试。
/// <para>
/// <b>默认禁止执行</b>：只有显式设置 <c>RUN_AUDIO_MUTATION_TESTS=1</c> 才运行。
/// </para>
/// <para>
/// <b>安全规则（严格遵守）</b>：
/// <list type="bullet">
///   <item><description>绝不修改系统关键进程（explorer / audiodg / csrss / winlogon / dwm / lsass / services / System 等）。</description></item>
///   <item><description>拒绝修改测试宿主自身（避免 COM 枚举与调试宿主相互影响）。</description></item>
///   <item><description>优先使用人工指定的 <c>WINAUDIOROUTE_TEST_PID</c>；否则选择一个有音频会话的普通用户进程。</description></item>
///   <item><description>找不到安全目标就<b>跳过</b>，绝不为了"全绿"改系统。</description></item>
///   <item><description>每个写入都用 <c>try/finally</c> 恢复原路由，即使断言失败也恢复。</description></item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "Mutation")]
public class RoutingMutationTests
{
    /// <summary>允许人工指定测试目标进程。</summary>
    private const string TargetPidVariable = "WINAUDIOROUTE_TEST_PID";

    /// <summary>绝不修改的系统关键进程。</summary>
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "audiodg", "audiosrv", "dwm", "explorer", "sihost", "taskhostw", "fontdrvhost",
        "ctfmon", "spoolsv", "searchindexer", "searchhost", "shellexperiencehost",
        "startmenuexperiencehost", "runtimebroker", "securityhealthservice", "msmpeng",
        "wlanext", "wudfhost", "conhost", "dllhost",
    };

    private static bool RequireGate()
    {
        if (TestEnvironment.MutationTestsEnabled)
        {
            return true;
        }

        TestEnvironment.Skip(
            $"RoutingMutation: 未设置 {TestEnvironment.MutationSwitch}=1，跳过会修改系统路由的测试");
        return false;
    }

    /// <summary>
    /// 选择一个安全的测试目标 PID。
    /// </summary>
    /// <param name="audio">管理器（用于枚举会话）。</param>
    /// <param name="reason">选择失败的原因。</param>
    /// <returns>目标 PID；找不到时返回 0。</returns>
    private static int TryChooseTargetProcessId(WindowsAudioManager audio, out string reason)
    {
        // 1) 人工指定优先
        string? configured = Environment.GetEnvironmentVariable(TargetPidVariable);
        if (!string.IsNullOrWhiteSpace(configured) && int.TryParse(configured, out int explicitPid) && explicitPid > 0)
        {
            reason = $"explicit {TargetPidVariable}={explicitPid}";
            return explicitPid;
        }

        // 2) 自动挑选：有音频会话、非系统关键、非测试宿主自身
        int ownPid = Environment.ProcessId;
        IReadOnlyList<AudioSession> sessions = audio.GetSessions(bypassCache: true);

        foreach (AudioSession session in sessions)
        {
            int pid = session.ProcessId;
            if (pid <= 0 || pid == ownPid)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(session.ProcessName))
            {
                continue;
            }

            if (BlockedProcesses.Contains(session.ProcessName))
            {
                continue;
            }

            reason = $"auto-selected '{session.ProcessName}' (PID {pid})";
            return pid;
        }

        reason = "no safe process with audio sessions was found";
        return 0;
    }

    [Fact]
    public void WriteRestore_OutputRoute_KeepsStateIntact()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        if (!audio.IsPerAppRoutingSupported)
        {
            TestEnvironment.Skip($"RoutingMutation/Output: 本机不支持按应用路由（{audio.RoutingCapability}）");
            return;
        }

        int pid = TryChooseTargetProcessId(audio, out string reason);
        if (pid == 0)
        {
            TestEnvironment.Skip($"RoutingMutation/Output: {reason}");
            return;
        }

        AudioDevice? target = audio.GetPlaybackDevices().FirstOrDefault();
        if (target is null)
        {
            TestEnvironment.Skip("RoutingMutation/Output: 本机没有活动播放设备");
            return;
        }

        // 记录原状态
        AudioDevice? before = audio.GetApplicationOutput(pid);

        try
        {
            // 修改
            AudioOperationResult result = audio.SetApplicationOutput(pid, target);
            Assert.True(result.IsSuccess, $"写入失败：{result}（目标 PID {pid}）");

            // 验证回读
            AudioDevice? after = audio.GetApplicationOutput(pid);
            Assert.NotNull(after);
            Assert.Equal(target.Id, after!.Id);
        }
        finally
        {
            // 恢复：有原值则写回原值，否则清除 → 跟随系统默认
            AudioOperationResult restore = before is null
                ? audio.ResetApplicationOutput(pid)
                : audio.SetApplicationOutput(pid, before);

            Assert.True(restore.IsSuccess, $"恢复失败：{restore}（目标 PID {pid}，原因：{reason}）");

            AudioDevice? restored = audio.GetApplicationOutput(pid);
            Assert.Equal(before?.Id, restored?.Id);
        }
    }

    [Fact]
    public void WriteRestore_InputRoute_KeepsStateIntact()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        if (!audio.IsPerAppRoutingSupported)
        {
            TestEnvironment.Skip($"RoutingMutation/Input: 本机不支持按应用路由（{audio.RoutingCapability}）");
            return;
        }

        int pid = TryChooseTargetProcessId(audio, out string reason);
        if (pid == 0)
        {
            TestEnvironment.Skip($"RoutingMutation/Input: {reason}");
            return;
        }

        AudioDevice? target = audio.GetRecordingDevices().FirstOrDefault();
        if (target is null)
        {
            TestEnvironment.Skip("RoutingMutation/Input: 本机没有活动录音设备");
            return;
        }

        AudioDevice? before = audio.GetApplicationInput(pid);

        try
        {
            AudioOperationResult result = audio.SetApplicationInput(pid, target);
            Assert.True(result.IsSuccess, $"写入失败：{result}（目标 PID {pid}）");

            AudioDevice? after = audio.GetApplicationInput(pid);
            Assert.NotNull(after);
            Assert.Equal(target.Id, after!.Id);
        }
        finally
        {
            AudioOperationResult restore = before is null
                ? audio.ResetApplicationInput(pid)
                : audio.SetApplicationInput(pid, before);

            Assert.True(restore.IsSuccess, $"恢复失败：{restore}（目标 PID {pid}，原因：{reason}）");

            AudioDevice? restored = audio.GetApplicationInput(pid);
            Assert.Equal(before?.Id, restored?.Id);
        }
    }

    [Fact]
    public void FollowSystemDefault_NullMeansReset()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        if (!audio.IsPerAppRoutingSupported)
        {
            TestEnvironment.Skip($"RoutingMutation/FollowDefault: 本机不支持按应用路由（{audio.RoutingCapability}）");
            return;
        }

        int pid = TryChooseTargetProcessId(audio, out string reason);
        if (pid == 0)
        {
            TestEnvironment.Skip($"RoutingMutation/FollowDefault: {reason}");
            return;
        }

        AudioDevice? target = audio.GetPlaybackDevices().FirstOrDefault();
        AudioDevice? before = audio.GetApplicationOutput(pid);

        try
        {
            // 先真正写入一个路由，确保"清除"有意义
            if (target is not null)
            {
                AudioOperationResult set = audio.SetApplicationOutput(pid, target);
                Assert.True(set.IsSuccess, $"预置路由失败：{set}");
                Assert.NotNull(audio.GetApplicationOutput(pid));
            }

            // null 语义 = 删除持久化端点 = 跟随系统默认
            AudioOperationResult reset = audio.SetApplicationOutput(pid, device: null);
            Assert.True(reset.IsSuccess, $"null 语义清除失败：{reset}");

            Assert.Null(audio.GetApplicationOutput(pid));
        }
        finally
        {
            if (before is not null)
            {
                audio.SetApplicationOutput(pid, before);
                Assert.Equal(before.Id, audio.GetApplicationOutput(pid)?.Id);
            }
        }
    }

    [Fact]
    public void ResetApplicationRouting_ClearsBothDirections()
    {
        if (!RequireGate())
        {
            return;
        }

        using var audio = new WindowsAudioManager();

        if (!audio.IsPerAppRoutingSupported)
        {
            TestEnvironment.Skip($"RoutingMutation/ResetBoth: 本机不支持按应用路由（{audio.RoutingCapability}）");
            return;
        }

        int pid = TryChooseTargetProcessId(audio, out string reason);
        if (pid == 0)
        {
            TestEnvironment.Skip($"RoutingMutation/ResetBoth: {reason}");
            return;
        }

        AudioDevice? outputBefore = audio.GetApplicationOutput(pid);
        AudioDevice? inputBefore = audio.GetApplicationInput(pid);

        try
        {
            AudioOperationResult reset = audio.ResetApplicationRouting(pid);

            // 两个方向都必须被显式处理
            Assert.Equal(2, reset.Total);

            if (reset.IsSuccess)
            {
                Assert.Null(audio.GetApplicationOutput(pid));
                Assert.Null(audio.GetApplicationInput(pid));
            }
        }
        finally
        {
            Restore(audio, pid, outputBefore, inputBefore, reason);
        }
    }

    [Fact]
    public void WriteToSystemCriticalProcess_IsRefused_ByTestHarness()
    {
        if (!RequireGate())
        {
            return;
        }

        // 这条测试验证"测试夹具本身"的安全策略：绝不把系统关键进程当作可写目标。
        using var audio = new WindowsAudioManager();

        if (!audio.IsPerAppRoutingSupported)
        {
            TestEnvironment.Skip("RoutingMutation/SafetyPolicy: 本机不支持按应用路由");
            return;
        }

        int pid = TryChooseTargetProcessId(audio, out _);
        if (pid == 0)
        {
            TestEnvironment.Skip("RoutingMutation/SafetyPolicy: 没有候选目标");
            return;
        }

        using var process = System.Diagnostics.Process.GetProcessById(pid);
        Assert.DoesNotContain(process.ProcessName, BlockedProcesses);
        Assert.NotEqual(Environment.ProcessId, pid);
    }

    private static void Restore(
        WindowsAudioManager audio,
        int pid,
        AudioDevice? outputBefore,
        AudioDevice? inputBefore,
        string reason)
    {
        var failures = new List<string>();

        try
        {
            AudioOperationResult r = outputBefore is null
                ? audio.ResetApplicationOutput(pid)
                : audio.SetApplicationOutput(pid, outputBefore);

            if (!r.IsSuccess)
            {
                failures.Add($"output: {r}");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"output: {ex.GetType().Name} {ex.Message}");
        }

        try
        {
            AudioOperationResult r = inputBefore is null
                ? audio.ResetApplicationInput(pid)
                : audio.SetApplicationInput(pid, inputBefore);

            if (!r.IsSuccess)
            {
                failures.Add($"input: {r}");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"input: {ex.GetType().Name} {ex.Message}");
        }

        // 恢复失败必须显式暴露，绝不静默留下被改动的用户路由
        List<string> details = [.. failures.Select(f => $"{f} (target PID {pid}, {reason})")];
        Assert.Empty(details);
    }
}

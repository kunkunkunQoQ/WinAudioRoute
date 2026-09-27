using System.Text.Json.Nodes;

namespace WinAudioRoute.Cli;

/// <summary>
/// 命令实现。
/// <para>
/// 约定：
/// <list type="bullet">
///   <item><description>所有数据输出写 <b>stdout</b>；所有诊断/错误写 <b>stderr</b>。</description></item>
///   <item><description>失败通过 <see cref="CliException"/> 表达，由 <see cref="Program"/> 统一映射退出码。</description></item>
///   <item><description>任何模糊匹配都不允许"静默选第一个"——歧义必须列出候选并失败。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class Commands
{
    /// <summary>执行命令。</summary>
    /// <param name="args">已解析的参数。</param>
    /// <returns>退出码。</returns>
    public static ExitCode Run(CliArguments args) => args.Command switch
    {
        "devices" => Devices(args),
        "sessions" => Sessions(args),
        "default" => Default(args),
        "default-output" => SetDefaultDevice(args, AudioDataFlow.Render),
        "default-input" => SetDefaultDevice(args, AudioDataFlow.Capture),
        "volume" => Volume(args),
        "mute" => SetMute(args, muted: true),
        "unmute" => SetMute(args, muted: false),
        "route" => Route(args),
        "reset" => Reset(args),
        "capabilities" => Capabilities(args),
        "first-audible-pid" => FirstAudiblePid(args),
        _ => throw new CliException(
            ExitCode.UsageError,
            $"Unknown command '{args.Command}'. Run 'winaudio --help' for usage."),
    };

    /// <summary>
    /// 已知的音频/系统关键进程：真实硬件测试绝不修改它们的状态。
    /// <para>
    /// 只列 Windows 自身的系统组件与音频服务。第三方虚拟声卡/音频路由软件（例如各种
    /// "Sonar" 类产品）不在此处硬编码——那会让列表绑定到某台机器上装了什么。
    /// 调用方自身的进程由 <see cref="FirstAudiblePid"/> 另行排除。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "audiodg", "audiosrv", "dwm", "explorer", "sihost", "taskhostw", "fontdrvhost",
        "ctfmon", "spoolsv", "searchindexer", "searchhost", "shellexperiencehost",
        "startmenuexperiencehost", "runtimebroker", "securityhealthservice", "msmpeng",
        "wlanext", "wudfhost", "conhost", "dllhost",
    };

    /// <summary>
    /// 输出一个"可安全修改音频状态"的进程 PID（用于真实硬件测试脚本）。
    /// <para>
    /// 硬性排除：当前进程自身、系统/音频关键进程（见 <see cref="ProtectedProcesses"/>）。
    /// 找不到合适目标时返回非零退出码，脚本据此跳过而不是乱改系统。
    /// </para>
    /// </summary>
    private static ExitCode FirstAudiblePid(CliArguments args)
    {
        using var audio = new WindowsAudioManager();

        int ownPid = Environment.ProcessId;

        foreach (AudioSession session in audio.GetSessions(bypassCache: true))
        {
            int pid = session.ProcessId;
            if (pid <= 0 || pid == ownPid)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(session.ProcessName)
                || ProtectedProcesses.Contains(session.ProcessName))
            {
                continue;
            }

            if (args.Json)
            {
                var envelope = Json.Envelope("first-audible-pid");
                envelope["data"] = new JsonObject
                {
                    ["processId"] = pid,
                    ["processName"] = Json.StringOrNull(session.ProcessName),
                };
                Console.Out.WriteLine(Json.Serialize(envelope));
            }
            else
            {
                Console.Out.WriteLine(pid);
            }

            return ExitCode.Success;
        }

        Console.Error.WriteLine(
            "winaudio: no safe audio-active process found (all candidates are protected or the test host itself).");
        return ExitCode.SessionNotFound;
    }

    // ------------------------------------------------------------------
    // devices
    // ------------------------------------------------------------------

    private static ExitCode Devices(CliArguments args)
    {
        using var audio = new WindowsAudioManager();

        bool outputOnly = args.Has("output");
        bool inputOnly = args.Has("input");
        if (outputOnly && inputOnly)
        {
            throw new CliException(ExitCode.UsageError, "--output and --input are mutually exclusive.");
        }

        AudioDeviceState states = args.Has("all") ? AudioDeviceState.All : AudioDeviceState.Active;

        var devices = new List<AudioDevice>();
        if (!inputOnly)
        {
            devices.AddRange(audio.GetPlaybackDevices(states));
        }

        if (!outputOnly)
        {
            devices.AddRange(audio.GetRecordingDevices(states));
        }

        if (args.Json)
        {
            var envelope = Json.Envelope("devices");
            envelope["data"] = new JsonObject
            {
                ["states"] = states.ToString(),
                ["devices"] = Json.ArrayOf(devices.Select(JsonProjection.Device)),
            };

            Console.Out.WriteLine(Json.Serialize(envelope));
            return ExitCode.Success;
        }

        if (devices.Count == 0)
        {
            Console.Error.WriteLine($"winaudio: no devices matched (states={states}).");
            return ExitCode.Success;
        }

        TextOutput.Columns("FLOW", "STATE", "DEFAULT", "NAME", "ID");
        foreach (AudioDevice device in devices)
        {
            TextOutput.Columns(
                JsonProjection.Flow(device.Flow),
                device.State.ToString(),
                DefaultRoles(device),
                TextOutput.Or(device.FriendlyName),
                device.Id);
        }

        return ExitCode.Success;
    }

    private static string DefaultRoles(AudioDevice device)
    {
        var roles = new List<string>();
        if (device.IsDefaultConsole)
        {
            roles.Add("console");
        }

        if (device.IsDefaultMultimedia)
        {
            roles.Add("multimedia");
        }

        if (device.IsDefaultCommunications)
        {
            roles.Add("communications");
        }

        return roles.Count == 0 ? "-" : string.Join("|", roles);
    }

    // ------------------------------------------------------------------
    // sessions
    // ------------------------------------------------------------------

    private static ExitCode Sessions(CliArguments args)
    {
        using var audio = new WindowsAudioManager();

        AudioDataFlow? flow = null;
        if (args.Has("output"))
        {
            flow = AudioDataFlow.Render;
        }
        else if (args.Has("input"))
        {
            flow = AudioDataFlow.Capture;
        }

        IReadOnlyList<AudioSession> sessions =
            flow is null ? audio.GetSessions() : audio.GetSessions(flow.Value);

        if (args.Json)
        {
            var envelope = Json.Envelope("sessions");
            envelope["data"] = new JsonObject
            {
                ["sessions"] = Json.ArrayOf(sessions.Select(JsonProjection.Session)),
            };

            Console.Out.WriteLine(Json.Serialize(envelope));
            return ExitCode.Success;
        }

        if (sessions.Count == 0)
        {
            Console.Error.WriteLine("winaudio: no audio sessions found.");
            return ExitCode.Success;
        }

        TextOutput.Columns("PID", "PROCESS", "STATE", "FLOW", "VOLUME", "MUTED", "DISPLAY");
        foreach (AudioSession session in sessions)
        {
            TextOutput.Columns(
                session.ProcessId.ToString(),
                TextOutput.Or(session.ProcessName),
                session.State.ToString(),
                JsonProjection.Flow(session.Flow),
                TextOutput.Volume(session.Volume),
                session.IsMuted is null ? "-" : (session.IsMuted.Value ? "yes" : "no"),
                TextOutput.Or(session.DisplayName));
        }

        return ExitCode.Success;
    }

    // ------------------------------------------------------------------
    // default（读取）
    // ------------------------------------------------------------------

    private static ExitCode Default(CliArguments args)
    {
        using var audio = new WindowsAudioManager();

        AudioDataFlow[] flows = args.Has("output")
            ? [AudioDataFlow.Render]
            : args.Has("input")
                ? [AudioDataFlow.Capture]
                : [AudioDataFlow.Render, AudioDataFlow.Capture];

        var entries = new List<(AudioDataFlow Flow, AudioRole Role, AudioDevice? Device)>();
        foreach (AudioDataFlow flow in flows)
        {
            foreach (AudioRole role in (AudioRole[])[AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications])
            {
                entries.Add((flow, role, audio.GetDefaultDevice(flow, role)));
            }
        }

        if (args.Json)
        {
            var items = new JsonArray();
            foreach ((AudioDataFlow flow, AudioRole role, AudioDevice? device) in entries)
            {
                items.Add(new JsonObject
                {
                    ["flow"] = JsonProjection.Flow(flow),
                    ["role"] = JsonProjection.Role(role),
                    ["deviceId"] = device is null ? null : JsonValue.Create(device.Id),
                    ["name"] = device is null ? null : JsonValue.Create(device.FriendlyName),
                });
            }

            var envelope = Json.Envelope("default");
            envelope["data"] = new JsonObject { ["defaults"] = items };
            Console.Out.WriteLine(Json.Serialize(envelope));
            return ExitCode.Success;
        }

        TextOutput.Columns("FLOW", "ROLE", "NAME", "ID");
        foreach ((AudioDataFlow flow, AudioRole role, AudioDevice? device) in entries)
        {
            TextOutput.Columns(
                JsonProjection.Flow(flow),
                JsonProjection.Role(role),
                device is null ? "(none)" : TextOutput.Or(device.FriendlyName),
                device?.Id ?? "(none)");
        }

        return ExitCode.Success;
    }

    // ------------------------------------------------------------------
    // default-output / default-input（写入）
    // ------------------------------------------------------------------

    private static ExitCode SetDefaultDevice(CliArguments args, AudioDataFlow flow)
    {
        using var audio = new WindowsAudioManager();

        string query = args.RequiredPositional(0, "device");
        AudioRole role = ParseRole(args.Value("role"));

        AudioDevice device = ResolveDevice(audio, flow, query);

        if (!args.Has("yes"))
        {
            Console.Error.WriteLine(
                "winaudio: this command changes the system default device and needs --yes to confirm.");
            return ExitCode.UsageError;
        }

        AudioOperationResult result = audio.SetDefaultDevice(device, role);

        return Report(args, "default-device", result, onSuccess: () =>
        {
            AudioDevice? after = audio.GetDefaultDevice(flow, role);
            var envelope = Json.Envelope("default-device");
            envelope["data"] = new JsonObject
            {
                ["flow"] = JsonProjection.Flow(flow),
                ["role"] = JsonProjection.Role(role),
                ["deviceId"] = device.Id,
                ["verified"] = after?.Id == device.Id,
                ["result"] = JsonProjection.Result(result),
            };
            return envelope;
        }, successMessage: $"Default {JsonProjection.Flow(flow)}/{role} set to '{device.FriendlyName}'.");
    }

    private static AudioRole ParseRole(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "console" => AudioRole.Console,
        "multimedia" => AudioRole.Multimedia,
        "communications" => AudioRole.Communications,
        _ => throw new CliException(ExitCode.UsageError, $"Unknown role '{value}'. Valid: console, multimedia, communications."),
    };

    // ------------------------------------------------------------------
    // volume
    // ------------------------------------------------------------------

    private static ExitCode Volume(CliArguments args)
    {
        using var audio = new WindowsAudioManager();

        string processQuery = args.RequiredPositional(0, "process");
        string volumeText = args.RequiredPositional(1, "volume");

        if (!int.TryParse(volumeText, out int percent))
        {
            throw new CliException(ExitCode.UsageError, $"Volume must be an integer 0-100, got '{volumeText}'.");
        }

        int pid = ResolveProcessId(audio, processQuery);
        AudioOperationResult result = audio.SetApplicationVolumePercent(pid, percent);

        return Report(args, "volume", result, onSuccess: () =>
        {
            int actualPercent = audio.GetApplicationVolumePercent(pid);
            var envelope = Json.Envelope("volume");
            envelope["data"] = new JsonObject
            {
                ["processId"] = pid,
                ["processName"] = Json.StringOrNull(GetProcessName(pid)),
                ["requestedPercent"] = percent,
                ["actualPercent"] = actualPercent,
                ["result"] = JsonProjection.Result(result),
            };
            return envelope;
        }, successMessage: $"Volume for PID {pid} set to {Math.Clamp(percent, 0, 100)}%.");
    }

    // ------------------------------------------------------------------
    // mute / unmute
    // ------------------------------------------------------------------

    private static ExitCode SetMute(CliArguments args, bool muted)
    {
        using var audio = new WindowsAudioManager();

        string processQuery = args.RequiredPositional(0, "process");
        int pid = ResolveProcessId(audio, processQuery);

        AudioOperationResult result = audio.SetApplicationMute(pid, muted);

        return Report(args, muted ? "mute" : "unmute", result, onSuccess: () =>
        {
            bool actual = audio.GetApplicationMute(pid);
            var envelope = Json.Envelope(muted ? "mute" : "unmute");
            envelope["data"] = new JsonObject
            {
                ["processId"] = pid,
                ["processName"] = Json.StringOrNull(GetProcessName(pid)),
                ["requestedMuted"] = muted,
                ["actualMuted"] = actual,
                ["result"] = JsonProjection.Result(result),
            };
            return envelope;
        }, successMessage: $"PID {pid} {(muted ? "muted" : "unmuted")}.");
    }

    // ------------------------------------------------------------------
    // route
    // ------------------------------------------------------------------

    private static ExitCode Route(CliArguments args)
    {
        using var audio = new WindowsAudioManager();

        string processQuery = args.RequiredPositional(0, "process");
        int pid = ResolveProcessId(audio, processQuery);

        bool output = args.Has("output");
        bool input = args.Has("input");
        if (output == input)
        {
            throw new CliException(
                ExitCode.UsageError,
                "Specify exactly one of --output <device> or --input <device>.");
        }

        AudioDataFlow flow = output ? AudioDataFlow.Render : AudioDataFlow.Capture;
        string deviceQuery = args.RequiredValue(output ? "output" : "input");

        AudioDevice device = ResolveDevice(audio, flow, deviceQuery);

        AudioOperationResult result = output
            ? audio.SetApplicationOutput(pid, device)
            : audio.SetApplicationInput(pid, device);

        return Report(
            args,
            "route",
            result,
            onSuccess: () =>
            {
                AudioDevice? actual = output
                    ? audio.GetApplicationOutput(pid)
                    : audio.GetApplicationInput(pid);

                var envelope = Json.Envelope("route", usesUndocumentedApi: true);
                envelope["data"] = new JsonObject
                {
                    ["processId"] = pid,
                    ["processName"] = Json.StringOrNull(GetProcessName(pid)),
                    ["flow"] = JsonProjection.Flow(flow),
                    ["deviceId"] = device.Id,
                    ["deviceName"] = device.FriendlyName,
                    ["verified"] = actual?.Id == device.Id,
                    ["result"] = JsonProjection.Result(result),
                };
                return envelope;
            },
            onFailure: BuildFailureEnvelope("route", pid, flow, result),
            successMessage: $"PID {pid} {JsonProjection.Flow(flow)} routed to '{device.FriendlyName}'.");
    }

    /// <summary>构造"写入失败"时的 JSON 信封（仍输出结构化结果，便于脚本判断）。</summary>
    /// <param name="command">命令名。</param>
    /// <param name="processId">进程 ID。</param>
    /// <param name="flow">方向。</param>
    /// <param name="result">失败结果。</param>
    /// <returns>信封工厂。</returns>
    private static Func<System.Text.Json.Nodes.JsonObject> BuildFailureEnvelope(
        string command,
        int processId,
        AudioDataFlow flow,
        AudioOperationResult result) => () =>
    {
        var envelope = Json.Envelope(command, usesUndocumentedApi: true);
        envelope["data"] = new JsonObject
        {
            ["processId"] = processId,
            ["flow"] = JsonProjection.Flow(flow),
            ["result"] = JsonProjection.Result(result),
        };
        return envelope;
    };

    // ------------------------------------------------------------------
    // reset
    // ------------------------------------------------------------------

    private static ExitCode Reset(CliArguments args)
    {
        using var audio = new WindowsAudioManager();

        string processQuery = args.RequiredPositional(0, "process");
        int pid = ResolveProcessId(audio, processQuery);

        AudioOperationResult result =
            (args.Has("output") && !args.Has("input")) ? audio.ResetApplicationOutput(pid)
            : (args.Has("input") && !args.Has("output")) ? audio.ResetApplicationInput(pid)
            : audio.ResetApplicationRouting(pid);

        return Report(args, "reset", result, onSuccess: () =>
        {
            var envelope = Json.Envelope("reset", usesUndocumentedApi: true);
            envelope["data"] = new JsonObject
            {
                ["processId"] = pid,
                ["processName"] = Json.StringOrNull(GetProcessName(pid)),
                ["output"] = Json.StringOrNull(audio.GetApplicationOutput(pid)?.Id),
                ["input"] = Json.StringOrNull(audio.GetApplicationInput(pid)?.Id),
                ["result"] = JsonProjection.Result(result),
            };
            return envelope;
        }, successMessage: $"PID {pid} routing reset to follow the system default device.");
    }

    // ------------------------------------------------------------------
    // capabilities
    // ------------------------------------------------------------------

    private static ExitCode Capabilities(CliArguments args)
    {
        using var audio = new WindowsAudioManager();

        AudioRoutingCapability routing = audio.RoutingCapability;

        if (args.Json)
        {
            var envelope = Json.Envelope("capabilities", usesUndocumentedApi: routing.IsSupported);
            envelope["data"] = new JsonObject
            {
                ["windowsBuild"] = WindowsAudioEnvironment.OsBuild,
                ["isWindows11OrLater"] = WindowsAudioEnvironment.IsWindows11OrLater,
                ["architecture"] = WindowsAudioEnvironment.ProcessArchitecture.ToString(),
                ["perAppRoutingSupported"] = routing.IsSupported,
                ["perAppRoutingReason"] = Json.StringOrNull(routing.Reason),
                ["defaultDeviceWriteEnabled"] = audio.IsDefaultDeviceWriteEnabled,
                ["defaultDeviceWriteBlocker"] = Json.StringOrNull(audio.DefaultDeviceWriteBlocker),
                ["deviceNotificationRegistered"] = audio.IsDeviceNotificationRegistered,
            };

            Console.Out.WriteLine(Json.Serialize(envelope));
            return ExitCode.Success;
        }

        TextOutput.Line($"windows build            : {WindowsAudioEnvironment.OsBuild}");
        TextOutput.Line($"architecture             : {WindowsAudioEnvironment.ProcessArchitecture}");
        TextOutput.Line($"per-app routing          : {(routing.IsSupported ? "supported" : "not supported")}");
        if (!routing.IsSupported)
        {
            TextOutput.Line($"  reason                 : {routing.Reason}");
            TextOutput.Line($"  hresult                : {routing.HResultHex}");
        }

        TextOutput.Line($"default device write     : {(audio.IsDefaultDeviceWriteEnabled ? "enabled" : "disabled")}");
        TextOutput.Line($"device notification      : {(audio.IsDeviceNotificationRegistered ? "registered" : "not registered")}");
        return ExitCode.Success;
    }

    // ------------------------------------------------------------------
    // 共享辅助
    // ------------------------------------------------------------------

    private static ExitCode Report(
        CliArguments args,
        string command,
        AudioOperationResult result,
        Func<System.Text.Json.Nodes.JsonObject> onSuccess,
        string successMessage,
        Func<System.Text.Json.Nodes.JsonObject>? onFailure = null)
    {
        if (args.Json)
        {
            var envelope = result.IsSuccess ? onSuccess() : (onFailure ?? onSuccess)();
            Console.Out.WriteLine(Json.Serialize(envelope));
        }
        else if (result.IsSuccess)
        {
            TextOutput.Line(successMessage);
        }

        if (result.IsSuccess)
        {
            return ExitCode.Success;
        }

        foreach (AudioOperationFailure failure in result.Failures)
        {
            Console.Error.WriteLine(
                $"winaudio: {command} failed for '{failure.Target}': {failure.Message} (HRESULT 0x{failure.HResult:X8})");
        }

        Console.Error.WriteLine($"winaudio: {command} result: {result}");

        return ExitCode.GeneralError;
    }

    /// <summary>解析进程标识：纯数字视为 PID，否则按进程名解析（歧义时失败）。</summary>
    private static int ResolveProcessId(WindowsAudioManager audio, string query)
    {
        if (int.TryParse(query, out int pid))
        {
            if (pid <= 0)
            {
                throw new CliException(ExitCode.UsageError, $"Invalid process id '{query}'.");
            }

            return pid;
        }

        try
        {
            IReadOnlyList<AudioSession> sessions = audio.GetSessionsForProcess(query);
            return sessions[0].ProcessId;
        }
        catch (AmbiguousAudioSessionException ex)
        {
            throw new CliException(
                ExitCode.SessionNotFound,
                $"Process name '{query}' matches {ex.CandidateProcessIds.Count} processes; use a PID instead.",
                ex)
            {
                Candidates = [.. ex.CandidateProcessIds.Select(p => p.ToString())],
            };
        }
        catch (AudioSessionNotFoundException ex)
        {
            throw new CliException(ExitCode.SessionNotFound, ex.Message, ex);
        }
    }

    /// <summary>解析设备：ID 优先、其次名称（歧义时列出候选并失败）。</summary>
    private static AudioDevice ResolveDevice(WindowsAudioManager audio, AudioDataFlow flow, string query)
    {
        try
        {
            return audio.ResolveDevice(flow, query);
        }
        catch (AmbiguousAudioDeviceException ex)
        {
            throw new CliException(
                ExitCode.AmbiguousDevice,
                $"Device query '{query}' matches {ex.Candidates.Count} devices.",
                ex)
            {
                Candidates = [.. ex.Candidates.Select(c => $"{c.FriendlyName} [{c.Id}]")],
            };
        }
        catch (AudioDeviceNotFoundException ex)
        {
            throw new CliException(ExitCode.DeviceNotFound, ex.Message, ex);
        }
    }

    private static string? GetProcessName(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}

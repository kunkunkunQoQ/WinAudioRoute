using WinAudioRoute;

// PerAppRouting —— 把某个应用的输出/输入路由到指定设备，并在退出前恢复原状。
//
// ⚠️ 本示例使用【未公开的 Windows 内部 API】（Windows.Media.Internal.AudioPolicyConfig）。
//    它不是 Microsoft 公开或受支持的 API，未来 Windows 更新可能改变其行为或使其失效。
//    可用性请先用 audio.IsPerAppRoutingSupported / RoutingCapability 检查。
//
// 用法：
//   PerAppRouting                             显示能力与当前路由状态
//   PerAppRouting route <pid> <deviceName> [output|input]
//   PerAppRouting reset <pid> [output|input]

using var audio = new WindowsAudioManager();

AudioRoutingCapability capability = audio.RoutingCapability;
Console.WriteLine($"Per-app routing : {(capability.IsSupported ? "supported" : "NOT supported")}");
if (!capability.IsSupported)
{
    Console.WriteLine($"  reason  : {capability.Reason}");
    Console.WriteLine($"  hresult : {capability.HResultHex}");
    Console.WriteLine();
    Console.WriteLine("This sample requires per-app routing support; exiting.");
    return 6;
}

if (args.Length == 0)
{
    return ShowState(audio);
}

switch (args[0].ToLowerInvariant())
{
    case "route" when args.Length >= 3:
        return Route(audio, args[1], args[2], args.Length >= 4 ? args[3] : "output");

    case "reset" when args.Length >= 2:
        return Reset(audio, args[1], args.Length >= 3 ? args[2] : "both");

    default:
        Console.Error.WriteLine("usage: PerAppRouting route <pid> <deviceName> [output|input]");
        Console.Error.WriteLine("       PerAppRouting reset <pid> [output|input|both]");
        return 2;
}

static int ShowState(WindowsAudioManager audio)
{
    Console.WriteLine();
    Console.WriteLine("Current per-application routes (PID -> device):");

    // 路由是按 PID 生效的：对 PID 去重，避免每个会话重复打印一行
    var seen = new HashSet<int>();
    foreach (AudioSession session in audio.GetSessions(bypassCache: true))
    {
        if (!seen.Add(session.ProcessId))
        {
            continue;
        }

        try
        {
            AudioDevice? output = audio.GetApplicationOutput(session.ProcessId);
            AudioDevice? input = audio.GetApplicationInput(session.ProcessId);

            Console.WriteLine(
                $"  PID {session.ProcessId,-7} {session.ProcessName ?? "(unknown)",-24} " +
                $"out={output?.FriendlyName ?? "(follow system default)"}  " +
                $"in={input?.FriendlyName ?? "(follow system default)"}");
        }
        catch (WinAudioException ex)
        {
            Console.WriteLine($"  PID {session.ProcessId,-7} <{ex.GetType().Name}>");
        }
    }

    return 0;
}

static int Route(WindowsAudioManager audio, string pidText, string deviceQuery, string direction)
{
    if (!int.TryParse(pidText, out int pid) || pid <= 0)
    {
        Console.Error.WriteLine($"Invalid pid '{pidText}'.");
        return 2;
    }

    bool input = direction.Equals("input", StringComparison.OrdinalIgnoreCase);
    AudioDataFlow flow = input ? AudioDataFlow.Capture : AudioDataFlow.Render;

    // 记录原状态，退出前恢复
    AudioDevice? original = input ? audio.GetApplicationInput(pid) : audio.GetApplicationOutput(pid);

    try
    {
        AudioDevice device = audio.ResolveDevice(flow, deviceQuery);

        Console.WriteLine($"Routing PID {pid} {(input ? "input" : "output")} -> {device.FriendlyName}");

        AudioOperationResult result = input
            ? audio.SetApplicationInput(pid, device)
            : audio.SetApplicationOutput(pid, device);

        Console.WriteLine($"  result: {result}");
        if (!result.IsSuccess)
        {
            foreach (AudioOperationFailure failure in result.Failures)
            {
                Console.Error.WriteLine($"  {failure.Target}: {failure.Message} (0x{failure.HResult:X8})");
            }

            return 1;
        }

        AudioDevice? actual = input ? audio.GetApplicationInput(pid) : audio.GetApplicationOutput(pid);
        Console.WriteLine($"  verified: {(actual?.Id == device.Id ? "yes" : "NO")}");
        return actual?.Id == device.Id ? 0 : 1;
    }
    catch (AmbiguousAudioDeviceException ex)
    {
        Console.Error.WriteLine($"'{deviceQuery}' matches {ex.Candidates.Count} devices:");
        foreach (AudioDevice candidate in ex.Candidates)
        {
            Console.Error.WriteLine($"  - {candidate.FriendlyName} [{candidate.Id}]");
        }

        return 4;
    }
    catch (AudioDeviceNotFoundException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 3;
    }
    finally
    {
        Console.WriteLine("Restoring the original route ...");
        try
        {
            AudioOperationResult restore = input
                ? (original is null ? audio.ResetApplicationInput(pid) : audio.SetApplicationInput(pid, original))
                : (original is null ? audio.ResetApplicationOutput(pid) : audio.SetApplicationOutput(pid, original));

            Console.WriteLine($"  restore: {restore}");
        }
        catch (WinAudioException ex)
        {
            Console.Error.WriteLine($"  restore failed: {ex.Message}");
        }
    }
}

static int Reset(WindowsAudioManager audio, string pidText, string direction)
{
    if (!int.TryParse(pidText, out int pid) || pid <= 0)
    {
        Console.Error.WriteLine($"Invalid pid '{pidText}'.");
        return 2;
    }

    AudioOperationResult result = direction.ToLowerInvariant() switch
    {
        "output" => audio.ResetApplicationOutput(pid),
        "input" => audio.ResetApplicationInput(pid),
        _ => audio.ResetApplicationRouting(pid),
    };

    Console.WriteLine($"Reset PID {pid} ({direction}): {result}");
    return result.IsSuccess ? 0 : 1;
}

using System.Reflection;

namespace WinAudioRoute.Cli;

/// <summary>
/// CLI 入口点。
/// <para>
/// <b>输出约定</b>：数据一律写 stdout；诊断、警告与错误一律写 stderr。
/// 因此 <c>winaudio devices --json | ConvertFrom-Json</c> 之类的管道不会被日志污染。
/// </para>
/// <para><b>退出码</b>见 <see cref="ExitCode"/>（0 成功 / 2 用法 / 3 设备未找到 / 4 设备歧义 /
/// 5 会话未找到 / 6 功能不支持 / 7 访问被拒绝 / 1 其他）。</para>
/// </summary>
internal static class Program
{
    /// <summary>CLI 版本（与库版本保持一致）。</summary>
    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            CliArguments parsed = CliArguments.Parse(args);

            if (parsed.Has("version") || parsed.Command == "version")
            {
                Console.Out.WriteLine($"winaudio {Version}");
                return (int)ExitCode.Success;
            }

            if (args.Length == 0 || parsed.Has("help") || parsed.Command is "" or "help")
            {
                PrintHelp();
                return args.Length == 0 ? (int)ExitCode.UsageError : (int)ExitCode.Success;
            }

            if (parsed.Command is "__parse")
            {
                // 内部诊断：回显解析结果，用于排查命令行转义问题
                Console.Out.WriteLine($"argv    = [{string.Join("] [", args)}]");
                Console.Out.WriteLine($"command = '{parsed.Command}'");
                Console.Out.WriteLine($"pos     = [{string.Join("] [", parsed.Positional)}]");
                foreach (KeyValuePair<string, string?> option in parsed.Options)
                {
                    Console.Out.WriteLine($"opt     = {option.Key} = {option.Value ?? "(switch)"}");
                }

                return (int)ExitCode.Success;
            }

            return (int)Commands.Run(parsed);
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"winaudio: {ex.Message}");

            if (ex.Candidates.Count > 0)
            {
                Console.Error.WriteLine("Candidates:");
                foreach (string candidate in ex.Candidates)
                {
                    Console.Error.WriteLine($"  - {candidate}");
                }
            }

            return (int)ex.ExitCode;
        }
        catch (AmbiguousAudioDeviceException ex)
        {
            Console.Error.WriteLine($"winaudio: {ex.Message}");
            Console.Error.WriteLine("Candidates:");
            foreach (AudioDevice candidate in ex.Candidates)
            {
                Console.Error.WriteLine($"  - {candidate.FriendlyName} [{candidate.Id}]");
            }

            return (int)ExitCode.AmbiguousDevice;
        }
        catch (AudioDeviceNotFoundException ex)
        {
            Console.Error.WriteLine($"winaudio: {ex.Message}");
            return (int)ExitCode.DeviceNotFound;
        }
        catch (AmbiguousAudioSessionException ex)
        {
            Console.Error.WriteLine($"winaudio: {ex.Message}");
            Console.Error.WriteLine("Candidates (PIDs):");
            foreach (int pid in ex.CandidateProcessIds)
            {
                Console.Error.WriteLine($"  - {pid}");
            }

            return (int)ExitCode.SessionNotFound;
        }
        catch (AudioSessionNotFoundException ex)
        {
            Console.Error.WriteLine($"winaudio: {ex.Message}");
            return (int)ExitCode.SessionNotFound;
        }
        catch (AudioRoutingNotSupportedException ex)
        {
            Console.Error.WriteLine($"winaudio: {ex.Message}");
            if (ex.HResult != 0)
            {
                Console.Error.WriteLine($"  HRESULT: {ex.HResultHex}");
            }

            return (int)ExitCode.NotSupported;
        }
        catch (AudioOperationFailedException ex)
        {
            Console.Error.WriteLine($"winaudio: {ex.Message} (HRESULT {ex.HResultHex})");
            if (ex.OperationResult is { } result)
            {
                foreach (AudioOperationFailure failure in result.Failures)
                {
                    Console.Error.WriteLine($"  {failure.Target}: {failure.Message} (0x{failure.HResult:X8})");
                }
            }

            return ex.HResult == unchecked((int)0x80070005u)
                ? (int)ExitCode.AccessDenied
                : (int)ExitCode.GeneralError;
        }
        catch (WinAudioException ex)
        {
            Console.Error.WriteLine($"winaudio: {ex.Message} (HRESULT {ex.HResultHex})");
            return (int)ExitCode.GeneralError;
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine($"winaudio: {ex.Message}");
            return (int)ExitCode.NotSupported;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"winaudio: unexpected error: {ex.GetType().Name}: {ex.Message}");
            return (int)ExitCode.GeneralError;
        }
    }

    private static void PrintHelp()
    {
        Console.Out.WriteLine($"""
winaudio {Version} - Windows audio devices, sessions and per-app routing

USAGE
  winaudio <command> [arguments] [options]

COMMANDS
  devices [--output|--input] [--all] [--json]
      List playback/recording devices. --all includes disabled/not-present/unplugged.
  sessions [--output|--input] [--json]
      List audio sessions (session granularity, not per-application).
  default [--output|--input] [--json]
      Show default devices for console/multimedia/communications.
  default-output <device> [--role <role>] [--yes] [--json]
      Set the system default playback device. Requires --yes.
  default-input <device> [--role <role>] [--yes] [--json]
      Set the system default recording device. Requires --yes.
  volume <process> <0-100> [--json]
      Set application volume. <process> is a PID or a process name.
  mute <process> [--json]
  unmute <process> [--json]
      Set application mute state.
  route <process> --output <device> [--json]
  route <process> --input <device> [--json]
      Route an application's output/input to a specific device (per-app routing).
  reset <process> [--output|--input] [--json]
      Make an application follow the system default device again.
  capabilities [--json]
      Report OS build, architecture and feature availability.
  first-audible-pid [--json]
      Print a PID that is safe to modify for audio testing (used by scripts/test-real-audio.ps1).

OPTIONS
  --json           Emit machine-readable JSON on stdout.
  --role <role>    console | multimedia | communications
  --yes            Confirm a state-changing operation.
  --help           Show this help.
  --version        Show the version.

DEVICE AND PROCESS MATCHING
  Devices: exact device ID, then exact friendly name, then unique substring
           (case-insensitive). Ambiguity is an error with the candidate list.
  Processes: a PID, or a process name with or without '.exe' (case-insensitive).
           Several distinct PIDs with the same name is an error; pass a PID.

EXIT CODES
  0 success            4 ambiguous device     7 access denied
  1 general error      5 session not found
  2 usage error        6 not supported
  3 device not found

NOTES
  'route' and 'reset' use an undocumented Windows internal API; the JSON output
  marks this with "usesUndocumentedApi": true. Availability is reported by
  'capabilities'. Changing the system default device is a global, immediately
  visible change and therefore requires --yes.
""");
    }
}

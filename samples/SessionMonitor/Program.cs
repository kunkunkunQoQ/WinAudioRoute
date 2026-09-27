using WinAudioRoute;
using WinAudioRoute.Events;

// SessionMonitor —— 订阅设备与会话事件并打印。
//
// 事件系统是"事件优先、TTL 兜底"：
//   - 首次订阅 DeviceChanged / SessionChanged 时才会注册会话通知（惰性）；
//   - 库内部在事件到达时立即让缓存失效，因此随后的读取是最新的；
//   - 事件来自 COM 回调线程，**不保证**是 UI 线程；订阅者异常被隔离并上报到
//     NotificationHandlerFaulted，不会穿越 COM 边界。
//
// 用法：
//   SessionMonitor            持续监听（按 Ctrl+C 退出）
//   SessionMonitor snapshot   只打印一次当前快照

using var audio = new WindowsAudioManager();

Console.WriteLine($"Device notification registered : {audio.IsDeviceNotificationRegistered}");

audio.DeviceChanged += (_, e) => Print("DEVICE ", e.ToString());
audio.SessionChanged += (_, e) => Print("SESSION", e.ToString());
audio.NotificationHandlerFaulted += (source, ex) =>
    Print("FAULT  ", $"{source}: {ex.GetType().Name}: {ex.Message}");
audio.NotificationRegistrationFailed += hresult =>
    Print("WARN   ", $"native notification registration failed (HRESULT 0x{hresult:X8}); falling back to TTL caching");

Console.WriteLine($"Event system enabled           : {audio.IsEventSystemEnabled}");
Console.WriteLine();

if (args.Length > 0 && args[0].Equals("snapshot", StringComparison.OrdinalIgnoreCase))
{
    PrintSnapshot(audio);
    return 0;
}

PrintSnapshot(audio);
Console.WriteLine();
Console.WriteLine("Listening for device and session changes. Press Ctrl+C to exit.");

using var stopped = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopped.Set();
};

stopped.Wait();
Console.WriteLine();
Console.WriteLine("Stopped.");
return 0;

static void PrintSnapshot(WindowsAudioManager audio)
{
    Console.WriteLine("== Active playback devices ==");
    foreach (AudioDevice device in audio.GetPlaybackDevices())
    {
        Console.WriteLine($"  {device.FriendlyName}  {device.Id}");
    }

    Console.WriteLine();
    Console.WriteLine("== Audio sessions ==");
    IReadOnlyList<AudioSession> sessions = audio.GetSessions();
    if (sessions.Count == 0)
    {
        Console.WriteLine("  (none)");
        return;
    }

    foreach (AudioSession session in sessions)
    {
        string volume = session.Volume is null ? "-" : $"{(int)Math.Round(session.Volume.Value * 100f)}%";
        string muted = session.IsMuted is null ? "-" : (session.IsMuted.Value ? "muted" : "unmuted");

        Console.WriteLine(
            $"  PID {session.ProcessId,-7} {session.ProcessName ?? "(unknown)",-24} " +
            $"{session.State,-8} {volume,-5} {muted}");
    }
}

static void Print(string kind, string message)
{
    // 事件可能来自任意线程：用锁保证行不被交错
    lock (Console.Out)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{kind}] {message}");
    }
}

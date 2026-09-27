using WinAudioRoute;

// DeviceSwitcher —— 列出音频设备、显示系统默认设备、按名称/ID 切换默认设备。
//
// 用法：
//   DeviceSwitcher                       列出播放/录音设备与默认设备
//   DeviceSwitcher set <name|id> [role]  把设备设为默认（role 默认 console）
//
// 注意：切换系统默认设备是全局且立即可见的状态变更。

if (args.Length >= 2 && args[0].Equals("set", StringComparison.OrdinalIgnoreCase))
{
    return SetDefault(args[1], args.Length >= 3 ? args[2] : "console");
}

return List();

static int List()
{
    using var audio = new WindowsAudioManager();

    Console.WriteLine($"Windows build : {WindowsAudioEnvironment.OsBuild}");
    Console.WriteLine($"Architecture  : {WindowsAudioEnvironment.ProcessArchitecture}");
    Console.WriteLine();

    foreach (AudioDataFlow flow in (AudioDataFlow[])[AudioDataFlow.Render, AudioDataFlow.Capture])
    {
        string label = flow == AudioDataFlow.Render ? "Playback devices" : "Recording devices";
        Console.WriteLine($"== {label} ==");

        foreach (AudioDevice device in audio.GetDevices(flow, AudioDeviceState.All))
        {
            string roles = DefaultRoles(device);
            Console.WriteLine($"  {(device.IsActive ? "[active]  " : "[inactive]")} {device.FriendlyName}");
            Console.WriteLine($"      id    = {device.Id}");
            Console.WriteLine($"      state = {device.State}{(roles.Length > 0 ? $"   default: {roles}" : string.Empty)}");
        }

        Console.WriteLine();
    }

    Console.WriteLine("== Default devices ==");
    foreach (AudioDataFlow flow in (AudioDataFlow[])[AudioDataFlow.Render, AudioDataFlow.Capture])
    {
        foreach (AudioRole role in (AudioRole[])[AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications])
        {
            AudioDevice? device = audio.GetDefaultDevice(flow, role);
            Console.WriteLine($"  {flow,-8} {role,-15} {(device is null ? "(none)" : device.FriendlyName)}");
        }
    }

    return 0;
}

static int SetDefault(string query, string roleText)
{
    if (!Enum.TryParse(roleText, ignoreCase: true, out AudioRole role))
    {
        Console.Error.WriteLine($"Unknown role '{roleText}'. Valid: Console, Multimedia, Communications.");
        return 2;
    }

    using var audio = new WindowsAudioManager();

    try
    {
        // ResolveDevice 先按 ID 精确匹配，再按名称精确匹配，最后按唯一子串匹配；
        // 命中多个会抛 AmbiguousAudioDeviceException（不会替调用方"选第一个"）。
        AudioDevice device = audio.ResolvePlaybackDevice(query);

        Console.WriteLine($"Setting {device.FriendlyName}");
        Console.WriteLine($"  id   = {device.Id}");
        Console.WriteLine($"  role = {role}");

        AudioOperationResult result = audio.SetDefaultDevice(device, role);

        if (!result.IsSuccess)
        {
            Console.Error.WriteLine($"Failed: {result}");
            return 1;
        }

        AudioDevice? after = audio.GetDefaultDevice(AudioDataFlow.Render, role);
        bool verified = after?.Id == device.Id;

        Console.WriteLine(verified ? "Verified: default device updated." : "Warning: read-back does not match.");
        return verified ? 0 : 1;
    }
    catch (AmbiguousAudioDeviceException ex)
    {
        Console.Error.WriteLine($"'{query}' matches {ex.Candidates.Count} devices:");
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
    catch (AudioRoutingNotSupportedException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 6;
    }
}

static string DefaultRoles(AudioDevice device)
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

    return string.Join(", ", roles);
}

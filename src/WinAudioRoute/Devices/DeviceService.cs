namespace WinAudioRoute.Internal;

/// <summary>
/// 设备查询服务。
/// <para>
/// 提取来源：SonicRoute.Core/AudioService.cs 的 <c>GetDevices</c> /
/// <c>GetDefaultDeviceId</c> / <c>ReadFriendlyName</c>
/// （MIT License, Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）。
/// </para>
/// <para>
/// <b>与 SonicRoute 的差异</b>：
/// <list type="bullet">
///   <item><description>枚举状态由调用方指定（SonicRoute 硬编码 <c>DeviceState.ACTIVE</c>），
///   因此支持 Disabled / NotPresent / Unplugged / All。</description></item>
///   <item><description>默认设备读走完整 Role 参数（SonicRoute 硬编码 <c>ERole.eConsole</c>），
///   支持 Console / Multimedia / Communications 三种角色。</description></item>
///   <item><description>不再使用 SonicRoute 的进程级静态 TTL 缓存。设备缓存属于
///   <see cref="WindowsAudioManager"/> 实例，并由该实例释放。</description></item>
/// </list>
/// </para>
/// <para>
/// 本服务不长期持有 COM 对象：每次调用创建并释放一个枚举器（与 SonicRoute 一致）。
/// </para>
/// </summary>
internal sealed class DeviceService
{
    private const int DefaultCacheTtlMilliseconds = 3000;

    private readonly Func<IDeviceEnumerator> _enumeratorFactory;
    private readonly TimeProvider _timeProvider;
    // 说明：System.Threading.Lock 是 .NET 9 才引入的类型，本库面向 net8.0，因此使用 object 锁。
    private readonly object _gate = new();
    private IReadOnlyList<AudioDevice>? _playbackCache;
    private IReadOnlyList<AudioDevice>? _recordingCache;
    private DateTimeOffset _playbackCachedAt;
    private DateTimeOffset _recordingCachedAt;

    /// <summary>创建服务。</summary>
    /// <param name="enumeratorFactory">枚举器工厂（默认使用真实 COM 枚举器）。</param>
    /// <param name="timeProvider">时钟（用于缓存 TTL；默认使用系统时钟）。</param>
    public DeviceService(
        Func<IDeviceEnumerator>? enumeratorFactory = null,
        TimeProvider? timeProvider = null)
    {
        _enumeratorFactory = enumeratorFactory ?? (() => new ComDeviceEnumerator());
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>缓存存活时间。默认 3 秒（沿用 SonicRoute 的 TTL）。</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMilliseconds(DefaultCacheTtlMilliseconds);

    /// <summary>
    /// 取得播放（输出）设备。
    /// </summary>
    /// <param name="states">要包含的设备状态；默认仅活动设备。</param>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存直接重新枚举。</param>
    /// <returns>设备列表。</returns>
    public IReadOnlyList<AudioDevice> GetPlaybackDevices(
        AudioDeviceState states = AudioDeviceState.Active,
        bool bypassCache = false) =>
        GetDevicesCore(AudioDataFlow.Render, states, bypassCache);

    /// <summary>
    /// 取得录音（输入）设备。
    /// </summary>
    /// <param name="states">要包含的设备状态；默认仅活动设备。</param>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存直接重新枚举。</param>
    /// <returns>设备列表。</returns>
    public IReadOnlyList<AudioDevice> GetRecordingDevices(
        AudioDeviceState states = AudioDeviceState.Active,
        bool bypassCache = false) =>
        GetDevicesCore(AudioDataFlow.Capture, states, bypassCache);

    /// <summary>
    /// 按方向取得设备。
    /// </summary>
    /// <param name="flow">数据流方向。</param>
    /// <param name="states">要包含的设备状态；默认仅活动设备。</param>
    /// <param name="bypassCache">为 <see langword="true"/> 时跳过缓存直接重新枚举。</param>
    /// <returns>设备列表。</returns>
    public IReadOnlyList<AudioDevice> GetDevices(
        AudioDataFlow flow,
        AudioDeviceState states = AudioDeviceState.Active,
        bool bypassCache = false)
    {
        if (flow == AudioDataFlow.All)
        {
            List<AudioDevice> combined = [.. GetDevicesCore(AudioDataFlow.Render, states, bypassCache)];
            combined.AddRange(GetDevicesCore(AudioDataFlow.Capture, states, bypassCache));
            return combined;
        }

        return GetDevicesCore(flow, states, bypassCache);
    }

    /// <summary>
    /// 取得指定方向与角色下的默认设备。
    /// <para>
    /// 覆盖 6 种组合（Render/Capture × Console/Multimedia/Communications）。
    /// 该组合没有端点（例如未配置默认通信设备）时返回 <see langword="null"/>。
    /// </para>
    /// </summary>
    /// <param name="flow">数据流方向。</param>
    /// <param name="role">端点角色。</param>
    /// <param name="bypassCache">
    /// 兼容参数：默认设备读取不使用缓存（每次都直接查询系统），
    /// 该参数保留是为了与其余设备查询 API 的签名一致。
    /// </param>
    /// <returns>默认设备；不存在时返回 <see langword="null"/>。</returns>
    public AudioDevice? GetDefaultDevice(
        AudioDataFlow flow,
        AudioRole role = AudioRole.Console,
        bool bypassCache = false)
    {
        _ = bypassCache;
        using IDeviceEnumerator enumerator = _enumeratorFactory();

        string? id = enumerator.GetDefaultDeviceId(flow, role);
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        AudioDevice? device = enumerator.GetDevice(id);
        if (device is null)
        {
            // 设备存在但属性读取失败：仍然返回一个仅含 ID 的快照，而不是静默返回 null
            return new AudioDevice
            {
                Id = id,
                FriendlyName = string.Empty,
                Flow = flow,
                State = AudioDeviceState.Active,
            };
        }

        // 按 ID 查回的设备方向未知（ComDeviceEnumerator 默认按 Render 填充），此处按查询方向修正
        return device.Flow == flow ? device : device with { Flow = flow };
    }

    /// <summary>
    /// 在给定的已知设备集合中解析一个设备；用于把"持久化路由里的设备 ID"还原成
    /// 完整的 <see cref="AudioDevice"/>（含默认设备标记与状态）。
    /// <para>
    /// 集合中没有该设备时（例如已拔出、已禁用、或从未出现在各端点列表中），
    /// 回退到按 ID 直接读取属性存储，并按 <paramref name="flow"/> 构造快照，
    /// 以保证调用方仍然拿到一个方向正确、ID 正确的设备对象。
    /// </para>
    /// <para><b>只读</b>：不修改设备缓存。</para>
    /// </summary>
    /// <param name="deviceId">设备短 ID。</param>
    /// <param name="flow">设备方向。</param>
    /// <param name="known">
    /// 已知的候选设备集合（通常是 <c>GetDevices(flow, AudioDeviceState.All)</c> 的结果）。
    /// 传 <see langword="null"/> 时仅走回退路径。
    /// </param>
    /// <returns>设备快照；无法构造时返回 <see langword="null"/>。</returns>
    internal AudioDevice? ResolveDevice(
        string deviceId,
        AudioDataFlow flow,
        IReadOnlyList<AudioDevice>? known)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        if (known is not null)
        {
            foreach (AudioDevice candidate in known)
            {
                if (string.Equals(candidate.Id, deviceId, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }

        // 回退：直接按 ID 读取（属性存储与方向无关，但状态需要重新查询）
        using IDeviceEnumerator enumerator = _enumeratorFactory();
        AudioDevice? device = enumerator.GetDevice(deviceId);

        return device is null
            ? new AudioDevice
            {
                Id = deviceId,
                FriendlyName = string.Empty,
                Flow = flow,
                State = AudioDeviceState.None,
            }
            : device.Flow == flow ? device : device with { Flow = flow };
    }

    /// <summary>
    /// 清空设备缓存。事件系统（后续阶段）到达时应调用本方法使缓存失效。
    /// </summary>
    public void InvalidateCache()
    {
        lock (_gate)
        {
            _playbackCache = null;
            _recordingCache = null;
        }
    }

    private IReadOnlyList<AudioDevice> GetDevicesCore(
        AudioDataFlow flow,
        AudioDeviceState states,
        bool bypassCache)
    {
        // 契约前置：状态掩码在服务层校验（不依赖具体 IDeviceEnumerator 实现）
        states = DeviceStateMask.Normalize(states);

        bool cacheable = states == AudioDeviceState.Active;

        if (!bypassCache && cacheable)
        {
            IReadOnlyList<AudioDevice>? cached = TryGetCached(flow);
            if (cached is not null)
            {
                return cached;
            }
        }

        IReadOnlyList<AudioDevice> devices;
        using (IDeviceEnumerator enumerator = _enumeratorFactory())
        {
            devices = enumerator.GetDevices(flow, states);
        }

        devices = MarkDefaultDevices(devices, requestedDeviceId: null);

        if (cacheable)
        {
            Store(flow, devices);
        }

        return devices;
    }

    /// <summary>
    /// 为单个设备补齐默认标记与真实状态（供按应用路由解析设备时使用）。
    /// <para><b>只读</b>：不写缓存（可以读缓存以避免重复枚举）。</para>
    /// </summary>
    /// <param name="deviceId">设备短 ID。</param>
    /// <param name="flow">设备方向。</param>
    /// <returns>
    /// 设备快照（含三个 Role 的默认标记与状态）；参数为空或无法构造时返回 <see langword="null"/>。
    /// </returns>
    internal AudioDevice? GetDeviceWithDefaultFlags(string deviceId, AudioDataFlow flow)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        // 优先从缓存/枚举结果中找
        IReadOnlyList<AudioDevice>? cached = TryGetCached(flow);
        if (cached is not null)
        {
            foreach (AudioDevice device in cached)
            {
                if (string.Equals(device.Id, deviceId, StringComparison.Ordinal))
                {
                    return device;
                }
            }
        }

        // 目标设备可能处于非 Active 状态（已禁用/未插入/不存在）→ 用全部状态再找一次
        IReadOnlyList<AudioDevice> all = ResolveDevicesInternal(flow, AudioDeviceState.All, requestedDeviceId: null);
        foreach (AudioDevice device in all)
        {
            if (string.Equals(device.Id, deviceId, StringComparison.Ordinal))
            {
                return device;
            }
        }

        // 设备不在任何端点枚举结果中（例如已被彻底移除）：回退按 ID 读取属性存储
        using IDeviceEnumerator enumerator = _enumeratorFactory();
        AudioDevice? single = enumerator.GetDevice(deviceId);

        return single is null
            ? new AudioDevice
            {
                Id = deviceId,
                FriendlyName = string.Empty,
                Flow = flow,
                State = AudioDeviceState.None,
            }
            : single.Flow == flow ? single : single with { Flow = flow };
    }

    /// <summary>
    /// 枚举设备并标记默认角色（只读，不写缓存）。
    /// </summary>
    /// <param name="flow">方向。</param>
    /// <param name="states">状态掩码。</param>
    /// <param name="requestedDeviceId">保留参数（当前未用于额外解析）。</param>
    private IReadOnlyList<AudioDevice> ResolveDevicesInternal(
        AudioDataFlow flow,
        AudioDeviceState states,
        string? requestedDeviceId)
    {
        _ = requestedDeviceId;

        states = DeviceStateMask.Normalize(states);

        IReadOnlyList<AudioDevice> devices;
        using (IDeviceEnumerator enumerator = _enumeratorFactory())
        {
            devices = enumerator.GetDevices(flow, states);
        }

        return MarkDefaultDevices(devices, requestedDeviceId: null);
    }

    private IReadOnlyList<AudioDevice>? TryGetCached(AudioDataFlow flow)
    {
        lock (_gate)
        {
            IReadOnlyList<AudioDevice>? cached = flow == AudioDataFlow.Render ? _playbackCache : _recordingCache;
            DateTimeOffset cachedAt = flow == AudioDataFlow.Render ? _playbackCachedAt : _recordingCachedAt;

            if (cached is null)
            {
                return null;
            }

            return _timeProvider.GetUtcNow() - cachedAt < CacheTtl ? cached : null;
        }
    }

    private void Store(AudioDataFlow flow, IReadOnlyList<AudioDevice> devices)
    {
        lock (_gate)
        {
            if (flow == AudioDataFlow.Render)
            {
                _playbackCache = devices;
                _playbackCachedAt = _timeProvider.GetUtcNow();
            }
            else
            {
                _recordingCache = devices;
                _recordingCachedAt = _timeProvider.GetUtcNow();
            }
        }
    }

    /// <summary>
    /// 一次性读取该方向三个 Role 的默认设备 ID，并据此标记每个设备是否为默认设备。
    /// 这样每个枚举结果最多额外 3 次 <c>GetDefaultAudioEndpoint</c> 调用。
    /// </summary>
    /// <param name="devices">已枚举的设备。</param>
    /// <param name="requestedDeviceId">
    /// 可选的额外设备 ID（例如持久化路由指向但当前不在枚举结果中的设备）。
    /// 提供时会在标记后再回读该设备，以保证调用方仍能拿到方向与状态正确的对象。
    /// </param>
    private IReadOnlyList<AudioDevice> MarkDefaultDevices(
        IReadOnlyList<AudioDevice> devices,
        string? requestedDeviceId)
    {
        if (devices.Count == 0 && string.IsNullOrEmpty(requestedDeviceId))
        {
            return devices;
        }

        // 设备集合为空但需要解析指定设备时，用调用方给出的方向；否则以首个设备为准
        AudioDataFlow flow = devices.Count > 0 ? devices[0].Flow : AudioDataFlow.Render;

        using IDeviceEnumerator enumerator = _enumeratorFactory();

        string? console = enumerator.GetDefaultDeviceId(flow, AudioRole.Console);
        string? multimedia = enumerator.GetDefaultDeviceId(flow, AudioRole.Multimedia);
        string? communications = enumerator.GetDefaultDeviceId(flow, AudioRole.Communications);

        if (console is null && multimedia is null && communications is null)
        {
            return devices;
        }

        var result = new List<AudioDevice>(devices.Count);
        foreach (AudioDevice device in devices)
        {
            result.Add(device with
            {
                IsDefaultConsole = console is not null && string.Equals(device.Id, console, StringComparison.Ordinal),
                IsDefaultMultimedia = multimedia is not null && string.Equals(device.Id, multimedia, StringComparison.Ordinal),
                IsDefaultCommunications = communications is not null && string.Equals(device.Id, communications, StringComparison.Ordinal),
            });
        }

        return result;
    }
}

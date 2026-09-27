using WinAudioRoute.Interop;
using WinAudioRoute.Internal;

namespace WinAudioRoute.Tests;

/// <summary>
/// 按需前进的 <see cref="TimeProvider"/>，用于确定性地测试 TTL / 节流逻辑。
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>把时钟向前推进。</summary>
    /// <param name="delta">推进量。</param>
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

/// <summary>
/// 跨多个 <see cref="FakeDeviceEnumerator"/> 实例共享的调用记录。
/// <para>
/// 之所以需要共享：服务会在每次使用后<b>释放</b>枚举器，因此正确的测试替身必须
/// "每次调用工厂返回一个新实例"（与真实 <c>ComDeviceEnumerator</c> 一致）。
/// 调用统计因此不能放在单个实例上。
/// </para>
/// </summary>
internal sealed class EnumeratorRecorder
{
    /// <summary>创建过的枚举器数量。</summary>
    public int CreatedCount;

    /// <summary>枚举调用总次数。</summary>
    public int EnumerateCalls;

    /// <summary>被释放的枚举器数量。</summary>
    public int DisposeCount;

    /// <summary>收到过的状态掩码。</summary>
    public List<AudioDeviceState> RequestedStates { get; } = [];

    /// <summary>收到过的枚举方向。</summary>
    public List<AudioDataFlow> DevicesRequested { get; } = [];
}

/// <summary>
/// 测试替身：内存中的设备枚举器，记录调用并模拟释放。
/// <para>
/// <see cref="OpenDeviceObject"/> 返回 <see langword="null"/>：本替身不提供真实
/// <c>IMMDevice</c>，因此会话枚举路径会按"该设备没有会话"处理。
/// </para>
/// </summary>
internal sealed class FakeDeviceEnumerator : IDeviceEnumerator
{
    private readonly EnumeratorRecorder _recorder;

    /// <summary>创建一个记录到共享记录器的替身。</summary>
    /// <param name="recorder">共享记录器。</param>
    /// <param name="devices">可选的设备来源（默认使用空集合）。</param>
    /// <param name="defaults">可选的默认设备来源。</param>
    public FakeDeviceEnumerator(
        EnumeratorRecorder recorder,
        Dictionary<AudioDataFlow, IReadOnlyList<AudioDevice>>? devices = null,
        Dictionary<(AudioDataFlow Flow, AudioRole Role), string?>? defaults = null)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        Devices = devices ?? [];
        Defaults = defaults ?? [];
        Interlocked.Increment(ref recorder.CreatedCount);
    }

    /// <summary>各方向返回的设备。</summary>
    public Dictionary<AudioDataFlow, IReadOnlyList<AudioDevice>> Devices { get; }

    /// <summary>各 (方向, 角色) 的默认设备 ID。</summary>
    public Dictionary<(AudioDataFlow Flow, AudioRole Role), string?> Defaults { get; }

    /// <summary>本实例是否已释放。</summary>
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<AudioDevice> GetDevices(AudioDataFlow flow, AudioDeviceState states)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        Interlocked.Increment(ref _recorder.EnumerateCalls);
        _recorder.DevicesRequested.Add(flow);
        _recorder.RequestedStates.Add(states);

        return Devices.TryGetValue(flow, out IReadOnlyList<AudioDevice>? devices) ? devices : [];
    }

    /// <inheritdoc />
    public AudioDevice? GetDevice(string deviceId) =>
        Devices.Values.SelectMany(d => d).FirstOrDefault(d => d.Id == deviceId);

    /// <inheritdoc />
    public string? GetDefaultDeviceId(AudioDataFlow flow, AudioRole role) =>
        Defaults.TryGetValue((flow, role), out string? id) ? id : null;

    /// <inheritdoc />
    public IMMDevice? OpenDeviceObject(string deviceId) => null;

    /// <inheritdoc />
    public string? GetPlaybackDeviceIdForNotifications() => null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Interlocked.Increment(ref _recorder.DisposeCount);
    }
}

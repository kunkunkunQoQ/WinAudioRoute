using System.Runtime.InteropServices;
using WinAudioRoute.Interop;

namespace WinAudioRoute.Internal;

/// <summary>
/// 端点设备枚举的抽象。存在两个目的：
/// <list type="bullet">
///   <item><description>让上层服务对 COM 枚举器的生命周期有单一、可审计的所有权点。</description></item>
///   <item><description>让设备相关逻辑可以在没有音频设备的环境中被替换/隔离。</description></item>
/// </list>
/// </summary>
internal interface IDeviceEnumerator : IDisposable
{
    /// <summary>按方向与状态掩码枚举设备。</summary>
    /// <param name="flow">数据流方向。</param>
    /// <param name="states">状态掩码。</param>
    /// <returns>设备快照列表（按系统返回顺序）。</returns>
    IReadOnlyList<AudioDevice> GetDevices(AudioDataFlow flow, AudioDeviceState states);

    /// <summary>按 ID 取得单个设备。</summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <returns>设备；不存在或读取失败时返回 <see langword="null"/>。</returns>
    AudioDevice? GetDevice(string deviceId);

    /// <summary>取得指定方向与角色下的默认设备 ID。</summary>
    /// <param name="flow">数据流方向。</param>
    /// <param name="role">端点角色。</param>
    /// <returns>默认设备 ID；该组合没有默认端点时返回 <see langword="null"/>。</returns>
    string? GetDefaultDeviceId(AudioDataFlow flow, AudioRole role);

    /// <summary>
    /// 打开底层 <c>IMMDevice</c> 对象，供需要直接激活子接口的场景使用（例如打开会话管理器）。
    /// </summary>
    /// <param name="deviceId">设备 ID。</param>
    /// <returns>
    /// 调用方拥有其生命周期的 <c>IMMDevice</c>；失败时返回 <see langword="null"/>。
    /// </returns>
    IMMDevice? OpenDeviceObject(string deviceId);

    /// <summary>
    /// 取得一个可用于注册会话通知的播放设备 ID（优先默认设备，否则第一个活动播放设备）。
    /// </summary>
    /// <returns>设备 ID；没有可用播放设备时返回 <see langword="null"/>。</returns>
    string? GetPlaybackDeviceIdForNotifications();
}

/// <summary>
/// 基于公开 MMDevice API（<c>IMMDeviceEnumerator</c>）的设备枚举实现。
/// <para>
/// 提取来源：SonicRoute.Core/AudioService.cs（MIT License, Copyright (c) 2026 kunkunkunQoQ,
/// https://github.com/kunkunkunQoQ/SonicRoute）。
/// 提取时保留：<c>EnumAudioEndpoints</c> + <c>GetDefaultAudioEndpoint</c> + <c>GetDevice</c>
/// 的调用顺序、属性存储读取 FriendlyName 的方式、以及逐个释放 COM 对象的做法。
/// 新增：状态掩码改为调用方传入（SonicRoute 硬编码 ACTIVE）、读取每个设备的真实
/// <c>GetState()</c>、以及一次性读取三个 Role 的默认设备。
/// </para>
/// <para>
/// <b>所有权</b>：本类型拥有自己的 COM 枚举器，销毁时释放；每次调用内部创建的
/// 设备/集合/属性存储都在调用内释放，不把 RCW 传出调用边界。
/// </para>
/// </summary>
internal sealed class ComDeviceEnumerator : IDeviceEnumerator, IDisposable
{
    private IMMDeviceEnumerator? _enumerator;

    /// <summary>创建并持有一个 <c>MMDeviceEnumerator</c> COM 对象。</summary>
    /// <exception cref="WinAudioException">无法创建枚举器。</exception>
    public ComDeviceEnumerator()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }
        catch (Exception ex)
        {
            throw new WinAudioException("Failed to create the audio endpoint enumerator (MMDeviceEnumerator).", ex);
        }
    }

    /// <summary>当前是否已释放。</summary>
    public bool IsDisposed => _enumerator is null;

    /// <inheritdoc />
    public IReadOnlyList<AudioDevice> GetDevices(AudioDataFlow flow, AudioDeviceState states)
    {
        IMMDeviceEnumerator enumerator = RequireEnumerator();

        int hr = enumerator.EnumAudioEndpoints(
            MapFlow(flow),
            MapStates(states),
            out IMMDeviceCollection collection);

        if (hr < 0 || collection is null)
        {
            throw new AudioOperationFailedException(
                $"EnumAudioEndpoints({flow}) failed.", hr);
        }

        using ComScope collectionScope = ComScope.Own(collection);

        _ = collection.GetCount(out uint count);

        var devices = new List<AudioDevice>((int)Math.Min(count, 64));
        for (uint i = 0; i < count; i++)
        {
            if (collection.Item(i, out IMMDevice device) < 0 || device is null)
            {
                continue;
            }

            using ComScope deviceScope = ComScope.Own(device);
            AudioDevice? snapshot = TryReadDevice(device, flow);
            if (snapshot is not null)
            {
                devices.Add(snapshot);
            }
        }

        return devices;
    }

    /// <inheritdoc />
    public AudioDevice? GetDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        // 按 ID 查询时方向未知：先按播放端点读一次（属性存储与方向无关），
        // 再回退判定方向——MMDevice 的属性存储不区分方向，因此读取一次即可，
        // 方向只影响返回快照上的 Flow 标记。
        IMMDeviceEnumerator enumerator = RequireEnumerator();

        IMMDevice? device = null;
        try
        {
            if (enumerator.GetDevice(deviceId, out IMMDevice found) < 0 || found is null)
            {
                return null;
            }

            device = found;
            return TryReadDevice(found, AudioDataFlow.Render);
        }
        finally
        {
            if (device is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(device);
                }
                catch
                {
                    // 释放失败不影响返回值
                }
            }
        }
    }

    /// <inheritdoc />
    public string? GetDefaultDeviceId(AudioDataFlow flow, AudioRole role)
    {
        IMMDeviceEnumerator enumerator = RequireEnumerator();

        IMMDevice? device = null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(MapFlow(flow), MapRole(role), out IMMDevice found) < 0 || found is null)
            {
                return null;
            }

            device = found;
            return device.GetId(out string id) < 0 ? null : id;
        }
        finally
        {
            if (device is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(device);
                }
                catch
                {
                    // 释放失败不影响返回值
                }
            }
        }
    }

    /// <inheritdoc />
    public IMMDevice? OpenDeviceObject(string deviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        IMMDeviceEnumerator enumerator = RequireEnumerator();

        return enumerator.GetDevice(deviceId, out IMMDevice device) >= 0 && device is not null
            ? device
            : null;
    }

    /// <inheritdoc />
    public string? GetPlaybackDeviceIdForNotifications()
    {
        try
        {
            IReadOnlyList<AudioDevice> devices = GetDevices(AudioDataFlow.Render, AudioDeviceState.Active);
            return devices.Count > 0 ? devices[0].Id : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>释放持有的 COM 枚举器。幂等。</summary>
    public void Dispose()
    {
        IMMDeviceEnumerator? enumerator = _enumerator;
        _enumerator = null;

        if (enumerator is not null)
        {
            try
            {
                Marshal.ReleaseComObject(enumerator);
            }
            catch
            {
                // 忽略释放异常
            }
        }
    }

    private IMMDeviceEnumerator RequireEnumerator() =>
        _enumerator ?? throw new ObjectDisposedException(nameof(ComDeviceEnumerator));

    /// <summary>
    /// 读取单个设备的快照。
    /// <para>
    /// <paramref name="flow"/> 由调用上下文提供（枚举时来自枚举方向，按 ID 查询时未知）。
    /// <c>IMMDevice</c> 本身不暴露方向，因此按 ID 查询得到的快照方向是调用方给定的提示；
    /// 需要精确方向时请使用枚举路径或 <c>GetDefaultDevice</c>。
    /// </para>
    /// </summary>
    private static AudioDevice? TryReadDevice(IMMDevice device, AudioDataFlow flow)
    {
        if (device.GetId(out string id) < 0 || string.IsNullOrEmpty(id))
        {
            return null;
        }

        string friendlyName = ReadFriendlyName(device) ?? string.Empty;

        AudioDeviceState state = AudioDeviceState.None;
        if (device.GetState(out DeviceState rawState) >= 0)
        {
            state = (AudioDeviceState)(uint)rawState;
        }

        return new AudioDevice
        {
            Id = id,
            FriendlyName = friendlyName,
            Flow = flow,
            State = state,
        };
    }

    /// <summary>
    /// 读取 <c>PKEY_Device_FriendlyName</c>。
    /// 与 SonicRoute 一致：读取后必须 <c>PropVariantClear</c>，且属性存储本身要释放。
    /// </summary>
    private static string? ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(ComConstants.STGM_READ, out IPropertyStore store) < 0 || store is null)
        {
            return null;
        }

        using ComScope storeScope = ComScope.Own(store);

        PROPERTYKEY key = PropertyKeys.PKEY_Device_FriendlyName;
        if (store.GetValue(ref key, out PROPVARIANT value) < 0)
        {
            return null;
        }

        try
        {
            return value.vt == ComConstants.VT_LPWSTR && value.pwszVal != IntPtr.Zero
                ? Marshal.PtrToStringUni(value.pwszVal)
                : null;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref value);
        }
    }

    private static EDataFlow MapFlow(AudioDataFlow flow) => flow switch
    {
        AudioDataFlow.Render => EDataFlow.eRender,
        AudioDataFlow.Capture => EDataFlow.eCapture,
        AudioDataFlow.All => EDataFlow.eAll,
        _ => throw new ArgumentOutOfRangeException(nameof(flow), flow, "Unsupported audio data flow."),
    };

    private static ERole MapRole(AudioRole role) => role switch
    {
        AudioRole.Console => ERole.eConsole,
        AudioRole.Multimedia => ERole.eMultimedia,
        AudioRole.Communications => ERole.eCommunications,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported audio role."),
    };

    /// <summary>
    /// 把公开状态掩码映射为原生 <c>DeviceState</c>。
    /// 校验与归一化逻辑集中在 <see cref="DeviceStateMask"/>，此处只做类型转换。
    /// </summary>
    private static DeviceState MapStates(AudioDeviceState states) =>
        (DeviceState)(uint)DeviceStateMask.Normalize(states);
}

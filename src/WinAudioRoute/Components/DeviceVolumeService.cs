namespace WinAudioRoute.Internal;

/// <summary>
/// 设备级音量与静音服务（<c>IAudioEndpointVolume</c>），同时支持播放与录音设备。
/// <para>
/// 提取来源：SonicRoute.Core/SystemVolumeService.cs
/// （MIT License, Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）。
/// </para>
/// <para>
/// <b>与 SonicRoute 的差异</b>：
/// <list type="bullet">
///   <item><description>SonicRoute 只做播放方向（<c>eRender</c>），本服务同时支持录音设备。</description></item>
///   <item><description>不再用 <c>deviceId == null</c> 表示"系统默认设备"的隐含语义；
///   调用方先 <c>GetDefaultDevice(...)</c> 再传入具体设备。</description></item>
///   <item><description>音量单位是标量 0.0–1.0（SonicRoute 用 0–100 的 <see cref="int"/>）。</description></item>
/// </list>
/// </para>
/// </summary>
internal sealed class DeviceVolumeService
{
    private static readonly Guid IID_IAudioEndpointVolume =
        new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private readonly Func<IDeviceEnumerator> _enumeratorFactory;

    /// <summary>创建服务。</summary>
    /// <param name="enumeratorFactory">设备枚举器工厂。</param>
    public DeviceVolumeService(Func<IDeviceEnumerator>? enumeratorFactory = null)
    {
        _enumeratorFactory = enumeratorFactory ?? (() => new ComDeviceEnumerator());
    }

    /// <summary>
    /// 读取设备音量标量（0.0–1.0）。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <returns>0.0–1.0 的标量音量。</returns>
    /// <exception cref="AudioDeviceNotFoundException">设备不存在（例如已拔出）。</exception>
    /// <exception cref="AudioOperationFailedException">设备不提供音量接口，或读取失败。</exception>
    public float GetVolume(AudioDevice device)
    {
        using VolumeContext context = CreateContext(device);

        int hr = context.Volume.GetMasterVolumeLevelScalar(out float level);
        if (hr < 0)
        {
            throw new AudioOperationFailedException(
                $"Failed to read the volume of device '{device.Id}'.", hr);
        }

        // Windows 允许返回值略高于 1.0（例如 1.0001221），统一收敛到契约范围
        return level > AudioVolume.MaxScalar ? AudioVolume.MaxScalar : AudioVolume.Clamp(level);
    }

    /// <summary>
    /// 设置设备音量。<para>越界值会被 <b>钳制</b>到 0.0–1.0（不抛异常），这是本 API 的明确契约。</para>
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="volume">标量音量；越界值被钳制。</param>
    /// <exception cref="AudioDeviceNotFoundException">设备不存在。</exception>
    /// <exception cref="AudioOperationFailedException">设备不提供音量接口，或写入失败。</exception>
    public void SetVolume(AudioDevice device, float volume)
    {
        using VolumeContext context = CreateContext(device);

        float clamped = AudioVolume.Clamp(volume);
        Guid eventContext = Guid.Empty;

        int hr = context.Volume.SetMasterVolumeLevelScalar(clamped, ref eventContext);
        if (hr < 0)
        {
            throw new AudioOperationFailedException(
                $"Failed to set the volume of device '{device.Id}' to {clamped}.", hr);
        }
    }

    /// <summary>
    /// 读取设备静音状态。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <returns>是否静音。</returns>
    /// <exception cref="AudioDeviceNotFoundException">设备不存在。</exception>
    /// <exception cref="AudioOperationFailedException">设备不提供音量接口，或读取失败。</exception>
    public bool GetMute(AudioDevice device)
    {
        using VolumeContext context = CreateContext(device);

        int hr = context.Volume.GetMute(out int muted);
        if (hr < 0)
        {
            throw new AudioOperationFailedException(
                $"Failed to read the mute state of device '{device.Id}'.", hr);
        }

        return muted != 0;
    }

    /// <summary>
    /// 设置设备静音状态。
    /// </summary>
    /// <param name="device">目标设备。</param>
    /// <param name="muted">是否静音。</param>
    /// <exception cref="AudioDeviceNotFoundException">设备不存在。</exception>
    /// <exception cref="AudioOperationFailedException">设备不提供音量接口，或写入失败。</exception>
    public void SetMute(AudioDevice device, bool muted)
    {
        using VolumeContext context = CreateContext(device);

        Guid eventContext = Guid.Empty;
        int hr = context.Volume.SetMute(muted ? 1 : 0, ref eventContext);
        if (hr < 0)
        {
            throw new AudioOperationFailedException(
                $"Failed to set the mute state of device '{device.Id}'.", hr);
        }
    }

    /// <summary>
    /// 打开设备上的 <c>IAudioEndpointVolume</c>，返回其生命周期作用域。
    /// </summary>
    private VolumeContext CreateContext(AudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        IDeviceEnumerator enumerator;
        try
        {
            enumerator = _enumeratorFactory();
        }
        catch (Exception ex)
        {
            throw new WinAudioException("Failed to create the audio endpoint enumerator.", ex);
        }

        using (enumerator)
        {
            Interop.IMMDevice? deviceObject = enumerator.OpenDeviceObject(device.Id);
            if (deviceObject is null)
            {
                throw new AudioDeviceNotFoundException(
                    $"The audio device '{device.Id}' could not be opened. It may have been removed.")
                {
                    Query = device.Id,
                    Flow = device.Flow,
                };
            }

            using ComScope deviceScope = ComScope.Own(deviceObject);

            Guid iid = IID_IAudioEndpointVolume;
            int hr = deviceObject.Activate(ref iid, Interop.ComConstants.CLSCTX_ALL, IntPtr.Zero, out object volumeObject);

            if (hr < 0 || volumeObject is null)
            {
                throw new AudioOperationFailedException(
                    $"Device '{device.Id}' does not expose the volume interface (IAudioEndpointVolume).", hr);
            }

            // IAudioEndpointVolume 与本服务同寿命：所有权移交给 VolumeContext
            return new VolumeContext(volumeObject);
        }
    }

    /// <summary>
    /// 持有 <c>IAudioEndpointVolume</c> 引用的生命周期作用域。
    /// </summary>
    private sealed class VolumeContext : IDisposable
    {
        private Interop.IAudioEndpointVolume? _volume;

        public VolumeContext(object volumeObject)
        {
            Volume = (Interop.IAudioEndpointVolume)volumeObject;
            _volume = Volume;
        }

        /// <summary>已激活的音量接口。</summary>
        public Interop.IAudioEndpointVolume Volume { get; }

        public void Dispose()
        {
            Interop.IAudioEndpointVolume? volume = _volume;
            _volume = null;

            if (volume is not null)
            {
                try
                {
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(volume);
                }
                catch
                {
                    // 忽略释放异常
                }
            }
        }
    }
}

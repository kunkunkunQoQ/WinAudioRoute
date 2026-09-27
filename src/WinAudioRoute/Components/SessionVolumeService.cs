using WinAudioRoute.Interop;

namespace WinAudioRoute.Internal;

/// <summary>
/// 会话级（应用级）音量与静音服务。
/// <para>
/// 提取来源：SonicRoute.Core/SessionVolumeService.cs
/// （MIT License, Copyright (c) 2026 kunkunkunQoQ, https://github.com/kunkunkunQoQ/SonicRoute）。
/// </para>
/// <para>
/// <b>沿用 SonicRoute 已验证的核心语义</b>（"读第一个 / 写全部"），并把它显式文档化：
/// <list type="bullet">
///   <item><description><b>读取</b>：同一 PID 的全部会话使用<b>首个会话（枚举顺序）</b>作为代表值。
///   之所以可以用首会话代表：同一进程的会话源自同一应用音量设置，
///   而写入路径会遍历全部会话，因此理论上它们保持一致。</description></item>
///   <item><description><b>写入</b>：遍历该 PID 的<b>全部</b>会话，逐个写入；
///   返回逐项结果而不是 <see langword="bool"/>，因此不会丢失部分失败信息。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>与 SonicRoute 的差异</b>：
/// <list type="bullet">
///   <item><description>不再把 <c>ISimpleAudioVolume</c> 存进进程级静态字典；
///   会话引用由 <see cref="SessionService"/> 的实例级缓存独占持有。</description></item>
///   <item><description>音量单位是标量 0.0–1.0（SonicRoute 的核心格式是 0–100 的 <see cref="int"/>）。</description></item>
///   <item><description>每个会话的失败原因（HRESULT）都会出现在结果里。</description></item>
///   <item><description>SonicRoute 用"失败后重试一次 + 强制刷新"补偿失效会话；
///   本服务改为<b>写操作前总是重新枚举</b>（<c>bypassCache: true</c>），
///   从根因上避免"作用在已失效会话上"。见 <see cref="SessionService.GetHandlesForProcess"/>。</description></item>
/// </list>
/// </para>
/// </summary>
internal sealed class SessionVolumeService
{
    private readonly SessionService _sessions;

    /// <summary>创建服务。</summary>
    /// <param name="sessions">会话枚举服务（同时是会话 COM 引用的所有者）。</param>
    public SessionVolumeService(SessionService sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <summary>
    /// 读取应用（PID）的当前音量标量。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <returns>0.0–1.0 的标量音量。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="processId"/> 不是正数。</exception>
    /// <exception cref="AudioSessionNotFoundException">该 PID 没有任何音频会话。</exception>
    /// <exception cref="AudioOperationFailedException">会话不提供音量接口，或读取失败。</exception>
    public float GetVolume(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        IReadOnlyList<AudioSessionHandle> handles = _sessions.GetHandlesForProcess(processId);
        if (handles.Count == 0)
        {
            throw new AudioSessionNotFoundException(
                $"No audio sessions were found for process {processId}.")
            {
                Query = processId.ToString(),
            };
        }

        AudioSessionHandle first = handles[0];
        float? volume = first.TryGetVolumeScalar();

        if (volume is null)
        {
            throw new AudioOperationFailedException(
                $"The first audio session of process {processId} does not expose a volume interface " +
                "(ISimpleAudioVolume) or the read failed.",
                hresult: 0);
        }

        return AudioVolume.Clamp(volume.Value);
    }

    /// <summary>
    /// 读取应用（PID）的当前静音状态。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <returns>是否静音。</returns>
    /// <exception cref="AudioSessionNotFoundException">该 PID 没有任何音频会话。</exception>
    /// <exception cref="AudioOperationFailedException">会话不提供音量接口，或读取失败。</exception>
    public bool GetMute(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        IReadOnlyList<AudioSessionHandle> handles = _sessions.GetHandlesForProcess(processId);
        if (handles.Count == 0)
        {
            throw new AudioSessionNotFoundException(
                $"No audio sessions were found for process {processId}.")
            {
                Query = processId.ToString(),
            };
        }

        bool? muted = handles[0].TryGetMute();
        if (muted is null)
        {
            throw new AudioOperationFailedException(
                $"The first audio session of process {processId} does not expose a volume interface " +
                "(ISimpleAudioVolume) or the read failed.",
                hresult: 0);
        }

        return muted.Value;
    }

    /// <summary>
    /// 设置应用（PID）的音量：写入该 PID 的全部会话。
    /// <para>越界值会被 <b>钳制</b>到 0.0–1.0（不抛异常），这是本 API 的明确契约。</para>
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <param name="volume">标量音量；越界值被钳制。</param>
    /// <returns>逐会话结果。</returns>
    /// <exception cref="AudioSessionNotFoundException">该 PID 没有任何音频会话。</exception>
    public AudioOperationResult SetVolume(int processId, float volume)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        float clamped = AudioVolume.Clamp(volume);
        Guid eventContext = Guid.Empty;

        return ApplyToProcessSessions(
            processId,
            static (handle, state) =>
            {
                ISimpleAudioVolume? target = state.Volume;
                if (target is null)
                {
                    return -1;
                }

                float value = state.Clamped;
                Guid context = state.EventContext;
                return target.SetMasterVolume(value, ref context);
            },
            new WriteState { Clamped = clamped, EventContext = eventContext });
    }

    /// <summary>
    /// 设置应用（PID）的静音状态：写入该 PID 的全部会话。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <param name="muted">是否静音。</param>
    /// <returns>逐会话结果。</returns>
    /// <exception cref="AudioSessionNotFoundException">该 PID 没有任何音频会话。</exception>
    public AudioOperationResult SetMute(int processId, bool muted)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        return ApplyToProcessSessions(
            processId,
            static (handle, state) =>
            {
                ISimpleAudioVolume? target = state.Volume;
                if (target is null)
                {
                    return -1;
                }

                Guid context = state.EventContext;
                return target.SetMute(state.Muted ? 1 : 0, ref context);
            },
            new WriteState { Muted = muted });
    }

    /// <summary>会话写入所需的参数（避免闭包分配与可变捕获）。</summary>
    private sealed class WriteState
    {
        /// <summary>要写入的音量标量。</summary>
        public float Clamped { get; init; }

        /// <summary>要写入的静音状态。</summary>
        public bool Muted { get; init; }

        /// <summary>COM 事件上下文 GUID（本库不区分事件来源，统一使用空 GUID）。</summary>
        public Guid EventContext { get; init; }

        /// <summary>当前正在写入的会话音量接口；每次写入前由调用方填充。</summary>
        public ISimpleAudioVolume? Volume { get; set; }
    }

    /// <summary>
    /// 对指定 PID 的全部会话执行一次写入，返回逐项结果。
    /// </summary>
    private AudioOperationResult ApplyToProcessSessions(
        int processId,
        Func<AudioSessionHandle, WriteState, int> write,
        WriteState state)
    {
        IReadOnlyList<AudioSessionHandle> handles = _sessions.GetHandlesForProcess(processId);

        if (handles.Count == 0)
        {
            throw new AudioSessionNotFoundException(
                $"No audio sessions were found for process {processId}.")
            {
                Query = processId.ToString(),
            };
        }

        int succeeded = 0;
        List<AudioOperationFailure> failures = [];

        foreach (AudioSessionHandle handle in handles)
        {
            string target = DescribeTarget(handle);
            int hr;

            try
            {
                state.Volume = handle.TryGetVolume();
                if (state.Volume is null)
                {
                    failures.Add(new AudioOperationFailure(
                        target, 0, "The session does not expose a volume interface (ISimpleAudioVolume)."));
                    continue;
                }

                hr = write(handle, state);
            }
            catch (Exception ex)
            {
                failures.Add(new AudioOperationFailure(target, 0, ex.Message));
                continue;
            }
            finally
            {
                state.Volume = null;
            }

            if (hr >= 0)
            {
                succeeded++;
            }
            else
            {
                failures.Add(new AudioOperationFailure(target, hr, "The COM call failed."));
            }
        }

        return failures.Count == 0
            ? AudioOperationResult.AllSucceeded(succeeded)
            : succeeded > 0
                ? AudioOperationResult.Partial(succeeded, failures)
                : AudioOperationResult.AllFailed(failures);
    }

    /// <summary>构造失败诊断用的目标标识。</summary>
    private static string DescribeTarget(AudioSessionHandle handle)
    {
        string instance = string.IsNullOrEmpty(handle.SessionInstanceIdentifier)
            ? "(no instance id)"
            : handle.SessionInstanceIdentifier;
        return $"{instance} [PID {handle.ProcessId}, {handle.Flow}]";
    }
}

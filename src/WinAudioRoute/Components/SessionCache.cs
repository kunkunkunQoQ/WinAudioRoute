namespace WinAudioRoute.Internal;

/// <summary>
/// 一次会话枚举的结果：公开快照 + 其背后的 COM 句柄（供音量操作使用）。
/// </summary>
internal sealed class AudioSessionSnapshot
{
    /// <summary>创建快照。</summary>
    /// <param name="sessions">公开会话快照（与会话粒度一一对应）。</param>
    /// <param name="handles">与快照同序的 COM 句柄。</param>
    /// <param name="capturedAt">枚举完成时刻。</param>
    public AudioSessionSnapshot(
        IReadOnlyList<AudioSession> sessions,
        IReadOnlyList<AudioSessionHandle> handles,
        DateTimeOffset capturedAt)
    {
        Sessions = sessions;
        Handles = handles;
        CapturedAt = capturedAt;
    }

    /// <summary>公开会话快照，顺序与 <see cref="Handles"/> 一致。</summary>
    public IReadOnlyList<AudioSession> Sessions { get; }

    /// <summary>COM 句柄，顺序与 <see cref="Sessions"/> 一致。</summary>
    public IReadOnlyList<AudioSessionHandle> Handles { get; }

    /// <summary>枚举完成时刻。</summary>
    public DateTimeOffset CapturedAt { get; }
}

/// <summary>
/// 会话快照缓存。
/// <para>
/// <b>与 SonicRoute 的关键差异</b>：SonicRoute 把
/// <c>ISimpleAudioVolume</c> 存进<b>进程级静态</b> <c>Dictionary&lt;int, List&lt;...&gt;&gt;</c>，
/// 没有释放入口、没有所有者。本类是<b>实例级</b>、<b>可释放</b>、
/// 并且<b>唯一</b>持有 <see cref="AudioSessionHandle"/> 的地方：
/// 缓存替换与 <see cref="Dispose"/> 都会释放全部句柄。
/// </para>
/// </summary>
internal sealed class SessionCache : IDisposable
{
    private readonly object _gate = new();
    private AudioSessionSnapshot? _snapshot;
    private bool _disposed;

    /// <summary>缓存是否已释放。</summary>
    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    /// <summary>当前缓存中的会话数量；无缓存时为 0。</summary>
    public int CachedSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _snapshot?.Sessions.Count ?? 0;
            }
        }
    }

    /// <summary>
    /// 当快照的存在时间不超过 <paramref name="maxAge"/> 时返回它，否则返回
    /// <see langword="null"/>（调用方应重新枚举）。
    /// </summary>
    /// <param name="maxAge">可接受的最大快照年龄。</param>
    /// <param name="now">当前时刻（由注入的时钟提供）。</param>
    /// <returns>可用快照或 <see langword="null"/>。</returns>
    public AudioSessionSnapshot? TryGet(TimeSpan maxAge, DateTimeOffset now)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_snapshot is null)
            {
                return null;
            }

            return now - _snapshot.CapturedAt <= maxAge ? _snapshot : null;
        }
    }

    /// <summary>
    /// 用新快照替换当前快照，并释放被替换掉的旧句柄。
    /// </summary>
    /// <param name="snapshot">新快照。</param>
    /// <param name="disposeReplaced">
    /// 是否释放旧句柄。调用方若仍持有旧快照的引用，可传 <see langword="false"/>。
    /// </param>
    public void Set(AudioSessionSnapshot snapshot, bool disposeReplaced = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        AudioSessionSnapshot? replaced;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            replaced = _snapshot;
            _snapshot = snapshot;
        }

        if (disposeReplaced && replaced is not null && !ReferenceEquals(replaced, snapshot))
        {
            DisposeHandles(replaced);
        }
    }

    /// <summary>丢弃缓存并释放其中的句柄。</summary>
    public void Clear()
    {
        AudioSessionSnapshot? current;
        lock (_gate)
        {
            current = _snapshot;
            _snapshot = null;
        }

        if (current is not null)
        {
            DisposeHandles(current);
        }
    }

    /// <summary>释放缓存中的全部会话句柄。幂等。</summary>
    public void Dispose()
    {
        AudioSessionSnapshot? current;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            current = _snapshot;
            _snapshot = null;
        }

        if (current is not null)
        {
            DisposeHandles(current);
        }
    }

    private static void DisposeHandles(AudioSessionSnapshot snapshot)
    {
        foreach (AudioSessionHandle handle in snapshot.Handles)
        {
            handle.Dispose();
        }
    }
}

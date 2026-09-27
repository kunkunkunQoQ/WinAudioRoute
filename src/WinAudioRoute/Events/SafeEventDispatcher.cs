using System.Runtime.InteropServices;

namespace WinAudioRoute.Events;

/// <summary>
/// 托管事件的安全分发。
/// <para>
/// <b>为什么必须存在</b>：设备/会话事件来自<b>原生 COM 回调线程</b>。
/// 如果事件订阅者抛出的异常穿过 COM 边界，会变成不可捕获的原生异常并可能终止进程。
/// 因此本类在分发时逐个隔离订阅者的异常，并汇总到 <see cref="HandlerFaulted"/>，
/// 由宿主自行决定是否记录。
/// </para>
/// <para>
/// <b>线程模型</b>：事件在触发它的线程（通常是 COM/线程池线程）上同步分发。
/// 本库不引入 UI 线程假设（无 WPF Dispatcher / WinForms / SynchronizationContext 依赖）。
/// </para>
/// </summary>
/// <typeparam name="TEventArgs">事件参数类型。</typeparam>
internal sealed class SafeEventDispatcher<TEventArgs>
    where TEventArgs : EventArgs
{
    private readonly object _gate = new();
    private readonly string _eventName;

    /// <summary>创建分发器。</summary>
    /// <param name="eventName">事件名（用于故障报告）。</param>
    public SafeEventDispatcher(string eventName)
    {
        _eventName = eventName ?? throw new ArgumentNullException(nameof(eventName));
    }

    /// <summary>
    /// 订阅者的异常被捕获时触发。参数为（订阅者、异常）。
    /// <para>该事件自身的订阅者异常会被忽略，避免递归失败。</para>
    /// </summary>
    public event Action<string, Exception>? HandlerFaulted;

    /// <summary>
    /// 原生注册失败时触发，参数为原始 HRESULT。
    /// <para>该事件自身的订阅者异常会被忽略。</para>
    /// </summary>
    public event Action<int>? RegistrationFailed;

    /// <summary>上报一次原生注册失败（异常绝不外传）。</summary>
    /// <param name="hresult">原始 HRESULT。</param>
    public void RaiseRegistrationFailure(int hresult)
    {
        try
        {
            RegistrationFailed?.Invoke(hresult);
        }
        catch
        {
            // 上报失败不得抛出
        }
    }

    /// <summary>当前订阅者数量（仅用于诊断与测试）。</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _handler?.GetInvocationList().Length ?? 0;
            }
        }
    }

    /// <summary>
    /// 事件分发的公开入口（等价于原生回调路径；供管理器与内部诊断复用）。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="args">事件参数。</param>
    internal void Dispatch(object? sender, TEventArgs args) => Raise(sender, args);

    private EventHandler<TEventArgs>? _handler;

    /// <summary>添加订阅者。</summary>
    /// <param name="handler">订阅者；为 <see langword="null"/> 时忽略。</param>
    public void Add(EventHandler<TEventArgs>? handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (_gate)
        {
            _handler += handler;
        }
    }

    /// <summary>移除订阅者。</summary>
    /// <param name="handler">订阅者；为 <see langword="null"/> 时忽略。</param>
    public void Remove(EventHandler<TEventArgs>? handler)
    {
        if (handler is null)
        {
            return;
        }

        lock (_gate)
        {
            _handler -= handler;
        }
    }

    /// <summary>
    /// 分发事件。订阅者异常一律被吞掉（不穿越 COM 边界），并上报到 <see cref="HandlerFaulted"/>。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="args">事件参数。</param>
    public void Raise(object? sender, TEventArgs args)
    {
        EventHandler<TEventArgs>? handler;
        lock (_gate)
        {
            handler = _handler;
        }

        if (handler is null)
        {
            return;
        }

        // 逐个调用：单个订阅者失败不得影响其余订阅者，也不得向外传播
        foreach (Delegate target in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<TEventArgs>)target)(sender, args);
            }
            catch (Exception ex)
            {
                ReportFaulted(target, ex);
            }
        }
    }

    private void ReportFaulted(Delegate target, Exception ex)
    {
        try
        {
            HandlerFaulted?.Invoke($"{_eventName}:{target.Method.Name}", ex);
        }
        catch
        {
            // 故障上报本身失败时不得再抛（否则会从 COM 回调中逃逸）
        }
    }
}

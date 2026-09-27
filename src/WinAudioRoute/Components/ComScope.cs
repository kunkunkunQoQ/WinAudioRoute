using System.Runtime.InteropServices;

namespace WinAudioRoute.Internal;

/// <summary>
/// COM 对象引用的作用域持有者：保证 <see cref="Marshal.ReleaseComObject"/> 恰好执行一次。
/// <para>
/// <b>为什么需要它</b>：SonicRoute 的会话枚举里有"所有权转移"写法
/// （<c>owned = true</c> 后加入容器，未加入则释放）。一旦在置位与入容器之间抛异常，
/// 该 RCW 就永久泄漏。本类型把"释放责任"和"容器代码"分离：
/// 对象先交给作用域，作用域负责在 <see cref="Dispose"/> 时释放；
/// 只有显式调用 <see cref="Detach"/> 才移交所有权，且移交动作本身不会抛异常。
/// </para>
/// <para>
/// 本类型仅在内部使用，不作为 SDK 公开表面的一部分。
/// </para>
/// </summary>
internal struct ComScope : IDisposable
{
    private object? _owned;

    /// <summary>接管一个 RCW 的释放责任。</summary>
    /// <param name="comObject">要接管的 COM 对象引用。</param>
    /// <returns>已接管该对象的释放作用域。</returns>
    public static ComScope Own(object? comObject) => new() { _owned = comObject };

    /// <summary>释放当前持有的 RCW（若仍持有）。幂等。</summary>
    public void Dispose()
    {
        object? owned = _owned;
        _owned = null;

        if (owned is not null)
        {
            try
            {
                Marshal.ReleaseComObject(owned);
            }
            catch
            {
                // 释放失败不得掩盖业务异常；RCW 最终仍会被 GC 终结器回收
            }
        }
    }

    /// <summary>
    /// 移交所有权：调用方从此负责释放，本作用域不再释放。
    /// </summary>
    /// <returns>被移交的 COM 对象引用。</returns>
    public object? Detach()
    {
        object? owned = _owned;
        _owned = null;
        return owned;
    }

    /// <summary>当前是否仍持有 RCW。</summary>
    public readonly bool IsOwned => _owned is not null;
}

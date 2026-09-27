namespace WinAudioRoute.Tests;

/// <summary>
/// <see cref="WindowsAudioManager"/> 生命周期骨架测试。
/// Phase 1 只有构造 / 释放 / 平台校验，因此这里只验证这三件事。
/// <b>不会触碰任何用户音频状态</b>：不枚举设备、不读写音量、不改路由。
/// </summary>
public class WindowsAudioManagerTests
{
    [Fact]
    public void Constructor_Succeeds_OnSupportedPlatform()
    {
        using var manager = new WindowsAudioManager();

        Assert.False(manager.IsDisposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var manager = new WindowsAudioManager();

        manager.Dispose();
        Assert.True(manager.IsDisposed);

        // 重复释放不得抛异常
        manager.Dispose();
        manager.Dispose();
        Assert.True(manager.IsDisposed);
    }

    [Fact]
    public void Using_Scope_Disposes()
    {
        WindowsAudioManager captured;

        using (var manager = new WindowsAudioManager())
        {
            captured = manager;
            Assert.False(captured.IsDisposed);
        }

        Assert.True(captured.IsDisposed);
    }

    [Fact]
    public void MultipleInstances_AreIndependent()
    {
        using var first = new WindowsAudioManager();
        using var second = new WindowsAudioManager();

        second.Dispose();

        Assert.True(second.IsDisposed);
        Assert.False(first.IsDisposed);
    }

    [Fact]
    public void Constructor_PerformsInteropLayoutCheck()
    {
        // 构造路径会执行 NativeMethods.AssertInteropLayout()；
        // 在 64 位进程上必须通过（布局不符时构造即失败）
        Assert.True(Environment.Is64BitProcess);
        using var manager = new WindowsAudioManager();
        Assert.False(manager.IsDisposed);
    }
}

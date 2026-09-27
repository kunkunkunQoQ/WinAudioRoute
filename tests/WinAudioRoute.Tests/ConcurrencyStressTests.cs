using WinAudioRoute.Events;

namespace WinAudioRoute.Tests;

/// <summary>
/// 并发压力测试。
/// <para>
/// 目标不是证明"完全无竞态"，而是发现明显问题：
/// <list type="bullet">
///   <item><description>集合被并发修改（<c>InvalidOperationException</c>）</description></item>
///   <item><description>双重释放（<c>InvalidComObjectException</c> / <c>AccessViolation</c>）</description></item>
///   <item><description>死锁（测试超时）</description></item>
///   <item><description>释放后仍收到托管事件（回调竞态）</description></item>
/// </list>
/// 允许出现 <see cref="ObjectDisposedException"/>——释放与操作竞争时这是<b>正确</b>行为。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class ConcurrencyStressTests
{
    private static bool IsExpectedDisposeRace(Exception ex) =>
        ex is ObjectDisposedException or AudioSessionNotFoundException;

    [Fact]
    public void GetSessions_Concurrent_DoesNotCorruptState()
    {
        using var audio = new WindowsAudioManager();
        var failures = new List<Exception>();

        Parallel.For(0, 12, workerIndex =>
        {
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    IReadOnlyList<AudioSession> sessions = audio.GetSessions();
                    Assert.NotNull(sessions);
                }

                _ = workerIndex;
            }
            catch (Exception ex) when (IsExpectedDisposeRace(ex))
            {
                // 允许
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        });

        Assert.Empty(failures);
    }

    [Fact]
    public void GetDevices_Concurrent_DoesNotCorruptState()
    {
        using var audio = new WindowsAudioManager();
        var failures = new List<Exception>();

        Parallel.For(0, 12, i =>
        {
            try
            {
                for (int n = 0; n < 5; n++)
                {
                    IReadOnlyList<AudioDevice> devices = i % 2 == 0
                        ? audio.GetPlaybackDevices()
                        : audio.GetRecordingDevices();
                    _ = devices.Count;
                }
            }
            catch (Exception ex) when (IsExpectedDisposeRace(ex))
            {
                // 允许
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        });

        Assert.Empty(failures);
    }

    [Fact]
    public void GetSessions_MixedWithEventInvalidation_DoesNotThrow()
    {
        using var audio = new WindowsAudioManager();
        var failures = new List<Exception>();

        Parallel.For(0, 10, i =>
        {
            try
            {
                for (int n = 0; n < 10; n++)
                {
                    _ = audio.GetSessions();

                    // 与事件回调并发：会话缓存会在另一个线程上被失效/重建
                    audio.RaiseSessionChangedForTesting(
                        new AudioSessionChangedEventArgs(
                            AudioSessionChangeKind.StateChanged, "sid", $"inst-{i}-{n}", 100 + i));
                }
            }
            catch (Exception ex) when (IsExpectedDisposeRace(ex))
            {
                // 允许
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        });

        Assert.Empty(failures);
    }

    [Fact]
    public void GetDevices_MixedWithDeviceEventInvalidation_DoesNotThrow()
    {
        using var audio = new WindowsAudioManager();
        var failures = new List<Exception>();

        Parallel.For(0, 10, i =>
        {
            try
            {
                for (int n = 0; n < 10; n++)
                {
                    _ = audio.GetPlaybackDevices();
                    audio.RaiseDeviceChangedForTesting(
                        new AudioDeviceChangedEventArgs(
                            AudioDeviceChangeKind.StateChanged, $"dev-{i}-{n}", state: AudioDeviceState.Unplugged));
                }
            }
            catch (Exception ex) when (IsExpectedDisposeRace(ex))
            {
                // 允许
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        });

        Assert.Empty(failures);
    }

    [Fact]
    public async Task OperationsRacingWithDispose_DoNotCrash()
    {
        var failures = new List<Exception>();
        int disposeRaceObserved = 0;

        for (int round = 0; round < 20; round++)
        {
            var audio = new WindowsAudioManager();
            using var start = new ManualResetEventSlim(false);

            Task worker = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    for (int i = 0; i < 20; i++)
                    {
                        _ = audio.GetSessions();
                        _ = audio.GetPlaybackDevices();
                    }
                }
                catch (Exception ex) when (IsExpectedDisposeRace(ex))
                {
                    Interlocked.Exchange(ref disposeRaceObserved, 1);
                }
                catch (Exception ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            });

            Task disposer = Task.Run(() =>
            {
                start.Wait();
                audio.Dispose();
            });

            start.Set();
            await Task.WhenAll(worker, disposer);
        }

        // 释放竞态可能发生也可能不发生；重点是不得出现"非预期异常"
        Assert.Empty(failures);
        _ = disposeRaceObserved;
    }

    [Fact]
    public void Dispose_Concurrent_IsIdempotent()
    {
        var audio = new WindowsAudioManager();
        var failures = new List<Exception>();

        Parallel.For(0, 16, _ =>
        {
            try
            {
                audio.Dispose();
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        });

        Assert.Empty(failures);
        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public void EventSubscribeUnsubscribe_Concurrent_IsSafe()
    {
        using var audio = new WindowsAudioManager();
        var failures = new List<Exception>();

        Parallel.For(0, 16, i =>
        {
            try
            {
                EventHandler<AudioDeviceChangedEventArgs> handler =
                    (_, _) => { };

                for (int n = 0; n < 20; n++)
                {
                    audio.DeviceChanged += handler;
                    audio.DeviceChanged -= handler;
                }

                _ = i;
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        });

        Assert.Empty(failures);
    }

    [Fact]
    public void SubscriberThrows_Concurrently_DoesNotEscape()
    {
        using var audio = new WindowsAudioManager();
        var failures = new List<Exception>();
        var faultCount = 0;

        audio.NotificationHandlerFaulted += (_, _) => Interlocked.Increment(ref faultCount);
        audio.DeviceChanged += (_, _) => throw new InvalidOperationException("subscriber failure");

        Parallel.For(0, 8, i =>
        {
            try
            {
                for (int n = 0; n < 20; n++)
                {
                    audio.RaiseDeviceChangedForTesting(
                        new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.Added, $"dev-{i}-{n}"));
                }
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        });

        Assert.Empty(failures);
        Assert.True(faultCount > 0, "订阅者异常应被上报到 NotificationHandlerFaulted");
    }

    [Fact]
    public void RoutingReads_MixedWithSessionAndDeviceEvents_DoNotThrow()
    {
        using var audio = new WindowsAudioManager();

        if (!audio.IsPerAppRoutingSupported)
        {
            TestEnvironment.Skip("RoutingReads_Concurrent: 本机不支持按应用路由");
            return;
        }

        var failures = new List<Exception>();

        Parallel.For(0, 6, i =>
        {
            try
            {
                for (int n = 0; n < 5; n++)
                {
                    _ = audio.GetApplicationOutput(Environment.ProcessId);
                    audio.RaiseSessionChangedForTesting(
                        new AudioSessionChangedEventArgs(
                            AudioSessionChangeKind.Created, "sid", $"inst-{i}-{n}", 200 + i));
                    audio.RaiseDeviceChangedForTesting(
                        new AudioDeviceChangedEventArgs(AudioDeviceChangeKind.PropertyChanged, "dev"));
                }
            }
            catch (Exception ex) when (IsExpectedDisposeRace(ex))
            {
                // 允许
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        });

        Assert.Empty(failures);
    }
}

// Milestone B.1：测试程序集禁用并行化。
//
// 原因：系统默认设备写入需要"进程内没有任何已注册的 IMMNotificationClient"这一前提。
// 库内部已做进程级串行化（WindowsAudioManager.DefaultDeviceWriteGate），
// 但测试宿主并行创建/释放多个 manager 仍可能在该窗口外触发已知的原生崩溃。
// 因此测试层显式串行执行；生产代码不依赖此设置。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

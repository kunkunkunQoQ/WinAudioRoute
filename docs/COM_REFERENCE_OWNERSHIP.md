# COM_REFERENCE_OWNERSHIP.md

**WinAudioRoute COM 引用所有权审计**

本文件的目的不是重构全部 COM，而是**证明每一个显式 AddRef / QueryInterface 都有对应的 Release**，并记录托管 RCW 的生命周期语义。

| 项目 | 值 |
| --- | --- |
| 审计对象 | `WinAudioRoute`（Milestone B Preflight） |
| 审计范围 | `IAudioSessionControl2`、`ISimpleAudioVolume`、`IMMDevice`、`IMMDeviceEnumerator`、`IMMDeviceCollection`、`IPropertyStore`、`PROPVARIANT`、`IAudioSessionManager2`、`IAudioSessionEnumerator`、`IAudioEndpointVolume`、`AudioPolicyConfig` 工厂指针 |
| 审计方法 | 全库检索 `Marshal.QueryInterface` / `Marshal.Release` / `Marshal.ReleaseComObject` / `Marshal.GetIUnknownForObject` / `GetObjectForIUnknown` / `ComScope.Detach`，逐调用点核对所有权 |
| 结论 | **无泄漏**。库内 `GetIUnknownForObject` / `GetObjectForIUnknown` 实际调用数为 **0**（Case C 不存在）；RCW 一律通过 `Marshal.ReleaseComObject` 释放且每个恰好一次；唯一的裸 `IntPtr` 路径（`AudioPolicyConfig` 工厂）使用 `Marshal.QueryInterface` **1 处** + `Marshal.Release` **2 处**，已逐一配对（见 §4） |

---

## 1. 结论先行：ISimpleAudioVolume 是如何取得的（Case A）

Milestone A 报告称"ISimpleAudioVolume 取自同一 RCW（QueryInterface），不新增独立所有权"。**重新核对实际代码后确认该结论成立**，但要更精确地区分"谁做了 QueryInterface"。

实际代码只有一个取得点：

```csharp
// Components/AudioSessionHandle.cs:88
_volume = _session as ISimpleAudioVolume;
```

其中 `_session` 是 `IAudioSessionControl2` 的 RCW 引用（由 `IAudioSessionEnumerator.GetSession` 返回，`Components/SessionService.cs:270`）。

**属于 Case A**：这是**托管 RCW 的接口转换**，不是手动 `Marshal.QueryInterface`。

| Case | 是否出现在本库 |
| --- | --- |
| **A**：`(ISimpleAudioVolume)sessionControl` managed RCW cast | ✅ **是**，唯一路径 |
| **B**：`Marshal.QueryInterface(...)` 取得新 `IntPtr` + `Marshal.Release` | ❌ 否（实际调用 0 处） |
| **C**：`GetIUnknownForObject` → `QueryInterface` → `GetObjectForIUnknown` | ❌ 否（实际调用 0 处） |

因此 **Milestone A 的实现没有真实泄漏**，无需修复。但原表述"不新增独立所有权"容易被误读为"这条链上没有任何引用计数变化"，所以本文件把每一步的实际语义写清楚。

---

## 2. 托管 RCW 模型的精确语义（本库依赖的前提）

这是理解后续所有条目的基础。.NET 的 COM 互操作遵循以下规则：

| 步骤 | 实际发生的事 | 引用计数影响 |
| --- | --- | --- |
| `IMMDeviceEnumerator.GetSessionEnumerator(out IAudioSessionEnumerator e)` | 原生方法 `out` 一个接口指针 → CLR 为该 COM 身份创建（或复用）一个 **RCW**，并持有该指针 | 原生对象 **+1**（由 RCW 持有） |
| `IAudioSessionEnumerator.GetSession(i, out IAudioSessionControl2 s)` | 同上，`s` 指向**会话对象**的 RCW | 会话对象 **+1** |
| `s as ISimpleAudioVolume`（或强制转换） | CLR 在该 RCW 上调用 `QueryInterface(IID_ISimpleAudioVolume)`，把返回的接口指针缓存进**同一个 RCW** 的接口缓存 | 会话对象 **+1**（缓存接口也是被持有的引用）；**不创建第二个 RCW** |
| 对 `_session` 或 `_volume` 再取另一个接口 | 复用/扩展同一 RCW 的接口缓存 | 会话对象 **+1** |
| `Marshal.ReleaseComObject(session)` | 释放 **RCW 持有的那个主身份引用**，并把该 RCW 标记为失效 | 会话对象 **−1**（主引用）；缓存接口引用在 RCW 终结时一并释放 |

**关键含义（本库的设计依据）**：

1. **同一 COM 身份只有一个 RCW**，所以"两次 cast"不会产生两个需要分别释放的托管对象。
2. **不需要（也不应该）对 `_volume` 再调一次 `Marshal.ReleaseComObject`**：它与 `_session` 是同一个 RCW，重复释放只会把 RCW 提前作废，制造"引用计数未归零但托管侧已失能"的不一致。
3. **`Marshal.ReleaseComObject` 是"释放一个引用"，不是"释放对象"**。CLR 的 RCW 终结器会在托管引用全部消失后清理剩余引用并释放接口缓存。因此本库只保证"**每个显式获取的 RCW 恰好释放一次**"，而不是"立刻把原生对象摧毁"——后者需要 `FinalReleaseComObject`，而它会无视其他线程仍持有的引用，属于更危险的操作，本库**刻意不使用**。

**同时记录：本库不使用 `FinalReleaseComObject`**。库内 `FinalReleaseComObject` 调用数 = 0。原因是它会强制把所有引用归零，如果原生侧仍有其他持有者（同进程其他组件、音频服务的回调线程）会破坏它们的生命周期。`ReleaseComObject` 是"放掉我的那一份"，语义更安全。

---

## 3. 逐对象所有权表

图例：**持有者** = 负责释放的托管对象；**释放点** = 实际调用 `Marshal.ReleaseComObject` 的位置。

### 3.1 `IMMDeviceEnumerator`

| 项 | 内容 |
| --- | --- |
| 取得 | `new MMDeviceEnumeratorComObject()` → cast `IMMDeviceEnumerator`（`ComDeviceEnumerator.cs:46`） |
| 引用计数变化 | 激活时 **+1** |
| 持有者 | `ComDeviceEnumerator` 的 `_enumerator` 字段 |
| 释放点 | `ComDeviceEnumerator.Dispose()`（`ComDeviceEnumerator.cs:208`） |
| 是否恰好一次 | ✅ 是。`Dispose` 先把字段置 `null` 再释放，天然幂等 |
| 调用方 | `DeviceService` / `SessionService` / `DeviceVolumeService` 全部用 `using` 包裹每次创建的实例 |

### 3.2 `IMMDeviceCollection`

| 项 | 内容 |
| --- | --- |
| 取得 | `EnumAudioEndpoints(..., out IMMDeviceCollection collection)`（`ComDeviceEnumerator.cs:78`） |
| 引用计数变化 | **+1** |
| 持有者 | `ComDeviceEnumerator.GetDevices` 的局部 `ComScope` |
| 释放点 | `using ComScope collectionScope = ComScope.Own(collection);`（`ComDeviceEnumerator.cs:90`） |
| 是否恰好一次 | ✅ `ComScope.Dispose` 先 `null` 再释放，幂等 |

### 3.3 `IMMDevice`

有 4 条独立路径，每条都恰好释放一次：

| 路径 | 取得 | 持有者 | 释放点 | 恰好一次 |
| --- | --- | --- | --- | --- |
| 枚举设备 | `collection.Item(i, out IMMDevice device)`（`:100`） | `GetDevices` 循环内的 `ComScope` | `:104` `using ComScope deviceScope` | ✅ |
| 按 ID 查询 | `enumerator.GetDevice(id, out IMMDevice found)`（`:138`） | 方法局部变量 `device` | `:144` `finally` | ✅ |
| 默认端点 | `GetDefaultAudioEndpoint(..., out IMMDevice found)`（`:170`） | 方法局部变量 `device` | `:176` `finally` | ✅ |
| 会话枚举 | `enumerator.GetDevice(deviceId, out IMMDevice device)`（`ComDeviceEnumerator.cs:220`，经 `OpenDeviceObject`） | **调用方**（`SessionService.CollectSessions` 的局部 `deviceObject`） | `SessionService.cs:325` `finally` | ✅ |

**特别注意 `OpenDeviceObject` 的所有权契约**：该方法的 XML 文档已明确写出"调用方拥有其生命周期的 `IMMDevice`"。唯一调用点 `SessionService.CollectSessions` 在 `finally` 中释放，且该 `finally` 覆盖了后续所有提前 `return` 分支。

### 3.4 `IPropertyStore`

| 项 | 内容 |
| --- | --- |
| 取得 | `device.OpenPropertyStore(STGM_READ, out IPropertyStore store)`（`ComDeviceEnumerator.cs:236`） |
| 引用计数变化 | **+1** |
| 持有者 | `ReadFriendlyName` 的局部 `ComScope` |
| 释放点 | `ComDeviceEnumerator.cs:241` `using ComScope storeScope` |
| 是否恰好一次 | ✅ |

### 3.5 `PROPVARIANT`（非 RCW，原生内存）

| 项 | 内容 |
| --- | --- |
| 取得 | `store.GetValue(ref key, out PROPVARIANT value)`（`ComDeviceEnumerator.cs:244`） |
| 分配 | 原生侧为 `VT_LPWSTR` 通过 `CoTaskMemAlloc` 分配字符串 |
| 释放点 | `finally { NativeMethods.PropVariantClear(ref value); }`（`ComDeviceEnumerator.cs:257`） |
| 是否恰好一次 | ✅ `finally` 覆盖成功与失败路径 |
| 备注 | `PropVariantClear` 是**必调**项：它不仅释放字符串内存，还会把 `vt` 置为 `VT_EMPTY`，防止重复清理 |

### 3.6 `IAudioSessionManager2`

| 项 | 内容 |
| --- | --- |
| 取得 | `deviceObject.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object managerObject)`（`SessionService.cs:246`） |
| 引用计数变化 | **+1**（`Activate` 返回的是一个新接口指针） |
| 持有者 | `CollectSessions` 的局部 `ComScope managerScope` |
| 释放点 | `SessionService.cs:253` `using ComScope managerScope = ComScope.Own(managerObject);` |
| 是否恰好一次 | ✅ |

### 3.7 `IAudioSessionEnumerator`

| 项 | 内容 |
| --- | --- |
| 取得 | `manager.GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator)`（`SessionService.cs:258`） |
| 引用计数变化 | **+1** |
| 持有者 | 局部 `ComScope enumeratorScope` |
| 释放点 | `SessionService.cs:265` `using ComScope enumeratorScope` |
| 是否恰好一次 | ✅ |

### 3.8 `IAudioSessionControl2`（**最需要注意的一条**）

| 项 | 内容 |
| --- | --- |
| 取得 | `sessionEnumerator.GetSession(i, out IAudioSessionControl2 session)`（`SessionService.cs:270`） |
| 引用计数变化 | **+1**（RCW 建立） |
| 中间态持有者 | 局部 `ComScope sessionScope`（`SessionService.cs:275`） |
| **所有权移交** | `SessionService.cs:291` `_ = sessionScope.Detach();` —— 之后 `sessionScope` **不再**释放 |
| 最终持有者 | `AudioSessionHandle`（构造参数 `session`，存于只读字段 `_session`） |
| 释放点 | `AudioSessionHandle.Dispose()`（`AudioSessionHandle.cs:174`） |
| 是否恰好一次 | ✅ 见下方"移交路径分析" |

**为什么需要 `Detach()`**：SonicRoute 的旧写法用 `owned = true` 标志表示"所有权已移交容器"，一旦在"置位"与"实际入容器"之间抛异常，该 RCW 就永久泄漏。本库把这两步顺序倒过来：

```
1. sessionScope 持有会话引用（此时若抛异常，using 会释放 → 无泄漏）
2. 构造 AudioSessionHandle（成功即接管；若构造抛异常，sessionScope 仍持有 → using 释放 → 无泄漏）
3. Detach() —— 移交完成
4. try { 读取音量/构造快照 } catch { handle.Dispose(); throw; }
   （若第 4 步失败，句柄已存在 → 显式释放 → 无泄漏）
5. handles.Add(handle) —— 交给 SessionCache
```

**不存在"已置位但未入容器"的窗口**，因此 Milestone A 声称的"消除该泄漏路径"成立。

**`ISimpleAudioVolume` 是否要单独释放**：**不需要**。理由见 §2：它与 `_session` 是同一个 RCW 的两个接口视图；单独再 `ReleaseComObject(_volume)` 会重复递减同一 RCW 的主引用，导致 RCW 提前失效（后续 `_session` 调用会抛 `InvalidComObjectException`）。`AudioSessionHandle.Dispose` 因此**只释放 `_session` 一次**，并把 `_volume` 字段置 `null`。

### 3.9 `IAudioEndpointVolume`

| 项 | 内容 |
| --- | --- |
| 取得 | `deviceObject.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object volumeObject)`（`DeviceVolumeService.cs:159`） |
| 引用计数变化 | **+1** |
| 持有者 | `DeviceVolumeService.VolumeContext` 的 `_volume` 字段（由 `CreateContext` 返回，调用方 `using`） |
| 释放点 | `VolumeContext.Dispose()`（`DeviceVolumeService.cs:189`） |
| 是否恰好一次 | ✅ 先 `null` 再释放，幂等 |
| 备注 | `VolumeContext.Volume` 属性与 `_volume` 指向同一 RCW；释放只做一次，属性本身不释放 |

### 3.10 `AudioPolicyConfig` 工厂指针（Milestone B 新增，Phase 5）

| 项 | 内容 |
| --- | --- |
| 取得 | `RoGetActivationFactory(hClass, ref iid, out IntPtr factoryUnk)`（→ `Marshal.QueryInterface(factoryUnk, ref iid, out _factory)`） |
| **AddRef 链** | ① `RoGetActivationFactory` 返回 `factoryUnk`：**+1**（引用计数 = 1）<br>② `Marshal.QueryInterface(factoryUnk, ref iid, out _factory)`：在 `factoryUnk` 上做 QI，**+1**（= 2）<br>③ `Marshal.Release(factoryUnk)`：**−1**（= 1，只剩 `_factory`） |
| **为什么必须用 `Marshal.Release` 而不是 `ReleaseComObject`** | `factoryUnk` 与 `_factory` 都是**裸 `IntPtr`**（不是 RCW），因为目标接口无法用 `ComImport` 正确封送。RCW 语义不适用，必须用 `Marshal.Release` 按 COM 规则递减 |
| 最终持有者 | `AudioPolicyConfigBackend` 实例的 `_factory` 字段（**进程内复用，不每次重建**） |
| 释放点 | `AudioPolicyConfigBackend.Dispose()` → `Marshal.Release(_factory)`（且先置 `IntPtr.Zero` 保证幂等） |
| 是否恰好一次 | ✅ 见 §4 的逐步核对 |
| HSTRING | 每次调用 `WindowsCreateString` **+1**，`finally { WindowsDeleteString(hstring); }` **−1**；`Get` 路径读到的 HSTRING 同样在 `finally` 删除。**成对，无泄漏** |

---

## 4. `AudioPolicyConfig` 工厂指针的逐步引用计数链（Case 特例：裸 IntPtr）

这一条是唯一**不经过 RCW** 的路径，因此单独列出完整链路。

```
初始状态：factory = null, factoryUnk = 0

① WindowsCreateString("Windows.Media.Internal.AudioPolicyConfig")
     → 分配 HSTRING hClass            [HSTRING 引用 +1]

② RoGetActivationFactory(hClass, ref iid, out factoryUnk)
     → 原生返回 IUnknown*，           [目标对象引用 +1]     factoryUnk = 1

③ Marshal.QueryInterface(factoryUnk, ref iid, out _factory)
     → 在 factoryUnk 上 QI 目标 IID，
       该 IID 已由 ② 的 iid 指定，
       返回同对象的另一接口指针        [目标对象引用 +1]     目标对象 = 2,  _factory = 1

④ Marshal.Release(factoryUnk)
     → 释放 ② 得到的那一份            [目标对象引用 −1]     目标对象 = 1,  factoryUnk = 0（不再使用）

⑤ finally { WindowsDeleteString(hClass) }
     → 释放 HSTRING                    [HSTRING 引用 −1]    hClass = 0

稳态：_factory 持有 1 个目标对象引用，无裸露的 factoryUnk，无遗留 HSTRING。

⑥ AudioPolicyConfigBackend.Dispose()
     → lock { if (_factory != 0) { Marshal.Release(_factory); _factory = 0; } }
                                       [目标对象引用 −1]     目标对象 = 0 → 可被销毁
```

**每一步 AddRef/QueryInterface 的对应 Release**：

| AddRef / QI | 位置 | 对应 Release | 位置 |
| --- | --- | --- | --- |
| `RoGetActivationFactory` 返回 `factoryUnk`（+1） | Phase 5 后端 | `Marshal.Release(factoryUnk)` | 紧随其后的 `QueryInterface` 之后 |
| `Marshal.QueryInterface` → `_factory`（+1） | Phase 5 后端 | `Marshal.Release(_factory)` | `AudioPolicyConfigBackend.Dispose()` |
| `WindowsCreateString`（+1） | Phase 5 后端 | `WindowsDeleteString(hClass)` | `finally` |
| `WindowsCreateString(fullDeviceId)`（+1） | 每次 Set 调用 | `WindowsDeleteString(hstring)` | `finally` |
| `Get...` 返回的 HSTRING（+1） | 每次 Get 调用 | `WindowsDeleteString(hstring)` | `finally` |

✅ **无遗漏、无重复**。

**线程安全**：`_factory` 的读取与释放都在同一个 `lock (_sync)` 内完成（双检锁模式），避免"检查-使用"竞态。Milestone A 审计曾把此点标为 `RK-6` 风险，Phase 5 实现时按此设计消除。

**vtable 槽位调用不产生引用**：`Marshal.GetDelegateForFunctionPointer` + 手动调用只是读取 vtable 指针，不涉及 AddRef；`GetPersistedDefaultAudioEndpoint` 返回的 HSTRING 由调用方负责删除（已在上表覆盖）。

---

## 5. `ComScope`：释放责任的单一执行点

`Components/ComScope.cs` 是本库控制引用释放的核心机制，语义如下：

```csharp
// 接管释放责任
using ComScope scope = ComScope.Own(comObject);

// 移交所有权（之后 scope 不再释放）
object? transferred = scope.Detach();
```

| 特性 | 保证 |
| --- | --- |
| 恰好释放一次 | `Dispose()` 先把 `_owned` 置 `null` 再释放；重复 `Dispose` 无操作 |
| 释放异常不外传 | `try { Marshal.ReleaseComObject(owned); } catch { }`，避免在 `Dispose`/终结器路径上掩盖业务异常 |
| `Detach` 不抛异常 | 只做字段读取 + 置 `null`，因此"移交"这一步本身不可能失败（这是消除 SonicRoute 泄漏窗口的关键） |
| 不释放 null | `if (owned is not null)` |

**全库显式释放调用点清单（共 7 处 `Marshal.ReleaseComObject` + Phase 5 新增 1 处 `Marshal.Release`）**：

| # | 位置 | 释放对象 |
| --- | --- | --- |
| 1 | `ComScope.cs:37` | 任意由 `ComScope` 接管的 COM 引用（设备集合、设备、属性存储、会话管理器、会话枚举器、会话） |
| 2 | `ComDeviceEnumerator.cs:144` | `IMMDevice`（按 ID 查询路径） |
| 3 | `ComDeviceEnumerator.cs:176` | `IMMDevice`（默认端点路径） |
| 4 | `ComDeviceEnumerator.cs:208` | `IMMDeviceEnumerator` |
| 5 | `AudioSessionHandle.cs:174` | `IAudioSessionControl2`（唯一所有者） |
| 6 | `DeviceVolumeService.cs:189` | `IAudioEndpointVolume` |
| 7 | `SessionService.cs:325` | `IMMDevice`（会话枚举路径） |
| 8 | Phase 5 `AudioPolicyConfigBackend.Dispose()` | `AudioPolicyConfig` 工厂（`Marshal.Release`，裸 IntPtr） |

---

## 6. 审计结论

| 检查项 | 结果 |
| --- | --- |
| `Marshal.QueryInterface` 实际调用数 | **1**（仅 `AudioPolicyConfig` 工厂）；对应 `Marshal.Release(_factory)` 已配对 |
| `Marshal.Release(IntPtr)` 实际调用数 | **2**（`Release(factoryUnk)` 立即配对；`Release(_factory)` 在 `Dispose`） |
| `IPolicyConfig` 路径是否也需引用审计 | 需要，但当前**默认禁用**（写入会崩溃）；见 §8 |
| `Marshal.GetIUnknownForObject` / `GetObjectForIUnknown` | **0 处**（Case C 不存在） |
| `Marshal.FinalReleaseComObject` | **0 处**（刻意不用，见 §2） |
| 需要释放的 RCW 是否都有唯一所有者 | ✅ 是 |
| 是否存在"同一 RCW 被释放两次" | ✅ 否（`ISimpleAudioVolume` 明确不单独释放，见 §3.8） |
| 是否存在"取得后无释放路径" | ✅ 否（`ComScope` + `finally` 全覆盖） |
| 是否存在"移交窗口泄漏" | ✅ 否（`Detach` 不抛异常 + 构造失败时 `handle.Dispose()`） |
| `Manager.Dispose` 是否能释放全部长期资源 | ✅ 是（唯一长期持有者是 `SessionCache` 的会话句柄集合 + Phase 5 的工厂指针） |
| **是否存在真实泄漏需要修复** | **无**。Milestone A 的实现未发现泄漏；Phase 5 的实现按上表设计 |

### 6.1 对 Milestone A 原表述的修正记录

| 原表述 | 精确表述 |
| --- | --- |
| "`ISimpleAudioVolume` 取自同一 RCW（QueryInterface），不新增独立所有权；`Dispose` 只释放会话引用一次。" | 前半句正确但需说明"QueryInterface 由 CLR 在 RCW 上执行，且有引用计数影响（+1 记在同一 RCW 的接口缓存上）"；后半句**正确且必须如此**——单独释放 `_volume` 会导致同一 RCW 被重复递减。 |
| 未提及 RCW 终结器与接口缓存的清理时机 | 已补：`ReleaseComObject` 只放掉 RCW 的主引用，接口缓存引用在 RCW 终结时释放；库**不追求**"立刻摧毁原生对象"。 |

---

## 7. Milestone B 新增 COM 回调的所有权（Phase 7 补充）

事件系统引入的引用关系与上述 RCW 路径不同：**回调对象是"托管对象交给原生存放"**，方向相反。规则如下：

| 对象 | 谁创建 | 谁持有 | 谁负责注销 |
| --- | --- | --- | --- |
| `IMMNotificationClient` 实现 | `WindowsAudioManager` | `WindowsAudioManager` 强引用字段 | `UnregisterEndpointNotificationCallback` |
| `IAudioSessionNotification` 实现 | `WindowsAudioManager` → `SessionService` | `SessionService._notificationClient` | `UnregisterSessionNotification` |
| `IAudioSessionEvents` 实现（每个会话一个） | `AudioSessionHandle.RegisterEvents` | `AudioSessionHandle._eventClient`（**复用句柄已独占的会话 RCW，不新增引用**） | `UnregisterAudioSessionNotification` + `MarkDisposed` |
| `IAudioEndpointVolumeCallback` | 本里程碑不实现 | — | — |

**关键约束**：原生侧只持有**裸接口指针**，不持有托管引用。因此托管侧必须有强引用直达注销完成，否则 GC 回收回调对象 → 原生侧悬空指针 → 进程崩溃。这一条在 Milestone B 的实现与测试中显式覆盖（`DeviceNotificationClientTests` / `SessionEventClientTests` / `ConcurrencyStressTests`）。

---


## 8. `IPolicyConfig` 工厂与默认设备写入（**Milestone B.1 修正版**）

> **本节已由 Milestone B.1 重写。** Milestone B 曾在此记录"调用 `SetDefaultEndpoint` 导致进程崩溃"，
> 该结论**已撤回**——根因是探针的 vtable 编号错误（见下）以及 WinAudioRoute 自身的事件系统冲突。
> 逐方法编号对照见 [IPOLICYCONFIG_CROSSCHECK.md](IPOLICYCONFIG_CROSSCHECK.md)。

### 8.1 引用所有权（当前启用）

| 项 | 内容 |
| --- | --- |
| 取得 | `new PolicyConfigClientComObject()`（`[ComImport]` CLSID `870af99c-171d-4f9e-af0d-e63df40c2bc9`）→ cast `IPolicyConfig` |
| IID | `F8679F50-850A-41CF-9C72-430F290290C8`（与 SoundSwitch / EarTrumpet 一致） |
| 引用计数 | RCW 建立时原生对象 **+1** |
| 调用 | `SetDefaultEndpoint(deviceId, ERole)` —— **绝对 vtable 槽位 13**（接口内第 11 个方法） |
| 持有者 | `PolicyConfigBackend.SetDefaultEndpoint` 内的局部变量（每次调用新建，调用后无托管引用） |
| 释放点 | 依赖 RCW 终结器（与 SonicRoute 一致）；方法返回后无托管引用 |
| 是否恰好一次 | ✅ 无显式 `ReleaseComObject`，因此也不存在双重释放 |

### 8.2 编号修正：Milestone B 的错误

`IPolicyConfig` 是 `InterfaceIsIUnknown`，绝对 vtable 前 3 槽被 `IUnknown` 占用：

| 编号体系 | `SetDefaultEndpoint` |
| --- | --- |
| 绝对 COM vtable 槽位 | **13** |
| 1-based 接口方法序号 | 11 |
| 0-based 接口方法索引 | 10 |

Milestone B 的裸探针把"接口方法序号 11"当成"绝对槽位 11"调用，
**实际调用到 `GetPropertyValue`**（把 `LPCWSTR` 当 `PROPERTYKEY*`），
因此 `AccessViolationException` 是**预期行为**，不能证明 `SetDefaultEndpoint` 有问题。

同样，当时"绝对槽位 10 返回 `S_OK`"实际调用的是 `SetShareMode`，与默认设备无关，也不能作为槽位证据。

### 8.3 B.1 实测：真正的崩溃条件是"已注册 `IMMNotificationClient`"

| 实验（隔离进程） | 结果 |
| --- | --- |
| 裸 `ComImport` + 硬编码设备 ID，**不使用任何 WinAudioRoute 类型** | ✅ `hr=0x00000000`，释放、GC 后干净退出（exit=0） |
| 同上 + **保持存活的 `WindowsAudioManager`**（注册了 `IMMNotificationClient`） | ❌ 崩溃 |
| 同上 + **禁用设备通知注册** | ✅ `hr=0x00000000`，干净退出 |
| 禁用全部 5 个回调方法体（仅保留 CCW 注册） | ❌ 仍崩溃 |
| 开启回调文件日志 | ❌ **日志为空** → 崩溃在托管回调**之前** |
| **SonicRoute 的 `SystemDefaultDeviceService.SetDefault`** | ✅ 无效 ID → `0x80070490`；有效 ID → `S_OK`；存活到 GC 后（exit=0） |

**结论**：`SetDefaultEndpoint`（绝对槽位 13）本身正常。
崩溃条件是"进程内已注册 `IMMNotificationClient` + 执行默认设备变更"，且崩溃发生在**原生侧**
（托管回调从未被进入）。这是 WinAudioRoute 事件系统引入的冲突，与 SonicRoute 无关。

### 8.4 修复（已实现）

| 措施 | 实现 |
| --- | --- |
| 写入窗口内临时注销设备通知 | `SuspendDeviceNotifications()` → 写入 → `ResumeDeviceNotifications()` |
| **进程级串行化** | `private static readonly object DefaultDeviceWriteGate` + `WithDefaultDeviceWriteGate<T>(...)`。必须是静态的：多实例并行时一个实例注销而另一个仍持注册会复现崩溃 |
| 写入默认启用 | 构造函数执行只读探测 `PrepareDefaultDeviceWrites()` |
| 能力不可用时明确失败 | 抛 `AudioRoutingNotSupportedException`，不触碰原生路径 |
| 测试层串行化 | `[assembly: CollectionBehavior(DisableTestParallelization = true)]` |
| 只读探测与写入分离 | `PolicyConfigBackend.Probe()` 只做激活 + 接口转换 |

**验证**：库路径写入 → `IsSuccess=True`、回读 MATCH、通知已恢复、GC 后干净退出；
门控 mutation **13/13 通过**，执行前后状态比对**完整恢复**。

### 8.5 对 SonicRoute 的影响

**无。** SonicRoute 的 `SystemDefaultDeviceService.SetDefault` 经隔离实测**完全正常**：
无效设备 ID 返回 `0x80070490`（`ERROR_NOT_FOUND`），有效 ID 返回 `S_OK`，两者都存活到 GC 之后。

SonicRoute 侧真正需要修复的是另外两个与崩溃无关的问题：

| 位置 | 问题 | 正确写法 |
| --- | --- | --- |
| `PolicyConfigClient.cs` 的 `SetDefaultEndpoint` | 第二参数声明为 `EDataFlow` | `ERole`（见 [IPOLICYCONFIG_CROSSCHECK.md](IPOLICYCONFIG_CROSSCHECK.md)） |
| `ComInterop.cs` 的 `DeviceState.ALL` | 值写成 `0xFFFFFFFF` | `DEVICE_STATEMASK_ALL = 0x0000000F`（见 `mmdeviceapi.h:153`） |

另需注意 `ComInterop.cs` 给 `ERole` 加了 `[Flags]`，而 `ERole` 是**互斥**的普通枚举，不是位标志。

> **本节结论的适用范围**：以上判断基于 **Windows 11 Build 26100 x64** 的隔离实测。
> 其他 Windows 版本上的行为属于 [TESTED_ENVIRONMENTS.md](TESTED_ENVIRONMENTS.md) 中的待补矩阵。

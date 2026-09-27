# IPOLICYCONFIG_CROSSCHECK.md

**`IPolicyConfig` 接口交叉核对（Milestone B.1）**

目的：修正 Milestone B 中关于 `SetDefaultEndpoint` 的 vtable 判断，并建立一份编号明确、可复现的参照表。

**本文件不复制任何第三方代码**，只做只读对照（IID / CLSID / 方法数量 / 方法顺序 / `SetDefaultEndpoint` 参数 / `ERole` 封送）。

---

## 1. 编号约定（本文件与后续所有报告必须遵守）

`IPolicyConfig` 声明为 `[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]`，
因此**绝对 COM vtable 的前三个槽位被 `IUnknown` 占用**。

| 编号体系 | 含义 | `SetDefaultEndpoint` 的值 |
| --- | --- | --- |
| **绝对 COM vtable 槽位**（absolute slot） | 从 `QueryInterface` 数起的槽位号，含 IUnknown 3 个 | **13** |
| **1-based 接口方法序号**（interface ordinal） | 只数 `IPolicyConfig` 自己的方法，从 1 开始 | **11** |
| **0-based 接口方法索引**（interface index） | 只数自己的方法，从 0 开始 | **10** |

换算关系：

```
absolute slot = 3 + 0-based interface index
absolute slot = 2 + 1-based interface ordinal
```

**Milestone B 的错误**：裸探针把"接口方法序号 11"直接当成了"绝对槽位 11"来调用，
实际调用到的是 `GetPropertyValue`（absolute slot 11），参数类型完全不符（把 `LPCWSTR` 当成 `PROPERTYKEY*`），
因此产生 `AccessViolationException` 是**预期行为**，不能用来证明 `SetDefaultEndpoint` 本身有问题。

> 本文件此后一律显式标注编号类型，禁止只写"slot 11"这类模糊表述。

---

## 2. 三份声明对照

| 项 | SonicRoute | SoundSwitch | EarTrumpet |
| --- | --- | --- | --- |
| 文件 | `SonicRoute.Core/Interop/PolicyConfigClient.cs` | `SoundSwitch.Audio.Manager/Interop/Interface/Policy/IPolicyConfig.cs` | `EarTrumpet/Interop/MMDeviceAPI/IPolicyConfig.cs` |
| **CLSID** | `870af99c-171d-4f9e-af0d-e63df40c2bc9`（`PolicyConfigClient`） | 由 `ComGuid.POLICY_CONFIG_CLSID` 常量引用（本文件未展开该常量文件） | 未在本文件声明（EarTrumpet 在别处用 `PolicyConfigClient`） |
| **IID** | `f8679f50-850a-41cf-9c72-430f290290c8` | `ComGuid.POLICY_CONFIG_IID`（常量；社区标准值即 `F8679F50-850A-41CF-9C72-430F290290C8`） | **`F8679F50-850A-41CF-9C72-430F290290C8`**（显式写出） |
| **方法数量** | **12** | **12** | **12** |
| **方法顺序** | 与 SoundSwitch 完全一致（`GetMixFormat` → `SetEndpointVisibility`） | `GetMixFormat` → `SetEndpointVisibility` | 8×`Unused` + `GetPropertyValue` + `SetPropertyValue` + `SetDefaultEndpoint` + `SetEndpointVisibility` |
| **`SetDefaultEndpoint` 位置** | 第 11 个（index 10） | 第 11 个（index 10） | 第 11 个（index 10） |
| **`SetDefaultEndpoint` 参数** | `(string wszDeviceId, EDataFlow dataFlow)` ❌ | `(string pszDeviceName, [MarshalAs(U4)] ERole role)` ✅ | `(string wszDeviceId, ERole eRole)` ✅ |
| **`ERole` 封送** | 无 `ERole`（误用 `EDataFlow`） | `[MarshalAs(UnmanagedType.U4)]` 显式 4 字节无符号 | 默认（C# 枚举底层为 `int`，同为 4 字节） |
| 前置方法的参数风格 | `(IntPtr, IntPtr)` 占位 | **从设备名开始**的完整语义签名 | `void Unused1..8` 占位 |

**三家一致的关键结论**：

1. **IID 一致**：`F8679F50-850A-41CF-9C72-430F290290C8`。
2. **方法数量一致**：12。
3. **`SetDefaultEndpoint` 的接口内位置一致**：第 11 个方法（0-based index **10**）→ **绝对槽位 13**。
4. **参数形态一致**：`(LPWSTR 设备 ID, ERole 角色)`。
5. SonicRoute 的**唯一实质性错误**是把第二参数类型写成 `EDataFlow`（应为 `ERole`），
   **方法数量与顺序本身是正确的**。

---

## 3. 逐方法顺序表（含两种编号）

| absolute slot | 0-based index | 1-based ordinal | SonicRoute 声明 | SoundSwitch 声明 | EarTrumpet 声明 |
| --- | --- | --- | --- | --- | --- |
| 0 | — | — | `IUnknown::QueryInterface` | 同 | 同 |
| 1 | — | — | `IUnknown::AddRef` | 同 | 同 |
| 2 | — | — | `IUnknown::Release` | 同 | 同 |
| 3 | 0 | 1 | `GetMixFormat(IntPtr, IntPtr)` | `GetMixFormat(string, IntPtr)` | `Unused1()` |
| 4 | 1 | 2 | `GetDeviceFormat(IntPtr, IntPtr, IntPtr)` | `GetDeviceFormat(string, bool, IntPtr)` | `Unused2()` |
| 5 | 2 | 3 | `ResetDeviceFormat(IntPtr, IntPtr)` | `ResetDeviceFormat(string)` | `Unused3()` |
| 6 | 3 | 4 | `SetDeviceFormat(IntPtr, IntPtr, IntPtr, IntPtr)` | `SetDeviceFormat(string, IntPtr, IntPtr)` | `Unused4()` |
| 7 | 4 | 5 | `GetProcessingPeriod(IntPtr, IntPtr, IntPtr, IntPtr)` | `GetProcessingPeriod(string, bool, IntPtr, IntPtr)` | `Unused5()` |
| 8 | 5 | 6 | `SetProcessingPeriod(IntPtr, IntPtr, IntPtr)` | `SetProcessingPeriod(string, IntPtr)` | `Unused6()` |
| 9 | 6 | 7 | `GetShareMode(IntPtr, IntPtr, IntPtr)` | `GetShareMode(string, IntPtr)` | `Unused7()` |
| 10 | 7 | 8 | `SetShareMode(IntPtr, IntPtr, IntPtr)` | `SetShareMode(string, IntPtr)` | `Unused8()` |
| **11** | **8** | **9** | `GetPropertyValue(IntPtr, IntPtr, IntPtr, IntPtr)` | `GetPropertyValue(string, bool, IntPtr, IntPtr)` | `GetPropertyValue(string, ref PROPERTYKEY, ref PropVariant)` |
| 12 | 9 | 10 | `SetPropertyValue(IntPtr, IntPtr, IntPtr, IntPtr)` | `SetPropertyValue(string, bool, IntPtr, IntPtr)` | `SetPropertyValue(string, ref PROPERTYKEY, ref PropVariant)` |
| **13** | **10** | **11** | **`SetDefaultEndpoint(string, EDataFlow)`** ❌ | **`SetDefaultEndpoint(string, ERole)`** ✅ | **`SetDefaultEndpoint(string, ERole)`** ✅ |
| 14 | 11 | 12 | `SetEndpointVisibility(IntPtr, IntPtr, int)` | `SetEndpointVisibility(string, bool)` | `SetEndpointVisibility(string, short)` |

**Milestone B 探针实际调用到的槽位**：

| 探针当时写的编号 | 真实 absolute slot | 真实方法 | 后果 |
| --- | --- | --- | --- |
| "slot 11" | 11 | `GetPropertyValue` | 传 `LPCWSTR` 当 `PROPERTYKEY*` → **AV（预期）** |
| "slot 10" | 10 | `SetShareMode` | 返回 `S_OK`（与默认设备无关，**不能**证明任何事） |

---

## 4. WinAudioRoute `PolicyConfigBackend.cs` 的核对结果

| 检查项 | 结果 |
| --- | --- |
| CLSID | ✅ `870af99c-171d-4f9e-af0d-e63df40c2bc9`（与 SonicRoute 一致） |
| IID | ✅ `f8679f50-850a-41cf-9c72-430f290290c8`（与三家一致） |
| 方法数量 | ✅ 12 |
| 方法顺序 | ✅ 与 SoundSwitch 完全一致 |
| `SetDefaultEndpoint` 第二参数 | ✅ 已修正为 `ERole`（vtable 布局不变） |
| `ERole` 封送 | ⚠️ 声明为 `ERole`（`int` 底层，4 字节）——与 EarTrumpet 一致；SoundSwitch 显式标 `[MarshalAs(UnmanagedType.U4)]`（同为 4 字节无符号，ABI 等价） |
| 前置方法参数 | ⚠️ **沿用 SonicRoute 的 `IntPtr` 占位签名**（未调用；数量与顺序正确，因此 vtable 布局正确） |

**结论：`PolicyConfigBackend.cs` 的 vtable 布局是正确的**，Milestone B 的崩溃结论不是布局问题。

**关于占位签名的处理决定（依据你的 §6）**：

- 这些方法**当前不被调用**，方法数量一致即保证槽位布局正确，因此不构成立即风险。
- 但按你的要求，**不自行猜签名**。SoundSwitch 提供了完整语义签名（可从本文件 §3 对照），
  EarTrumpet 只提供 `Unused` 占位——两家对**前置方法**的签名并不"交叉一致"（一家具名、一家占位），
  因此**本轮不替换为猜測定稿**，而是记录为待办：等 B.1 的崩溃问题解决、且需要真正调用这些方法时，
  以 SoundSwitch 的语义签名为准并逐方法实测验证。

---

## 5. 参照来源（只读，未复制代码）

| 项目 | 路径 | 核对了什么 |
| --- | --- | --- |
| [SoundSwitch](https://github.com/Belphemur/SoundSwitch) | `SoundSwitch.Audio.Manager/Interop/Interface/Policy/IPolicyConfig.cs` | 12 个具名方法、参数顺序、`[MarshalAs(U4)] ERole` |
| [EarTrumpet](https://github.com/File-New-Project/EarTrumpet) | `EarTrumpet/Interop/MMDeviceAPI/IPolicyConfig.cs` | 8×`Unused` + 4 个具名方法、IID 显式值、`SetDefaultEndpoint(string, ERole)` |

两者的方法数量、`SetDefaultEndpoint` 位置与参数形态**一致**，可互相印证。

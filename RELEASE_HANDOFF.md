# RELEASE_HANDOFF.md

**WinAudioRoute 0.1.0 — 上传交接单（本地冻结状态）**

| 项目 | 值 |
| --- | --- |
| 冻结时间 | 2026-09-27（+08:00） |
| 冻结执行方 | 开发 AI（本地开发与验证负责人） |
| 冻结范围 | **仅本地**。本 AI 不处理 GitHub 身份验证，不创建远程仓库，不 push |
| 下一步执行方 | 上传 AI（已持有 GitHub 凭证） |

> **本文件是开发 AI 与上传 AI 之间的契约。** 上传前请完整阅读
> [上传 AI 禁止操作](#上传-ai-禁止操作) 与 [CI 失败时的处理边界](#ci-失败时的处理边界)。

---

## Repository

```text
Name:           WinAudioRoute
Owner:          kunkunkunQoQ
Visibility:     Public
Default branch: main
Version:        0.1.0
License:        MIT
Target:         https://github.com/kunkunkunQoQ/WinAudioRoute
```

`.NET` 目标框架：`net8.0-windows10.0.19041.0`
支持架构：`win-x64`（已验证运行）、`win-arm64`（**仅交叉编译验证，运行期未验证**）

---

## Local Git

冻结时的状态（`origin` 已配置，尚未 push）。

> **关于 commit SHA 的自指问题**：本文件自身的提交会改变 `HEAD`，因此这里**不写死** SHA。
> 上传 AI 请以 `git rev-parse HEAD` 的**实际输出**为准，并核对
> `git log -1 --format=%s` 是 `Prepare GitHub upload handoff`。
> 产物与其绑定 commit 的说明见 [Artifact ↔ commit 绑定](#artifact--commit-绑定)。

```powershell
> git rev-parse HEAD
（以实际输出为准；应为 `Prepare GitHub upload handoff` 这个提交）

> git status --short
（无输出）

> git log -5 --oneline
<HEAD>   Prepare GitHub upload handoff
<HEAD~1> Make the upload report commit-agnostic for SourceLink
<HEAD~2> Update upload report with final commit and HEAD
<HEAD~3> Add GitHub upload report
<HEAD~4> Enable SourceLink and verify it from the PDB

> git remote -v
origin  https://github.com/kunkunkunQoQ/WinAudioRoute.git (fetch)
origin  https://github.com/kunkunkunQoQ/WinAudioRoute.git (push)

> git rev-parse --abbrev-ref HEAD
main
```

| 项 | 值 |
| --- | --- |
| HEAD | `git rev-parse HEAD` 的实际输出（上传前请记录） |
| 分支 | `main` |
| Commit 数 | **6** |
| 受版本控制文件数 | **93** |
| 工作树 | **clean** |
| 远程仓库是否已存在 | **否**（`GET https://api.github.com/repos/kunkunkunQoQ/WinAudioRoute` → HTTP 404） |

**本地不存在任何未提交的改动，也不存在被忽略但需要的文件。**
`bin/`、`obj/`、`artifacts/` 均由 `.gitignore` 排除，不应也无法进入提交。

### Artifact ↔ commit 绑定

SourceLink 把 PDB 绑死到**构建时的** commit（这是 SourceLink 的定义，不是缺陷）。
因此存在一个必然差异：

| 项 | 值 |
| --- | --- |
| `artifacts/` 中产物的绑定 commit | `3781cea5dfd64e429243eabf47bd2d639b769d0b`（`3781cea`） |
| 当前的 HEAD | 可能因**文档提交**而更新（本文件即为一例） |
| 对二进制的影响 | **无** —— Markdown 文档不参与编译，二进制逐位相同 |

**这一点对上传没有影响**：push 不产生新 commit，产物绑定的 SHA 仍是仓库历史中真实存在的提交。

**若需要"零差异"的交付**（产物绑定 == 最终 HEAD），在**最后一次提交之后**执行：

```powershell
dotnet build WinAudioRoute.sln -c Release
Remove-Item .\artifacts -Recurse -Force -ErrorAction SilentlyContinue
dotnet pack src\WinAudioRoute\WinAudioRoute.csproj -c Release --no-build -o .\artifacts
pwsh ./eng/verify-sourcelink.ps1     # 期望 PASS，且 template 中的 SHA == git rev-parse HEAD
```

`eng/verify-sourcelink.ps1` 的期望 commit 由 `git rev-parse HEAD` 在运行期取得，
因此它**总会**校验"产物是否绑定当前 HEAD"——SHA 不需要（也不应该）写在文档里。

---

## Verification

冻结前完整重跑，全部通过。以下是**实际执行**的结果。

| # | 项目 | 命令 | 结果 |
| --- | --- | --- | --- |
| 1 | **Build** | `dotnet clean` → `dotnet restore` → `dotnet build WinAudioRoute.sln -c Release` | **PASS** — 0 warnings / 0 errors |
| 2 | **Unit** | `dotnet test -c Release --filter "Category!=Integration&Category!=Mutation&Category!=Hardware&Category!=RealAudio"` | **PASS** — 293 passed / 0 failed / 0 skipped |
| 3 | **Integration** | `dotnet test -c Release --filter "Category=Integration"` | **PASS** — 55 passed / 0 failed |
| 4 | **Mutation** | `$env:RUN_AUDIO_MUTATION_TESTS='1'; dotnet test -c Release --filter "Category=Mutation"` | **PASS** — 13 passed / 0 failed |
| 5 | **Real hardware gate** | `scripts/test-real-audio.ps1` | **PASS** — exit code 0 |
| 6 | **State restoration** | 同上（脚本内前后快照比对） | **OK** — 默认设备与 per-app 路由与快照完全一致 |
| 7 | **SourceLink** | `eng/verify-sourcelink.ps1` | **PASS** — 映射指向本仓库且绑定运行时 HEAD |
| 8 | **Package** | `eng/verify-package.ps1 -PackageDirectory ./artifacts` | **PASS** |
| 8b | **Artifact freeze** | `eng/verify-artifacts.ps1` | **PASS** — 4/4 产物与冻结指纹一致 |
| 9 | **Consumer** | 全新项目仅引用本地 `.nupkg` | **PASS** — build PASS / run PASS，exit 0 |
| 10 | **x64** | `dotnet build -r win-x64` | **PASS** — PE `0x8664` |
| 11 | **ARM64 cross-build** | `dotnet build -r win-arm64` | **PASS** — PE `0xAA64`（交叉编译） |

### 测试计数汇总

```text
unit         passed=293  failed=0  skipped=0  total=293  env-bypassed=0
integration  passed=55   failed=0  skipped=0  total=55   env-bypassed=0
mutation     passed=13   failed=0  skipped=0  total=13   env-bypassed=0
                                                          ─────────────
                                                          361 全部真正执行
```

**`env-bypassed = 0` 是关键数字。** xUnit 2.5.3 没有 `Assert.Skip`，无法运行的测试会提前返回
**但仍被报告为 passed**。绕过计数表示"没有执行断言的测试数量"，此处为零 ——
即 361 个测试全部真正执行了断言，不存在"因为没有设备而静默跳过"的情况。

### 真实硬件环境

```text
OS                  Windows 11 (Pro)
Build               26100 (24H2)
Architecture        x64
.NET SDK            8.0.424
Playback devices    9 active
Recording devices   6 active
Audio sessions      20
Per-app routing     supported
Default device write  enabled
Device notification   registered
```

恢复核对：mutation 前后对 **6 项默认设备**（Render/Capture × Console/Multimedia/Communications）
与**每个有会话 PID 的持久化路由**抓取快照并比对，结果一致。

### SourceLink 实测

```text
documents            = 37
sourcelink.present   = yes
template             = https://raw.githubusercontent.com/kunkunkunQoQ/WinAudioRoute/<artifact-commit>/*
repositoryUrl        = https://github.com/kunkunkunQoQ/WinAudioRoute
informationalVersion = 0.1.0+<artifact-commit>
```

`<artifact-commit>` 由 `eng/verify-sourcelink.ps1` 在运行期与 `git rev-parse HEAD` 比对后确认一致，
因此不在此写死。校验脚本的断言包含：

| 断言 | 内容 |
| --- | --- |
| 1 | PDB 中存在 SourceLink custom debug information |
| 2 | 映射的 `<owner>/<repo>` 等于 `kunkunkunQoQ/WinAudioRoute` |
| 3 | 映射的 commit 等于运行时的 `git rev-parse HEAD` |
| 4 | 映射模板非空且指向 `raw.githubusercontent.com` |
| 5 | 程序集 `AssemblyMetadata("RepositoryUrl")` 等于仓库 URL |
| 6 | `AssemblyInformationalVersion` 形如 `0.1.0+<commit>` 且嵌入该 commit |

### 本地产物（`artifacts/`，不进版本控制）

| 文件 | 大小 | SHA256 |
| --- | --- | --- |
| `WinAudioRoute.0.1.0.nupkg` | 94,839 B | `239A38E7997C4A7AF6E59B02D75881397994DD856C6E9B5DE299E7F6362E7249` |
| `WinAudioRoute.0.1.0.snupkg` | 23,263 B | `855E76EABAA0C014F3F0925CACD43A493A8DEE86350C6153A9695B09D9EA9927` |
| `cli/win-x64/winaudio.exe` | 152,064 B | `67A25A4BC4B1F95C2FF6C20BFEC3124D1DFC6E6B7A7DBAABC3DE1753AD68E3C5` |
| `cli/win-arm64/winaudio.exe` | 133,120 B | `71AD7C93FEE1854B1C95417AA369047225B69CCF1448EC58D4CD592AABD1F074` |

> **NuGet 包的哈希会随 commit 变化。** 包的 PDB 内嵌 SourceLink 映射与
> `AssemblyInformationalVersion`，两者都含 commit SHA，因此**每次重新 pack 都会得到不同哈希**。
> 上表对应"产物构建时的 HEAD"。上传 AI 若拿到不同哈希，应先比对
> `pwsh ./eng/verify-sourcelink.ps1` 输出的 commit 是否等于当前 `git rev-parse HEAD`，
> 而不是把哈希差异直接当作产物损坏。
>
> 两个 `cli/**/winaudio.exe` 的哈希在同一次发布准备中保持稳定，可直接用于一致性核对。
>
> 一键核对（把上表指纹与本地产物比对）：
>
> ```powershell
> pwsh ./eng/verify-artifacts.ps1
> # 期望： Artifact freeze verification: PASS
> # commit 漂移导致的 .nupkg/.snupkg 差异可用 -AllowPackageDrift 降级为警告
> ```

> `artifacts/` **不在版本控制中**（`.gitignore` 已排除）。上述哈希是冻结时本地产物的指纹，
> 用于确认上传 AI 拿到的产物与开发 AI 验证过的产物一致。
> 产物绑定的 commit 见 [Artifact ↔ commit 绑定](#artifact--commit-绑定)。

### 明确的未验证项

以下**不**声称 PASS：

| 项 | 状态 |
| --- | --- |
| GitHub Actions 实际执行 | **未验证** — 需先创建仓库并 push（上传 AI 负责） |
| GitHub 上的文件/master 分支实际状态 | **未验证** — 同上 |
| ARM64 **运行期**行为 | **未验证** — 无 ARM64 硬件；仅证明可交叉编译且 PE 架构正确 |
| Windows 10 19041 / Windows 11 22000 / 22621 / 22631 | **未验证** — 见 `docs/TESTED_ENVIRONMENTS.md` |
| 真实设备插拔事件 / 真实会话创建销毁事件 | **未验证** — 仅验证注册与映射逻辑 |
| workflow YAML 的实际可执行性 | 仅本机结构自检（无 `actionlint`） |

**ARM64 必须始终表述为**：

```text
ARM64 build verified.
Runtime not yet hardware-verified.
```

---

## GitHub target

```text
https://github.com/kunkunkunQoQ/WinAudioRoute
```

远程仓库当前**不存在**。`origin` 已在本地配置为上述 URL。

---

## 上传 AI 允许操作

以下操作**授权执行**：

1. 创建远程 repository `kunkunkunQoQ/WinAudioRoute`（**Public**）
2. 添加 / 验证 `origin` 远端指向
3. `git push -u origin main`（推送 `main` 分支，HEAD = `037dc12a38f2dadabbffa281eb5159fb589da50c`）
4. 设置 repository description：
   ```text
   Modern Windows audio control for .NET — devices, sessions, per-app routing, events and CLI.
   ```
5. 设置 topics：
   ```text
   windows, audio, dotnet, csharp, wasapi, coreaudio, mmdevice,
   audio-session, audio-routing, per-app-routing, windows-audio, cli
   ```
6. 检查 GitHub Actions 首次运行，等待至最终状态（**不要异步留在后台**）
7. **仅修复属于 GitHub Actions / repository integration 的问题**，例如：
   - workflow 的 YAML 语法或事件触发配置
   - runner 镜像 / SDK 版本 / restore 源
   - 仅与 CI 环境相关、且**不触及库语义**的构建配置
   - 上传产物路径、artifact 名称、步骤顺序

**创建仓库时不要生成 README / License / .gitignore 模板** —— 本地已存在真实文件，
生成模板会导致首次 push 冲突或多余的模板提交。

---

## 上传 AI 禁止操作

**不得擅自修改**：

```text
src/
tests/
samples/
public API
Interop ABI
routing implementation
event implementation
version number
NuGet package metadata
```

具体含义（对照本仓库）：

| 禁止修改 | 对应路径 / 内容 |
| --- | --- |
| `src/` | `src/WinAudioRoute/**`、`src/WinAudioRoute.Cli/**` |
| `tests/` | `tests/WinAudioRoute.Tests/**`（含分类 Trait、跳过逻辑、断言） |
| `samples/` | `samples/**` |
| public API | `WindowsAudioManager` 及其公开类型/成员签名 |
| Interop ABI | `src/WinAudioRoute/Interop/**`（vtable 顺序、`PROPVARIANT` 布局、IID、槽位常量） |
| routing implementation | `src/WinAudioRoute/Routing/**`、`Components/PerAppRoutingService.cs` |
| event implementation | `src/WinAudioRoute/Events/**`、`Components/DeviceNotificationClient.cs`、`Components/SessionNotificationClient.cs` |
| version number | `0.1.0`（`WinAudioRoute.csproj` 的 `<Version>`、CLI 的 `<Version>`） |
| NuGet package metadata | `PackageId`、`Description`、`Tags`、`License`、`RepositoryUrl`、`IncludeSymbols`、README 打包项 |

**同时禁止**：

- 删除或弱化任何测试
- 弱化断言以换取绿色
- 让 `Category=Integration` 或 `Category=Mutation` 在 GitHub-hosted runner 上运行（该 runner 无音频端点，
  会产生假绿灯）
- 让 Mutation 测试自动运行（必须保持 `RUN_AUDIO_MUTATION_TESTS=1` 显式门控 + 手动触发）
- 使用 `continue-on-error: true` 隐藏失败
- 修改 `scripts/test-real-audio.ps1` 的判定标准
- 修改 `.gitignore` 使 `bin`/`obj`/`artifacts` 中的内容进入提交
- `dotnet nuget push`
- `gh release create`
- 创建或推送 `v0.1.0` tag

---

## CI 失败时的处理边界

CI 失败时，**先判断失败原因属于哪一类**。

### A 类 — 属于 GitHub Actions / repository integration

**上传 AI 自行修复**，例如：

- YAML 语法错误、缩进、表达式求值
- `actions/*` 版本或 runner 镜像问题
- `dotnet-version` / 还原源 / 缓存配置
- 步骤顺序、artifact 上传路径、TRX 路径
- 工作流文件路径与实际仓库结构不一致
- 权限 / token 作用域

修复后：

```powershell
git add .
git commit -m "Fix initial CI"
git push
# 重新检查运行结果，直到 green
```

### B 类 — 属于代码或测试逻辑

**必须停止，不得自行修复。** 例如：

- 编译错误（`CS####`）
- 单元/集成/变异测试失败
- XML 文档缺失（`CS1591`）
- 包内容或元数据校验失败（`eng/verify-package.ps1`）
- SourceLink 校验失败（`eng/verify-sourcelink.ps1`）
- 架构断言失败（PE machine 与 RID 不符）
- 任何需要触碰上述 [禁止修改](#上传-ai-禁止操作) 路径才能解决的问题

处理方式：

```powershell
gh run view <RUN_ID> --repo kunkunkunQoQ/WinAudioRoute --log-failed
```

把**完整失败日志**交回开发 AI，并在交接记录中注明：

```text
FAILURE CLASS: B (code or test logic)
RUN ID: <id>
STEP: <failing step name>
LOG: <pasted log>
ACTION: none taken — returned to development AI
```

> **不要"顺手修"。** 本 AI 的验证结论（293 + 55 + 13、SourceLink、包校验）是针对
> commit `037dc12a` 的状态；上传 AI 若改动源码，会使这些结论失效，且我无法验证改动后的状态。

### C 类 — 环境缺失（既非 A 也非 B）

例如 hosted runner 缺少某个组件、网络不可达。**先报告**，由开发 AI 判断是调整
workflow（A 类）还是调整验证策略。不要为了让 CI 变绿而降级验证强度。

---

## 当前禁止发布

```text
NuGet.org:      DO NOT PUBLISH
GitHub Release: DO NOT CREATE
v0.1.0 tag:     DO NOT CREATE
```

本次目标**仅限**：

```text
GitHub repository live
CI green
SourceLink verified
0.1.0 artifacts ready
```

具体禁止：

```powershell
# 禁止
dotnet nuget push ...
gh release create ...
git tag v0.1.0
git push origin v0.1.0
```

发布需要另行授权。0.1.0 的发布门禁清单见 `docs/releases/0.1.0-DRAFT.md`。

---

## 上传 AI 的执行清单

按顺序执行，每步确认后再进入下一步。

```powershell
# 0. 前置检查
cd <repo>                                  # WinAudioRoute 仓库根
git rev-parse HEAD                         # 记录实际 SHA，并核对 git log -1 --format=%s
git status --short                         # 期望无输出
git remote -v                              # 期望 origin -> https://github.com/kunkunkunQoQ/WinAudioRoute.git

# 1. 确认远程仓库不存在（不要覆盖已有仓库）
gh repo view kunkunkunQoQ/WinAudioRoute

# 2. 创建仓库（如尚不存在）
gh repo create kunkunkunQoQ/WinAudioRoute `
  --public `
  --source . `
  --remote origin `
  --description "Modern Windows audio control for .NET — devices, sessions, per-app routing, events and CLI."

# 3. 设置 topics
gh repo edit kunkunkunQoQ/WinAudioRoute --add-topic windows,audio,dotnet,csharp,wasapi,coreaudio,mmdevice,audio-session,audio-routing,per-app-routing,windows-audio,cli

# 4. 推送
git push -u origin main

# 5. 推送后确认
git status                                 # 期望 working tree clean
gh repo view kunkunkunQoQ/WinAudioRoute     # 确认默认分支 = main

# 6. 验证远端文件存在
gh api repos/kunkunkunQoQ/WinAudioRoute/contents/README.md            --jq .name
gh api repos/kunkunkunQoQ/WinAudioRoute/contents/LICENSE              --jq .name
gh api repos/kunkunkunQoQ/WinAudioRoute/contents/.github/workflows/ci.yml --jq .name
gh api repos/kunkunkunQoQ/WinAudioRoute/contents/scripts/test-real-audio.ps1 --jq .name
gh api repos/kunkunkunQoQ/WinAudioRoute/contents/src --jq '.[].name'
gh api repos/kunkunkunQoQ/WinAudioRoute/contents/tests --jq '.[].name'
gh api repos/kunkunkunQoQ/WinAudioRoute/contents/samples --jq '.[].name'
gh api repos/kunkunkunQoQ/WinAudioRoute/contents/docs --jq '.[].name'

# 7. 观察第一次 CI（等待至最终状态，不要后台挂着）
gh run list --repo kunkunkunQoQ/WinAudioRoute
gh run watch <RUN_ID> --repo kunkunkunQoQ/WinAudioRoute

# 8. 若失败：先分类（A/B/C），按上面边界处理

# 9. 回填结果
#    在 docs/releases/GITHUB_UPLOAD_REPORT.md 中填写 Run ID、状态、
#    若发生 A 类修复则记录 commit SHA
```

### 完成后请回报

```text
Repository URL:
Run ID:
CI status:
Default branch:
Files verified:
Failure class (if any):
New commits (if any):
```

---

## 交接边界小结

| 事项 | 负责方 |
| --- | --- |
| 源码、测试、API、Interop ABI、版本号、包元数据 | **开发 AI**（冻结，不再改动） |
| 本地验证结论（361 测试 / SourceLink / 包 / 消费者 / 架构） | **开发 AI** |
| 创建远程仓库、push、metadata、topics | **上传 AI** |
| GitHub Actions 运行与 A 类修复 | **上传 AI** |
| B 类失败（代码/测试逻辑） | **交回开发 AI**，上传 AI 不得自行修改 |
| NuGet.org 发布、GitHub Release、`v0.1.0` tag | **两者都禁止**（需另行授权） |

冻结状态：**本地可交付，等待上传。**

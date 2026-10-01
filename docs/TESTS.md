# AWD 后端（P0 核心）· win10_vm 编译测试清单

> **历史存档（P0 阶段，2026-09-29）**：本清单已执行完毕；文中 `src\` 目录在 0.1 Alpha 已拉平到仓库根目录。
> 写码：ZCode（win11 主机，2026-09-29），**未编译未运行**——分工是主机只写码，虚拟机编译测试。
> 代码在 VM 的 `D:\tools\AnywhereDoor`（= 主机 `Z:\tools\AnywhereDoor`，同一共享目录）。

## 前置

1. `dotnet --list-sdks` —— 有 8.x 即可（9/10 也能编 net8.0 目标）；没有就 `winget install Microsoft.DotNet.SDK.8`
2. `cd D:\tools\AnywhereDoor`

下文 `dotnet run ...` 是 `dotnet run --project src\Awd.CLI -c Release` 的缩写。

## 步骤（按序；任何一步失败：把错误**原文**贴到任务板，停在那里等结论，别自行改代码）

| # | 命令 | 预期 |
|---|---|---|
| 0 | `dotnet build src\Awd.CLI -c Release` | 0 error（warning 可忽略） |
| 1 | `dotnet run ... -- paths` | 打印 favorites.json 与 iconcache 两个路径 |
| 2 | `dotnet run ... -- apps` | 行数 > 0，lnk 与 uwp 两类都有；`--find 计算器` 能命中 |
| 3 | `dotnet run ... -- icon <第2步拿到的计算器AUMID>` | 真实尺寸 + 缓存 PNG 路径；PNG 能打开看 |
| 4 | `dotnet run ... -- icon "<任选一个开始菜单 .lnk 的完整路径>"` | 同上 |
| 5 | `dotnet run ... -- icons --limit 300` | **关键产出**：≥256 / 64-255 / <64 / 失败 四档统计 + 逐项列表 |
| 6 | `dotnet run ... -- launch <计算器AUMID>` | 计算器弹出（UWP 通道，应打印 PID） |
| 7 | `dotnet run ... -- launch "<某 .lnk 路径>"` | 对应程序弹出（shell 通道，PID 可能拿不到） |
| 8 | fav 全链路：`add`（UWP 与 .lnk 各一）→ `list` → `move` → `remove` | 输出与 favorites.json 内容一致 |

## 回填（做完在任务板回帖，并按需记笔记）

- 编译是否一次通过；没过的话错误原文
- `apps` 两类的数量
- `icons` 四档统计（这份分档数据**直接决定图标视觉方案**：小图标配圆角方块还是混排）
- UWP launch 与 .lnk launch 的结果（PID 是否拿到）
- 异常 / 诡异输出原文，不要转述

## 已知风险点（写码时无法验证，重点盯）

1. **IShellLinkW 槽位声明**（`src\Awd.Core\Interop\ShellInterop.cs`）：
   GetPath→SetRelativePath 共 16 个方法必须严格按原生 vtable 顺序，错一个 lnk 解析就会崩或返回空。
   症状：`apps` 里 lnk 项为 0、名字/target 异常、或进程崩溃。
2. **UWP 图标**：先带 ICONONLY 提取，失败会自动退化重试一次；若 uwp 项大面积"失败"，
   用 `apps --kind uwp` 挑几个 AUMID 逐个 `icon` 试，把输出发回。
3. **AppsFolder 枚举**：个别项取不到名字会被跳过（内部 catch，不算 bug）；
   总数比「设置-应用」略多是正常的（含 PWA / 系统应用）。
4. **STRRET 坑已避开**：所有显示名走 IShellItem.GetDisplayName，没有手解 STRRET 结构——
   若 VM 上出现内存异常/崩溃，先怀疑别的，但请在报告里注明这一点供回溯。

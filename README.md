# 任意门（Anywhere Door）

**唤起即跳** —— 一个常驻托盘的快速跳转工具：按全局热键，到达任意
**本地目录 / 远程目录 / 网站 / 程序**。四类目标各占一页，每页独立收藏。

> 当前状态：**P0 后端已写码**（Awd.Core + Awd.CLI，未编译；待虚拟机按 [docs/TESTS.md](docs/TESTS.md) 验证）。设计与阶段划分见 [docs/PLAN.md](docs/PLAN.md)。

## 与易远传（ERF）的关系

远程目录那一页**调用 ERF**，不重复实现 SFTP/FTP —— 职责划分是
「ERF 管远程文件系统，任意门管快速到达」。所以用远程页需要装 ERF。

## 目录结构

```
AnywhereDoor/
  docs/PLAN.md            设计与阶段划分
  docs/TESTS.md           虚拟机编译测试清单
  src/Awd.Core/           后端核心（枚举/启动/图标/收藏，将来 GUI 直接复用）
    Interop/ShellInterop.cs   手写 shell COM 互操作（零 NuGet 依赖）
  src/Awd.CLI/            命令行驱动（awd.exe）
```

## 命令行用法（后端验证）

```
awd paths                     收藏与图标缓存路径
awd apps [--find 关键词] [--kind lnk|exe|uwp]
awd icon  <目标|AUMID|@id> [--size 256]
awd icons [--find X] [--limit N]              批量探测图标真实尺寸
awd launch <目标|AUMID|@id>
awd fav    add|list|move|remove
```

数据：收藏 `%APPDATA%\AnywhereDoor\favorites.json`；图标缓存 `%LOCALAPPDATA%\AnywhereDoor\iconcache\`。

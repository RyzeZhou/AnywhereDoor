using System.Diagnostics;
using System.IO;
using Awd.Core.Interop;

namespace Awd.Core;

/// <summary>
/// 启动分两条通道（立项笔记 P0 要点 2）：传统 exe / .lnk 走 Process.Start；
/// UWP 必须走 IApplicationActivationManager.ActivateApplication，
/// 否则"商店应用点了没反应"——这是同类工具最常见的翻车点。
/// </summary>
public static class AppLauncher
{
    /// <summary>启动结果：用了哪条通道、进程号（.lnk 经 shell 启动时拿不到就为 null）。</summary>
    public sealed record LaunchResult(string Method, uint? ProcessId);

    public static LaunchResult Launch(AppEntry app)
    {
        return app.Kind switch
        {
            AppKind.Uwp => LaunchUwp(app),
            AppKind.Lnk => LaunchLnk(app),
            // 文件夹/网址/erf: 这类目标统一交 shell：资源管理器、默认浏览器、协议处理器各认各的
            AppKind.Folder or AppKind.Web or AppKind.Remote => LaunchShell(app),
            _ => LaunchExe(app),
        };
    }

    private static LaunchResult LaunchShell(AppEntry app)
    {
        var psi = new ProcessStartInfo(app.Target) { UseShellExecute = true };
        using var process = Process.Start(psi);
        return new LaunchResult("Process.Start（shell：文件夹/网址/协议）", process?.Id is { } id ? (uint)id : null);
    }

    private static LaunchResult LaunchUwp(AppEntry app)
    {
        var manager = ShellInterop.CreateActivationManager();
        var hr = manager.ActivateApplication(app.Target, app.Arguments ?? "", 0 /* AO_NONE */, out var pid);
        if (hr != 0)
            throw new InvalidOperationException(
                $"UWP 启动失败（AUMID={app.Target}）：HRESULT 0x{hr:X8}。常见原因：AUMID 不存在或应用已被卸载。");
        return new LaunchResult("IApplicationActivationManager.ActivateApplication", pid);
    }

    private static LaunchResult LaunchLnk(AppEntry app)
    {
        // .lnk 交给 shell 执行：工作目录、参数、运行方式都以 lnk 自身的设置为准
        var psi = new ProcessStartInfo(app.Target) { UseShellExecute = true };
        using var process = Process.Start(psi);
        return new LaunchResult("Process.Start（shell 执行 .lnk）", process?.Id is { } id ? (uint)id : null);
    }

    private static LaunchResult LaunchExe(AppEntry app)
    {
        var psi = new ProcessStartInfo(app.Target)
        {
            Arguments = app.Arguments ?? "",
            UseShellExecute = false,
            // 工作目录不能瞎给：默认进程工作目录是继承的，程序找资源会翻车（立项笔记点名）
            WorkingDirectory = string.IsNullOrWhiteSpace(app.WorkingDir)
                ? Path.GetDirectoryName(app.Target) ?? ""
                : app.WorkingDir,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"进程未能启动：{app.Target}");
        return new LaunchResult("Process.Start（exe，显式工作目录）", (uint)process.Id);
    }
}

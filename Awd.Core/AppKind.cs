namespace Awd.Core;

/// <summary>
/// 目标的来源形态：开始菜单快捷方式、直接的 exe、UWP 商店应用。
/// UWP 的启动通道与 .lnk/exe 完全不同（见 AppLauncher），所以类型必须显式区分。
/// Folder/Web/Remote 是四页 UI 引入的目标形态，统一走 shell 执行。
/// </summary>
public enum AppKind
{
    Lnk,
    Exe,
    Uwp,
    Folder,
    Web,
    Remote,
}

using System.Diagnostics;
using System.Windows;
using Clean.Core;
using Microsoft.Win32;

namespace Clean.App;

public interface IUserDialogs
{
    string? PickFolder();
    bool ConfirmCleanup(CleanupPlan plan);
    bool ConfirmEmptyBin();
    void OpenRecycleBin();
}
public sealed record AppLocations(string TemporaryDirectory, string StateDirectory);
public sealed class UserDialogs : IUserDialogs
{
    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择要分析的本地目录", Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
    public bool ConfirmCleanup(CleanupPlan plan)
    {
        string operation = plan.Mode == CleanupMode.Permanent ? "永久删除，无法由本应用恢复" : "移入 Windows 回收站，可手动还原";
        return MessageBox.Show($"将处理预览中的 {plan.Items.Length:N0} 个文件，逻辑大小 {FormatSize.Bytes(plan.LogicalBytes)}。\n\n方式：{operation}。\n\n停止只能取消后续项目，不能回滚已完成的操作。\n是否执行此计划？",
            plan.Mode == CleanupMode.Permanent ? "单独确认永久删除" : "确认移入回收站", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
    public bool ConfirmEmptyBin() => MessageBox.Show("此操作会清空当前用户所有磁盘的回收站，不限于 Clean 处理的文件。\n\n无法由本应用恢复。是否继续进入 Windows 确认？",
        "单独确认清空回收站", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public void OpenRecycleBin() => Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
}
public static class FormatSize
{
    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        int index = 0;
        while (Math.Abs(bytes) >= 1000 && index < units.Length - 1) { bytes /= 1000; index++; }
        return $"{bytes:N1} {units[index]}";
    }
}

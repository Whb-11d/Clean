# 架构

Clean.App (WPF / CommunityToolkit.Mvvm / Microsoft.Extensions.DependencyInjection) → Clean.Core；Clean.Windows → Clean.Core。仅 App.xaml.cs 组合根构造 Windows 适配器。ViewModel 注入用例、存储、指标与对话框抽象，禁止 Win32 调用。Core 为 net10.0，不引用 WPF 或原生接口；Windows 与 App 为 net10.0-windows。

扫描和执行通过 Task.Run 离开 UI 线程；回收站 COM 使用专用 STA 线程。UI 仅批量呈现扫描结果，DataGrid 虚拟化。运行中冻结选择、偏好和预览。关闭时若有任务则先取消并等当前操作返回。

设置、历史、日志在 %LOCALAPPDATA%/Clean；设置保留 .bak，临时文件同目录原子替换；历史每项更新，最多 50 条。日志仅时间、事件、错误类型，不记录文件路径。应用普通权限，无后台服务和网络模块。

安全敏感逻辑位于 PathPolicy、ScanService、CleanupService、WindowsFileSystem 和 ShellRecycleBin；系统原生边界必须通过 Windows 集成测试。测试只对自己生成的专用目录执行删除。发布包不裁剪、不启用 AOT，win-x64 自包含。

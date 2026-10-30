using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clean.App;
using Clean.Core;
using Clean.Testing;

namespace Clean.App.Tests;

public sealed class WindowLayoutTests
{
    // Render the real XAML with isolated fakes; never touch the user's Temp or recycle bin.
    [Fact] public void MainWindowRendersNavigationSettingsAndContextualCleanupAtBothSizes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clean;component/Styles.xaml", UriKind.Relative) });
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                MainWindow? window = null;
                try
                {
                    var files = new FakeFiles(); files.Add(@"C:\Test\expired-cache.tmp", bytes: 1048576);
                    var metrics = new FakeMetrics(); var history = new FakeHistory();
                    var vm = new MainViewModel(new(files, new()), new(files, new(), metrics, history),
                        metrics, metrics, new FakeBin(), new FakeSettings(), history, new FakeLog(), new NoDialogs(), new(@"C:\Test", @"C:\State"));
                    window = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
                    window.Show();
                    var root = (FrameworkElement)window.Content;
                    await vm.RefreshOverviewCommand.ExecuteAsync(null);
                    Render(root, "home", 1120, 780);
                    var navigation = Descendants<ListBox>(root).Single(x => AutomationProperties.GetName(x) == "页面导航");
                    Assert.Equal(5, navigation.Items.Count);
                    Assert.DoesNotContain(navigation.Items.Cast<NavigationItem>(), x => x.Page == 2);
                    Assert.DoesNotContain(VisibleButtons(root), x => Equals(x.Content, "清理详情 →"));
                    AssertPrimaryButtonFits(root);
                    Render(root, "home-small", 940, 660);
                    AssertPrimaryButtonFits(root);

                    vm.SelectedPage = 5; Render(root, "settings-small", 940, 660);
                    Assert.Contains(VisibleButtons(root), x => Equals(x.Content, "保存设置"));
                    Assert.Contains(Descendants<TextBox>(root), x => x.IsVisible && x.ActualHeight >= 80);
                    foreach (var page in new[] { 1, 3, 4 })
                    {
                        vm.SelectedPage = page; Render(root, $"page-{page}-small", 940, 660);
                        Assert.Equal(page, navigation.SelectedItem is NavigationItem item ? item.Page : -1);
                    }

                    await vm.QuickCleanCommand.ExecuteAsync(null);
                    Render(root, "cleanup-finished", 1120, 780);
                    Assert.Contains(VisibleButtons(root), x => Equals(x.Content, "清理详情 →"));
                    vm.ShowReportCommand.Execute(null); Render(root, "details-small", 940, 660);
                    Assert.Contains(VisibleButtons(root), x => Equals(x.Content, "← 返回"));
                    Assert.Contains(Descendants<DataGrid>(root), x => x.IsVisible && x.ItemsSource == vm.Results);
                    Assert.Null(navigation.SelectedItem);
                    vm.CloseReportCommand.Execute(null); Assert.Equal(0, vm.SelectedPage);
                }
                catch (Exception e) { failure = e; }
                finally
                {
                    window?.Close(); app.Shutdown();
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF layout check timed out.");
        Assert.Null(failure);
    }

    private static void Render(FrameworkElement root, string name, int width, int height)
    {
        var window = Window.GetWindow(root);
        window.Width = width; window.Height = height;
        root.SetValue(Panel.BackgroundProperty, window.Background);
        // Drain bindings and template application before checking or capturing.
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        width = (int)Math.Ceiling(root.ActualWidth); height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var directory = Path.Combine(AppContext.BaseDirectory, "ui-previews"); Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static IEnumerable<Button> VisibleButtons(FrameworkElement root) => Descendants<Button>(root).Where(x => x.IsVisible);
    private static void AssertPrimaryButtonFits(FrameworkElement root)
    {
        var button = VisibleButtons(root).Single(x => AutomationProperties.GetName(x) == "一键清理过期临时文件");
        var bounds = button.TransformToAncestor(root).TransformBounds(new(0, 0, button.ActualWidth, button.ActualHeight));
        Assert.True(bounds.Left >= 0 && bounds.Right <= root.ActualWidth && bounds.Bottom <= root.ActualHeight);
        Assert.True(button.ActualWidth >= 120);
    }
    private sealed class NoDialogs : IUserDialogs
    {
        public string? PickFolder() => throw new InvalidOperationException();
        public bool ConfirmCleanup(CleanupPlan plan) => throw new InvalidOperationException();
        public bool ConfirmEmptyBin() => throw new InvalidOperationException();
        public void OpenRecycleBin() => throw new InvalidOperationException();
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace Clean.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _closeRequested;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent(); DataContext = _viewModel = viewModel;
        Loaded += async (_, _) => { await viewModel.RefreshOverviewCommand.ExecuteAsync(null); _timer.Start(); };
        _timer.Tick += async (_, _) =>
        { if (IsVisible && WindowState != WindowState.Minimized && viewModel.SelectedPage == 4) await viewModel.SampleMemoryAsync(); };
        viewModel.PropertyChanged += ViewModelChanged;
        Closing += OnClosing;
        Closed += (_, _) => { _timer.Stop(); viewModel.PropertyChanged -= ViewModelChanged; };
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.Busy) return;
        e.Cancel = true; _closeRequested = true; _viewModel.CancelCommand.Execute(null);
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    { if (_closeRequested && e.PropertyName == nameof(MainViewModel.Busy) && !_viewModel.Busy) Dispatcher.BeginInvoke(Close); }
}

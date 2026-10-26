using AdbManager.Services;
using Microsoft.UI.Xaml;

namespace AdbManager;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // До загрузки XAML: x:Uid берёт строки на выбранном языке.
        Res.Init(Settings.Load().Language);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}

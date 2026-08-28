using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.WindowsAppRuntime;

namespace RemoteDesktop.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        EnsureWindowsAppRuntime();
    }

    private static void EnsureWindowsAppRuntime()
    {
        var status = DeploymentManager.GetStatus();
        if (status.Status == DeploymentStatus.Ok)
        {
            return;
        }

        _ = DeploymentManager.Initialize();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.Activate();
    }
}

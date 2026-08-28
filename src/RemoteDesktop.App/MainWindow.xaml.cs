using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RemoteDesktop.App.Views;

namespace RemoteDesktop.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Remote Desktop LAN";
        ContentFrame.Navigate(typeof(HomePage));
    }

    public Frame NavigationFrame => ContentFrame;
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteDesktop.App.Helpers;

public static class NetworkHelper
{
    public static string GetLocalHostName() => Environment.MachineName;

    public static string GetPrimaryIPv4Address()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address.Address))
                {
                    return address.Address.ToString();
                }
            }
        }

        return "127.0.0.1";
    }
}

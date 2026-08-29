using System.Diagnostics;

namespace RemoteDesktop.App.Services;

public static class SessionLog
{
    private static readonly object Sync = new();

    public static string DirectoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteDesktopLAN",
            "logs");

    public static string TodayPath(string role) =>
        Path.Combine(DirectoryPath, $"{role}-{DateTime.Now:yyyyMMdd}.log");

    public static void Write(string role, string message)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{role}] {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(TodayPath(role), line);
            }
        }
        catch (Exception)
        {
        }
    }

    public static void OpenDirectory()
    {
        Directory.CreateDirectory(DirectoryPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = DirectoryPath,
            UseShellExecute = true,
        });
    }
}

namespace RemoteDesktop.App.Helpers;

public static class PinGenerator
{
    public static string CreatePin()
    {
        return Random.Shared.Next(0, 1_000_000).ToString("D6");
    }
}

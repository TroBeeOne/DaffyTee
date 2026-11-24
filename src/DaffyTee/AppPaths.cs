namespace DaffyTee;

/// <summary>
/// Where DaffyTee keeps its state: the encrypted user store, its key, the TLS certificate and logs.
///
/// Per user rather than per directory, like DaffyDB's certificate: %LOCALAPPDATA%\DaffyTee on Windows,
/// ~/.local/share/DaffyTee elsewhere. DAFFYTEE_HOME or --config-dir moves it, which is how a service
/// account or a test run gets a store of its own.
/// </summary>
internal static class AppPaths
{
    private static string? _home;

    public static string Home
    {
        get => _home ??= Environment.GetEnvironmentVariable("DAFFYTEE_HOME") is { Length: > 0 } fromEnvironment
            ? Path.GetFullPath(fromEnvironment)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                          Environment.SpecialFolderOption.Create),
                "DaffyTee");
        set => _home = Path.GetFullPath(value);
    }

    public static string EnsureHome()
    {
        Directory.CreateDirectory(Home);
        return Home;
    }
}

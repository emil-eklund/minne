namespace MailSearch.Config;

/// <summary>Resolves where local state lives. Everything stays under one directory.</summary>
public sealed class DataPaths
{
    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string DatabaseFile => Path.Combine(Root, "mail.db");
    public string ModelsDirectory => Path.Combine(Root, "models");
    public string TokenCacheFile => "msal_cache.bin";

    public DataPaths(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("MINNE_DATA")
               ?? Environment.GetEnvironmentVariable("MAILSEARCH_DATA")
               ?? DefaultRoot();
        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// %LOCALAPPDATA%\Minne, except when an index from before the rename already exists next to it —
    /// adopting it keeps a populated database and downloaded models usable without a re-sync.
    /// </summary>
    private static string DefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(localAppData, "Minne");
        if (Directory.Exists(current)) return current;

        var legacy = Path.Combine(localAppData, "MailSearch");
        return File.Exists(Path.Combine(legacy, "mail.db")) ? legacy : current;
    }
}

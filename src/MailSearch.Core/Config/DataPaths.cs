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
        Root = root ?? Environment.GetEnvironmentVariable("MAILSEARCH_DATA")
               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MailSearch");
        Directory.CreateDirectory(Root);
    }
}

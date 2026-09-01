namespace MailSearch;

/// <summary>
/// Where long-running Core operations (model downloads, Graph throttling waits, device-code
/// sign-in prompts) report human-readable progress. The desktop app points <see cref="Sink"/>
/// at its status bar; when unset the messages are dropped.
/// </summary>
public static class StatusLog
{
    public static Action<string>? Sink { get; set; }

    public static void Post(string message) => Sink?.Invoke(message);
}

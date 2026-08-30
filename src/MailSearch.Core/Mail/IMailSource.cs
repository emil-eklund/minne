namespace MailSearch.Mail;

/// <summary>Anything that can deliver messages incrementally (Graph today; IMAP/PST could follow).</summary>
public interface IMailSource
{
    /// <summary>
    /// Stream changes for a folder. <paramref name="state"/> is the opaque resume token from the previous run
    /// (null = full sync). Every new token is reported through <paramref name="onNewState"/> and must be persisted.
    /// </summary>
    IAsyncEnumerable<MailChange> GetChangesAsync(string folder, string? state, Action<string> onNewState, CancellationToken ct);
}

namespace MailSearch.Mail;

public sealed record MailAddress(string? Name, string Address)
{
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name) || Name == Address ? Address : $"{Name} <{Address}>";
}

/// <summary>Provider-neutral representation of one email.</summary>
public sealed class MailMessage
{
    public required string Id { get; init; }
    public string? InternetMessageId { get; init; }
    public string? ConversationId { get; init; }
    public required string Folder { get; init; }
    public string Subject { get; init; } = "";
    public MailAddress? From { get; init; }
    public IReadOnlyList<MailAddress> To { get; init; } = [];
    public IReadOnlyList<MailAddress> Cc { get; init; } = [];
    public DateTimeOffset Received { get; init; }
    public bool HasAttachments { get; init; }
    public string? WebLink { get; init; }
    /// <summary>Plain-text body as delivered by the source (not yet cleaned).</summary>
    public string Body { get; init; } = "";
}

/// <summary>One change from an incremental sync. A null message means the item was removed.</summary>
public sealed record MailChange(string Id, MailMessage? Message)
{
    public bool IsRemoved => Message is null;
}

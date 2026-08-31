namespace MailSearch;

/// <summary>
/// Can drop its expensive resources (native inference sessions, in-memory indexes) while idle
/// and reload them lazily on next use. Unlike <see cref="IDisposable"/>, the object stays usable.
/// </summary>
public interface IUnloadable
{
    void Unload();
}

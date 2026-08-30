using System.Runtime.InteropServices;
using MailSearch.Mail;
using MailSearch.Search;
using Microsoft.Data.Sqlite;

namespace MailSearch.Storage;

public sealed record MessageRow(
    long RowId, string Id, string? InternetMessageId, string? ConversationId, string Folder, string Subject,
    string? SenderName, string? SenderAddress, string Recipients, DateTimeOffset Received, bool HasAttachments,
    string? WebLink, string Body);

public sealed record FtsHit(long RowId, double Score, string Snippet);

/// <summary>All chunk vectors held in one contiguous array for fast scanning.</summary>
public sealed class EmbeddingIndex
{
    public required int Dimensions { get; init; }
    public required long[] ChunkIds { get; init; }
    public required long[] MessageRowIds { get; init; }
    public required float[] Data { get; init; }
    public int Count => ChunkIds.Length;
    public ReadOnlySpan<float> Vector(int i) => Data.AsSpan(i * Dimensions, Dimensions);
}

public sealed record StoreStats(long Messages, long Chunks, long EmbeddedChunks, string? EmbeddingModel, long DatabaseBytes);

/// <summary>Single-file SQLite store: message metadata, cleaned bodies, FTS5 index and chunk embeddings.</summary>
public sealed class SearchStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly string _path;

    public SearchStore(string path)
    {
        _path = path;
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        CreateSchema();
    }

    private void CreateSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS sync_state(folder TEXT PRIMARY KEY, state TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages(
                rowid INTEGER PRIMARY KEY,
                id TEXT NOT NULL UNIQUE,
                internet_message_id TEXT,
                conversation_id TEXT,
                folder TEXT NOT NULL,
                subject TEXT NOT NULL,
                sender_name TEXT,
                sender_address TEXT,
                recipients TEXT NOT NULL,
                received_utc TEXT NOT NULL,
                received_unix INTEGER NOT NULL,
                has_attachments INTEGER NOT NULL,
                web_link TEXT,
                body TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_messages_received ON messages(received_unix);
            CREATE INDEX IF NOT EXISTS ix_messages_sender ON messages(sender_address);
            CREATE INDEX IF NOT EXISTS ix_messages_imid ON messages(internet_message_id);
            CREATE TABLE IF NOT EXISTS chunks(
                id INTEGER PRIMARY KEY,
                message_rowid INTEGER NOT NULL REFERENCES messages(rowid) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL,
                text TEXT NOT NULL,
                embedding BLOB
            );
            CREATE INDEX IF NOT EXISTS ix_chunks_message ON chunks(message_rowid);
            CREATE INDEX IF NOT EXISTS ix_chunks_pending ON chunks(id) WHERE embedding IS NULL;
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                subject, body, sender, recipients,
                tokenize = 'unicode61 remove_diacritics 2'
            );
            """);
        // v2: keep the raw body so cleaning/chunking can be re-run offline ('reindex') without re-fetching.
        if (!ColumnExists("messages", "body_raw"))
            Exec("ALTER TABLE messages ADD COLUMN body_raw TEXT");
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---- meta / sync state -------------------------------------------------------------

    public string? GetMeta(string key) => Scalar<string?>("SELECT value FROM meta WHERE key=$k", ("$k", key));

    public void SetMeta(string key, string? value)
    {
        if (value is null) Exec("DELETE FROM meta WHERE key=$k", ("$k", key));
        else Exec("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
    }

    public string? GetSyncState(string folder) => Scalar<string?>("SELECT state FROM sync_state WHERE folder=$f", ("$f", folder));

    public void SetSyncState(string folder, string? state)
    {
        if (state is null) Exec("DELETE FROM sync_state WHERE folder=$f", ("$f", folder));
        else Exec("INSERT INTO sync_state(folder,state,updated_utc) VALUES($f,$s,$u) ON CONFLICT(folder) DO UPDATE SET state=excluded.state, updated_utc=excluded.updated_utc",
            ("$f", folder), ("$s", state), ("$u", DateTimeOffset.UtcNow.ToString("O")));
    }

    // ---- messages ---------------------------------------------------------------------

    /// <summary>Insert or replace a message with its cleaned body and chunk texts. Chunks start un-embedded.</summary>
    public void UpsertMessage(MailMessage message, string cleanBody, IReadOnlyList<string> chunks)
    {
        using var tx = _db.BeginTransaction();

        var existing = Scalar<long?>("SELECT rowid FROM messages WHERE id=$id", ("$id", message.Id));
        if (existing is { } oldRowId)
        {
            Exec("DELETE FROM chunks WHERE message_rowid=$r", ("$r", oldRowId));
            Exec("DELETE FROM messages_fts WHERE rowid=$r", ("$r", oldRowId));
            Exec("DELETE FROM messages WHERE rowid=$r", ("$r", oldRowId));
        }

        var recipients = string.Join("; ", message.To.Concat(message.Cc).Select(a => a.ToString()));
        Exec("""
            INSERT INTO messages(id, internet_message_id, conversation_id, folder, subject, sender_name, sender_address,
                                 recipients, received_utc, received_unix, has_attachments, web_link, body, body_raw)
            VALUES($id,$imid,$conv,$folder,$subject,$sname,$saddr,$rcpt,$rutc,$runix,$att,$link,$body,$raw)
            """,
            ("$id", message.Id), ("$imid", message.InternetMessageId), ("$conv", message.ConversationId),
            ("$folder", message.Folder), ("$subject", message.Subject), ("$sname", message.From?.Name),
            ("$saddr", message.From?.Address), ("$rcpt", recipients), ("$rutc", message.Received.UtcDateTime.ToString("O")),
            ("$runix", message.Received.ToUnixTimeSeconds()), ("$att", message.HasAttachments ? 1 : 0),
            ("$link", message.WebLink), ("$body", cleanBody), ("$raw", message.Body));
        var rowId = Scalar<long>("SELECT last_insert_rowid()");

        Exec("INSERT INTO messages_fts(rowid, subject, body, sender, recipients) VALUES($r,$s,$b,$sender,$rcpt)",
            ("$r", rowId), ("$s", message.Subject), ("$b", cleanBody),
            ("$sender", message.From?.ToString() ?? ""), ("$rcpt", recipients));

        for (var i = 0; i < chunks.Count; i++)
            Exec("INSERT INTO chunks(message_rowid, ordinal, text) VALUES($r,$o,$t)", ("$r", rowId), ("$o", i), ("$t", chunks[i]));

        tx.Commit();
    }

    /// <summary>Everything needed to re-run cleaning and chunking for stored messages (only rows that kept a raw body).</summary>
    public IEnumerable<(long RowId, MailMessage Message)> EnumerateRaw()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT rowid, id, internet_message_id, conversation_id, folder, subject, sender_name, sender_address,
                   received_utc, has_attachments, web_link, body_raw
            FROM messages WHERE body_raw IS NOT NULL ORDER BY rowid
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var message = new MailMessage
            {
                Id = reader.GetString(1),
                InternetMessageId = reader.IsDBNull(2) ? null : reader.GetString(2),
                ConversationId = reader.IsDBNull(3) ? null : reader.GetString(3),
                Folder = reader.GetString(4),
                Subject = reader.GetString(5),
                From = reader.IsDBNull(7) ? null : new MailAddress(reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7)),
                Received = DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
                HasAttachments = reader.GetInt64(9) == 1,
                WebLink = reader.IsDBNull(10) ? null : reader.GetString(10),
                Body = reader.GetString(11),
            };
            yield return (reader.GetInt64(0), message);
        }
    }

    /// <summary>Replace the cleaned body and chunks of an existing message in place (row id and metadata are kept).</summary>
    public void ReplaceContent(long rowId, string cleanBody, IReadOnlyList<string> chunks)
    {
        using var tx = _db.BeginTransaction();
        Exec("UPDATE messages SET body=$b WHERE rowid=$r", ("$b", cleanBody), ("$r", rowId));
        Exec("UPDATE messages_fts SET body=$b WHERE rowid=$r", ("$b", cleanBody), ("$r", rowId));
        Exec("DELETE FROM chunks WHERE message_rowid=$r", ("$r", rowId));
        for (var i = 0; i < chunks.Count; i++)
            Exec("INSERT INTO chunks(message_rowid, ordinal, text) VALUES($r,$o,$t)", ("$r", rowId), ("$o", i), ("$t", chunks[i]));
        tx.Commit();
    }

    public long CountMessagesWithoutRaw() => Scalar<long>("SELECT COUNT(*) FROM messages WHERE body_raw IS NULL");

    public bool DeleteMessage(string id)
    {
        using var tx = _db.BeginTransaction();
        var rowId = Scalar<long?>("SELECT rowid FROM messages WHERE id=$id", ("$id", id));
        if (rowId is null) return false;
        Exec("DELETE FROM chunks WHERE message_rowid=$r", ("$r", rowId));
        Exec("DELETE FROM messages_fts WHERE rowid=$r", ("$r", rowId));
        Exec("DELETE FROM messages WHERE rowid=$r", ("$r", rowId));
        tx.Commit();
        return true;
    }

    public Dictionary<long, MessageRow> GetMessages(IEnumerable<long> rowIds)
    {
        var result = new Dictionary<long, MessageRow>();
        var ids = rowIds.Distinct().ToList();
        if (ids.Count == 0) return result;

        foreach (var batch in ids.Chunk(500))
        {
            using var cmd = _db.CreateCommand();
            var names = batch.Select((_, i) => $"$p{i}").ToArray();
            cmd.CommandText = $"""
                SELECT rowid, id, internet_message_id, conversation_id, folder, subject, sender_name, sender_address,
                       recipients, received_utc, has_attachments, web_link, body
                FROM messages WHERE rowid IN ({string.Join(",", names)})
                """;
            for (var i = 0; i < batch.Length; i++) cmd.Parameters.AddWithValue(names[i], batch[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new MessageRow(
                    reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8), DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.GetInt64(10) == 1, reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetString(12));
                result[row.RowId] = row;
            }
        }
        return result;
    }

    /// <summary>Resolve a Graph id, an Internet-Message-Id (with or without angle brackets) or "rowid:N".</summary>
    public long? FindMessageRowId(string reference)
    {
        if (reference.StartsWith("rowid:", StringComparison.OrdinalIgnoreCase) && long.TryParse(reference[6..], out var r))
            return Scalar<long?>("SELECT rowid FROM messages WHERE rowid=$r", ("$r", r));
        var imid = reference.Trim();
        var bracketed = imid.StartsWith('<') ? imid : $"<{imid}>";
        return Scalar<long?>("SELECT rowid FROM messages WHERE id=$x OR internet_message_id=$x OR internet_message_id=$b LIMIT 1",
            ("$x", imid), ("$b", bracketed));
    }

    // ---- filters ----------------------------------------------------------------------

    /// <summary>Row ids matching the structured filters of a query, or null when the query has no filters.</summary>
    public HashSet<long>? FilterRowIds(ParsedQuery query)
    {
        if (!query.HasFilters) return null;
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (query.From is not null) { where.Add("(sender_address LIKE $from OR sender_name LIKE $from)"); args.Add(("$from", $"%{query.From}%")); }
        if (query.To is not null) { where.Add("recipients LIKE $to"); args.Add(("$to", $"%{query.To}%")); }
        if (query.After is { } after) { where.Add("received_unix >= $after"); args.Add(("$after", after.ToUnixTimeSeconds())); }
        if (query.Before is { } before) { where.Add("received_unix < $before"); args.Add(("$before", before.ToUnixTimeSeconds())); }
        if (query.HasAttachments is { } att) { where.Add("has_attachments = $att"); args.Add(("$att", att ? 1 : 0)); }
        if (query.Folder is not null) { where.Add("folder = $folder COLLATE NOCASE"); args.Add(("$folder", query.Folder)); }

        var set = new HashSet<long>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT rowid FROM messages WHERE {string.Join(" AND ", where)}";
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetInt64(0));
        return set;
    }

    // ---- full text ----------------------------------------------------------------------

    public List<FtsHit> FullTextSearch(string ftsQuery, HashSet<long>? allowed, int limit)
    {
        var hits = new List<FtsHit>();
        if (string.IsNullOrWhiteSpace(ftsQuery)) return hits;
        using var cmd = _db.CreateCommand();
        // subject weighted 3x, sender/recipients 2x
        cmd.CommandText = """
            SELECT rowid, bm25(messages_fts, 3.0, 1.0, 2.0, 2.0) AS score,
                   snippet(messages_fts, 1, '[', ']', '…', 14)
            FROM messages_fts WHERE messages_fts MATCH $q ORDER BY score LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        // when filtering, over-fetch so that enough survivors remain
        cmd.Parameters.AddWithValue("$limit", allowed is null ? limit : Math.Max(limit * 20, 1000));
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && hits.Count < limit)
        {
            var rowId = reader.GetInt64(0);
            if (allowed is not null && !allowed.Contains(rowId)) continue;
            hits.Add(new FtsHit(rowId, reader.GetDouble(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
        }
        return hits;
    }

    // ---- embeddings -------------------------------------------------------------------

    public List<(long ChunkId, string Text)> GetChunksWithoutEmbedding(int limit)
    {
        var list = new List<(long, string)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, text FROM chunks WHERE embedding IS NULL ORDER BY id LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetInt64(0), reader.GetString(1)));
        return list;
    }

    public void SetEmbeddings(IReadOnlyList<(long ChunkId, float[] Vector)> embeddings)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE chunks SET embedding=$e WHERE id=$id";
        var pE = cmd.Parameters.Add("$e", SqliteType.Blob);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var (id, vector) in embeddings)
        {
            pE.Value = MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();
            pId.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void ClearEmbeddings()
    {
        Exec("UPDATE chunks SET embedding=NULL");
        SetMeta("embedding_model", null);
        SetMeta("embedding_dims", null);
    }

    public EmbeddingIndex LoadEmbeddings(int dimensions)
    {
        var count = Scalar<long>("SELECT COUNT(*) FROM chunks WHERE embedding IS NOT NULL");
        var chunkIds = new long[count];
        var messageIds = new long[count];
        var data = new float[count * dimensions];

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, message_rowid, embedding FROM chunks WHERE embedding IS NOT NULL";
        using var reader = cmd.ExecuteReader();
        var i = 0;
        var bytes = new byte[dimensions * sizeof(float)];
        while (reader.Read() && i < count)
        {
            chunkIds[i] = reader.GetInt64(0);
            messageIds[i] = reader.GetInt64(1);
            var read = reader.GetBytes(2, 0, bytes, 0, bytes.Length);
            if (read != bytes.Length)
                throw new InvalidOperationException($"Chunk {chunkIds[i]} has an embedding of unexpected size; run 'embed --reset'.");
            MemoryMarshal.Cast<byte, float>(bytes).CopyTo(data.AsSpan(i * dimensions, dimensions));
            i++;
        }
        return new EmbeddingIndex { Dimensions = dimensions, ChunkIds = chunkIds, MessageRowIds = messageIds, Data = data };
    }

    public string? GetChunkText(long chunkId) => Scalar<string?>("SELECT text FROM chunks WHERE id=$id", ("$id", chunkId));

    // ---- stats ------------------------------------------------------------------------

    public StoreStats GetStats() => new(
        Scalar<long>("SELECT COUNT(*) FROM messages"),
        Scalar<long>("SELECT COUNT(*) FROM chunks"),
        Scalar<long>("SELECT COUNT(*) FROM chunks WHERE embedding IS NOT NULL"),
        GetMeta("embedding_model"),
        File.Exists(_path) ? new FileInfo(_path).Length : 0);

    public IEnumerable<(string Folder, string UpdatedUtc)> GetSyncedFolders()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT folder, updated_utc FROM sync_state ORDER BY folder";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return (reader.GetString(0), reader.GetString(1));
    }

    // ---- helpers ----------------------------------------------------------------------

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return default!;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, target);
    }

    public void Dispose() => _db.Dispose();
}

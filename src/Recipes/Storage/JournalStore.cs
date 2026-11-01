using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Recipes.Domain;

namespace Recipes.Storage;

/// <summary>SQLite journal: current entries, every version, FTS5 text index, vectors, lookup log, action answers.</summary>
public sealed class JournalStore : IDisposable
{
    public const string SeedId = "e_oven-dial";
    public const int MinEntries = 10;
    public const int MinOlderLookups = 2;
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);
    public static readonly TimeSpan OlderThan = TimeSpan.FromHours(24);

    private const string Columns = "id, kind, title, body, tags, data, source, version, created_at, updated_at";

    private readonly SqliteConnection _db;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    public JournalStore(string connectionString, TimeProvider clock)
    {
        _clock = clock;
        _db = new SqliteConnection(connectionString);
        _db.Open();
        Exec("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS entries (
              id TEXT PRIMARY KEY, kind TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL,
              tags TEXT NOT NULL, data TEXT, source TEXT, version INTEGER NOT NULL,
              created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS entry_versions (
              id TEXT NOT NULL, version INTEGER NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL,
              tags TEXT NOT NULL, data TEXT, source TEXT, saved_at INTEGER NOT NULL, PRIMARY KEY (id, version));
            CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(id UNINDEXED, title, body, tags);
            CREATE TABLE IF NOT EXISTS vectors (
              id TEXT PRIMARY KEY, version INTEGER NOT NULL, model TEXT NOT NULL, vec BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS lookups (id TEXT NOT NULL, at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS actions (
              action_id TEXT PRIMARY KEY, status INTEGER NOT NULL, body TEXT NOT NULL, at INTEGER NOT NULL);
            """);
    }

    public void SeedIfMissing()
    {
        using var stream = typeof(JournalStore).Assembly.GetManifestResourceStream("seed.oven-dial.json")
            ?? throw new InvalidOperationException("seed.oven-dial.json is not embedded.");
        var seed = JsonNode.Parse(stream)!.AsObject();
        lock (_gate)
        {
            if (Scalar("SELECT 1 FROM entries WHERE id = $id", ("$id", SeedId)) is not null)
                return;
            using var tx = _db.BeginTransaction();
            Insert(SeedId, 1, seed["kind"]!.GetValue<string>(), seed["title"]!.GetValue<string>(),
                seed["body"]!.GetValue<string>(),
                [.. seed["tags"]!.AsArray().Select(t => t!.GetValue<string>())],
                seed["data"]!.AsObject(), null, _clock.GetUtcNow(), _clock.GetUtcNow(), create: true);
            tx.Commit();
        }
    }

    public SaveResult Save(EntryInput input)
    {
        var errors = new List<FieldError>();
        JsonObject? data = null;
        if (input.Data is JsonObject obj)
            data = obj;
        else if (input.Data is not null)
            errors.Add(new("data", "Data must be a JSON object."));
        errors.AddRange(EntryValidator.Validate(input, data));
        if (errors.Count > 0)
            return new(SaveStatus.Invalid, Errors: errors);

        var tags = EntryValidator.NormalizeTags(input.Tags ?? []);
        var title = input.Title.Trim();
        var now = _clock.GetUtcNow();

        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            if (input.Id is null)
            {
                var id = NewId();
                Insert(id, 1, input.Kind, title, input.Body ?? "", tags, data, input.Source, now, now, create: true);
                tx.Commit();
                return new(SaveStatus.Saved, ReadCurrent(id));
            }

            var current = ReadCurrent(input.Id);
            if (current is null)
                return new(SaveStatus.NotFound, Errors: [new("id", "No entry has this id.")]);
            if (input.BaseVersion is null)
                return new(SaveStatus.Invalid, Errors: [new("version", "Send the version this edit is based on.")]);
            if (input.BaseVersion != current.Version)
                return new(SaveStatus.Conflict, current, CurrentVersion: current.Version);

            Insert(input.Id, current.Version + 1, input.Kind, title, input.Body ?? "", tags, data, input.Source,
                current.CreatedAt, now, create: false);
            tx.Commit();
            return new(SaveStatus.Saved, ReadCurrent(input.Id));
        }
    }

    public Entry? Get(string id)
    {
        lock (_gate)
            return ReadCurrent(id);
    }

    public Entry? GetVersion(string id, long version)
    {
        lock (_gate)
        {
            using var cmd = Command($"""
                SELECT v.id, v.kind, v.title, v.body, v.tags, v.data, v.source, v.version, e.created_at, v.saved_at
                FROM entry_versions v JOIN entries e ON e.id = v.id WHERE v.id = $id AND v.version = $v
                """, ("$id", id), ("$v", version));
            using var r = cmd.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }
    }

    public IReadOnlyList<EntryVersion> Versions(string id)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT version, title, saved_at FROM entry_versions WHERE id = $id ORDER BY version",
                ("$id", id));
            using var r = cmd.ExecuteReader();
            var list = new List<EntryVersion>();
            while (r.Read())
                list.Add(new(r.GetInt64(0), r.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))));
            return list;
        }
    }

    public void RecordLookup(string id)
    {
        lock (_gate)
            Exec("INSERT INTO lookups (id, at) VALUES ($id, $at)", ("$id", id), ("$at", _clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    public IReadOnlyList<Hit> SearchText(string query, string? kind, int limit)
    {
        var match = ToMatch(query);
        if (match is null)
            return [];
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT e.id, bm25(entries_fts) FROM entries_fts JOIN entries e ON e.id = entries_fts.id
                WHERE entries_fts MATCH $q AND ($kind IS NULL OR e.kind = $kind)
                ORDER BY bm25(entries_fts) LIMIT $n
                """, ("$q", match), ("$kind", kind), ("$n", limit));
            var ranked = new List<(string Id, double Score)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                    ranked.Add((r.GetString(0), -r.GetDouble(1)));
            }

            return [.. ranked.Select(x => new Hit(ReadCurrent(x.Id)!, x.Score))];
        }
    }

    public void SaveVector(string id, long version, string model, float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO vectors (id, version, model, vec) VALUES ($id, $v, $m, $b)
                ON CONFLICT(id) DO UPDATE SET version = $v, model = $m, vec = $b
                WHERE excluded.version >= vectors.version
                """, ("$id", id), ("$v", version), ("$m", model), ("$b", bytes));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Vectors of the current version of each entry, for one embedding model.</summary>
    public IReadOnlyList<(string Id, float[] Vector)> Vectors(string model, string? kind)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT v.id, v.vec FROM vectors v JOIN entries e ON e.id = v.id AND e.version = v.version
                WHERE v.model = $m AND ($kind IS NULL OR e.kind = $kind)
                """, ("$m", model), ("$kind", kind));
            using var r = cmd.ExecuteReader();
            var list = new List<(string, float[])>();
            while (r.Read())
            {
                var bytes = (byte[])r[1];
                var vec = new float[bytes.Length / sizeof(float)];
                Buffer.BlockCopy(bytes, 0, vec, 0, bytes.Length);
                list.Add((r.GetString(0), vec));
            }

            return list;
        }
    }

    public IReadOnlyList<Entry> GetMany(IEnumerable<string> ids)
    {
        lock (_gate)
            return [.. ids.Select(ReadCurrent).OfType<Entry>()];
    }

    /// <summary>Entries without a current vector for the model, so a failed embedding call can be retried.</summary>
    public IReadOnlyList<Entry> WithoutVector(string model, int limit)
    {
        lock (_gate)
        {
            using var cmd = Command($"""
                SELECT {Columns} FROM entries e
                WHERE NOT EXISTS (SELECT 1 FROM vectors v WHERE v.id = e.id AND v.version = e.version AND v.model = $m)
                LIMIT $n
                """, ("$m", model), ("$n", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<Entry>();
            while (r.Read())
                list.Add(Map(r));
            return list;
        }
    }

    /// <summary>Latest entries first, for the home screen. The seed sorts by its own time like any other entry.</summary>
    public IReadOnlyList<Entry> Recent(int limit)
    {
        lock (_gate)
        {
            using var cmd = Command($"SELECT {Columns} FROM entries ORDER BY updated_at DESC, created_at DESC LIMIT $n",
                ("$n", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<Entry>();
            while (r.Read())
                list.Add(Map(r));
            return list;
        }
    }

    public Entry? NewestDial(string? id = null)
    {
        lock (_gate)
        {
            using var cmd = Command($"""
                SELECT {Columns} FROM entries
                WHERE kind = 'config' AND json_extract(data, '$.type') = 'dial' AND ($id IS NULL OR id = $id)
                ORDER BY updated_at DESC, created_at DESC LIMIT 1
                """, ("$id", id));
            using var r = cmd.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }
    }

    public JournalStats GetStats()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var entries = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM entries"), CultureInfo.InvariantCulture);
            var firstMs = Scalar("SELECT MIN(created_at) FROM entries WHERE id <> $seed", ("$seed", SeedId));
            if (firstMs is null or DBNull)
                return new(entries, 0, 0, null, null, false, "not_started");

            var start = DateTimeOffset.FromUnixTimeMilliseconds((long)firstMs);
            var end = start + Window;
            var saved = Convert.ToInt32(Scalar(
                "SELECT COUNT(*) FROM entries WHERE id <> $seed AND created_at BETWEEN $s AND $e",
                ("$seed", SeedId), ("$s", start.ToUnixTimeMilliseconds()), ("$e", end.ToUnixTimeMilliseconds())),
                CultureInfo.InvariantCulture);
            var older = Convert.ToInt32(Scalar("""
                SELECT COUNT(*) FROM lookups l JOIN entries e ON e.id = l.id
                WHERE l.at BETWEEN $s AND $e AND l.at - e.created_at >= $age
                """, ("$s", start.ToUnixTimeMilliseconds()), ("$e", end.ToUnixTimeMilliseconds()),
                ("$age", (long)OlderThan.TotalMilliseconds)), CultureInfo.InvariantCulture);

            var pass = saved >= MinEntries && older >= MinOlderLookups;
            var status = pass ? "passed" : now > end ? "failed" : "running";
            return new(entries, saved, older, start, end, pass, status);
        }
    }

    public (int Status, string Body)? GetAction(string actionId)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT status, body FROM actions WHERE action_id = $a", ("$a", actionId));
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt32(0), r.GetString(1)) : null;
        }
    }

    /// <summary>First answer wins: a repeat of the same <c>action_id</c> never overwrites it.</summary>
    public void PutAction(string actionId, int status, string body)
    {
        lock (_gate)
            Exec("INSERT OR IGNORE INTO actions (action_id, status, body, at) VALUES ($a, $s, $b, $t)",
                ("$a", actionId), ("$s", status), ("$b", body), ("$t", _clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    public void Dispose() => _db.Dispose();

    private void Insert(string id, long version, string kind, string title, string body, List<string> tags,
        JsonObject? data, string? source, DateTimeOffset created, DateTimeOffset saved, bool create)
    {
        var tagsJson = JsonSerializer.Serialize(tags);
        var dataJson = data?.ToJsonString();
        var args = new (string, object?)[]
        {
            ("$id", id), ("$v", version), ("$kind", kind), ("$title", title), ("$body", body), ("$tags", tagsJson),
            ("$data", dataJson), ("$source", source), ("$created", created.ToUnixTimeMilliseconds()),
            ("$saved", saved.ToUnixTimeMilliseconds()),
        };

        Exec(create
            ? "INSERT INTO entries (id, kind, title, body, tags, data, source, version, created_at, updated_at) VALUES ($id, $kind, $title, $body, $tags, $data, $source, $v, $created, $saved)"
            : "UPDATE entries SET kind = $kind, title = $title, body = $body, tags = $tags, data = $data, source = $source, version = $v, updated_at = $saved WHERE id = $id",
            args);
        Exec("INSERT INTO entry_versions (id, version, kind, title, body, tags, data, source, saved_at) VALUES ($id, $v, $kind, $title, $body, $tags, $data, $source, $saved)", args);
        Exec("DELETE FROM entries_fts WHERE id = $id", ("$id", id));
        Exec("INSERT INTO entries_fts (id, title, body, tags) VALUES ($id, $title, $body, $tagsText)",
            ("$id", id), ("$title", title), ("$body", body), ("$tagsText", string.Join(' ', tags)));
    }

    private Entry? ReadCurrent(string id)
    {
        using var cmd = Command($"SELECT {Columns} FROM entries WHERE id = $id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    private static Entry Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? [],
        r.IsDBNull(5) ? null : JsonNode.Parse(r.GetString(5)) as JsonObject,
        r.IsDBNull(6) ? null : r.GetString(6),
        r.GetInt64(7),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)));

    /// <summary>Quote every word so user text can never be read as FTS5 syntax; OR them so bm25 ranks partial matches.</summary>
    private static string? ToMatch(string query)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in query)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
            else if (sb.Length > 0)
            {
                words.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0)
            words.Add(sb.ToString());
        return words.Count == 0 ? null : string.Join(" OR ", words.Select(w => $"\"{w}\""));
    }

    private static string NewId()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var bits = BitConverter.ToUInt64(bytes);
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++, bits >>= 5)
            chars[i] = alphabet[(int)(bits & 31)];
        return "e_" + new string(chars);
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteScalar();
    }
}

using LocalBack.Core.Storage;
using Microsoft.Data.Sqlite;

namespace LocalBack.Core.Indexing;

public sealed record IndexedFile(long Size, long MTimeTicks, int Attributes, string Hash);

public sealed record FileVersion(string Hash, long Size, DateTimeOffset FirstSeen, DateTimeOffset? Superseded);

public sealed record SetState(string? LastManifest, DateTimeOffset? LastRun, DateTimeOffset? LastFullCheck, string? LastError);

/// <summary>
/// SQLite index on the PC. Mirrors the newest manifest of each set for fast change detection,
/// keeps per-file version history, and queues paths that changed while the drive was away.
/// It is a cache: if it disagrees with the drive it is rebuilt from the drive.
/// </summary>
public sealed class LocalIndex : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public LocalIndex(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-2000;");
        Exec("""
            CREATE TABLE IF NOT EXISTS sets_state(
              set_id TEXT PRIMARY KEY, last_manifest TEXT, last_run INTEGER, last_full INTEGER, last_error TEXT);
            CREATE TABLE IF NOT EXISTS files(
              set_id TEXT NOT NULL, root TEXT NOT NULL, path TEXT NOT NULL,
              size INTEGER NOT NULL, mtime INTEGER NOT NULL, attr INTEGER NOT NULL, hash TEXT NOT NULL,
              PRIMARY KEY(set_id, root, path));
            CREATE TABLE IF NOT EXISTS versions(
              set_id TEXT NOT NULL, root TEXT NOT NULL, path TEXT NOT NULL, hash TEXT NOT NULL, size INTEGER NOT NULL,
              first_seen INTEGER NOT NULL, superseded INTEGER,
              PRIMARY KEY(set_id, root, path, first_seen));
            CREATE TABLE IF NOT EXISTS pending(
              set_id TEXT NOT NULL, path TEXT NOT NULL, queued INTEGER NOT NULL,
              PRIMARY KEY(set_id, path));
            """);
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Ticks(DateTimeOffset t) => t.UtcTicks;
    private static DateTimeOffset FromTicks(long t) => new(t, TimeSpan.Zero);

    // ---- set state ----

    public SetState GetState(string setId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT last_manifest, last_run, last_full, last_error FROM sets_state WHERE set_id=$s";
            cmd.Parameters.AddWithValue("$s", setId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new SetState(null, null, null, null);
            return new SetState(
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? null : FromTicks(r.GetInt64(1)),
                r.IsDBNull(2) ? null : FromTicks(r.GetInt64(2)),
                r.IsDBNull(3) ? null : r.GetString(3));
        }
    }

    public void RecordRun(string setId, DateTimeOffset when, bool fullCheck, string? error)
    {
        lock (_gate)
        {
            EnsureState(setId);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = fullCheck && error == null
                ? "UPDATE sets_state SET last_run=$t, last_full=$t, last_error=NULL WHERE set_id=$s"
                : error == null
                    ? "UPDATE sets_state SET last_run=$t, last_error=NULL WHERE set_id=$s"
                    : "UPDATE sets_state SET last_error=$e WHERE set_id=$s";
            cmd.Parameters.AddWithValue("$s", setId);
            cmd.Parameters.AddWithValue("$t", Ticks(when));
            cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private void EnsureState(string setId, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO sets_state(set_id) VALUES($s)";
        cmd.Parameters.AddWithValue("$s", setId);
        cmd.ExecuteNonQuery();
    }

    // ---- files (mirror of the newest manifest) ----

    public Dictionary<FileKey, IndexedFile> LoadFiles(string setId)
    {
        lock (_gate)
        {
            var map = new Dictionary<FileKey, IndexedFile>(FileKey.Comparer);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT root, path, size, mtime, attr, hash FROM files WHERE set_id=$s";
            cmd.Parameters.AddWithValue("$s", setId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                map[new FileKey(r.GetString(0), r.GetString(1))] = new IndexedFile(r.GetInt64(2), r.GetInt64(3), r.GetInt32(4), r.GetString(5));
            return map;
        }
    }

    public IndexedFile? GetFile(string setId, FileKey key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT size, mtime, attr, hash FROM files WHERE set_id=$s AND root=$r AND path=$p";
            cmd.Parameters.AddWithValue("$s", setId);
            cmd.Parameters.AddWithValue("$r", key.Root);
            cmd.Parameters.AddWithValue("$p", key.Path);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new IndexedFile(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3)) : null;
        }
    }

    public sealed record Change(FileKey Key, IndexedFile? Old, IndexedFile? New);

    /// <summary>
    /// Records a run. <paramref name="manifestName"/> is the snapshot just written (null when nothing changed
    /// and no snapshot was written, e.g. only timestamps moved).
    /// </summary>
    public void Commit(string setId, string? manifestName, DateTimeOffset when, IReadOnlyCollection<Change> changes)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            EnsureState(setId, tx);
            if (manifestName != null)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE sets_state SET last_manifest=$m WHERE set_id=$s";
                cmd.Parameters.AddWithValue("$s", setId);
                cmd.Parameters.AddWithValue("$m", manifestName);
                cmd.ExecuteNonQuery();
            }

            using var upsert = _db.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = "INSERT OR REPLACE INTO files(set_id, root, path, size, mtime, attr, hash) VALUES($s,$r,$p,$size,$m,$a,$h)";
            var uS = upsert.Parameters.Add("$s", SqliteType.Text); var uR = upsert.Parameters.Add("$r", SqliteType.Text);
            var uP = upsert.Parameters.Add("$p", SqliteType.Text); var uSize = upsert.Parameters.Add("$size", SqliteType.Integer);
            var uM = upsert.Parameters.Add("$m", SqliteType.Integer); var uA = upsert.Parameters.Add("$a", SqliteType.Integer);
            var uH = upsert.Parameters.Add("$h", SqliteType.Text);

            using var del = _db.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM files WHERE set_id=$s AND root=$r AND path=$p";
            var dS = del.Parameters.Add("$s", SqliteType.Text); var dR = del.Parameters.Add("$r", SqliteType.Text); var dP = del.Parameters.Add("$p", SqliteType.Text);

            using var close = _db.CreateCommand();
            close.Transaction = tx;
            close.CommandText = "UPDATE versions SET superseded=$t WHERE set_id=$s AND root=$r AND path=$p AND superseded IS NULL";
            var cS = close.Parameters.Add("$s", SqliteType.Text); var cR = close.Parameters.Add("$r", SqliteType.Text);
            var cP = close.Parameters.Add("$p", SqliteType.Text); var cT = close.Parameters.Add("$t", SqliteType.Integer);

            using var open = _db.CreateCommand();
            open.Transaction = tx;
            open.CommandText = "INSERT OR REPLACE INTO versions(set_id, root, path, hash, size, first_seen, superseded) VALUES($s,$r,$p,$h,$size,$t,NULL)";
            var oS = open.Parameters.Add("$s", SqliteType.Text); var oR = open.Parameters.Add("$r", SqliteType.Text);
            var oP = open.Parameters.Add("$p", SqliteType.Text); var oH = open.Parameters.Add("$h", SqliteType.Text);
            var oSize = open.Parameters.Add("$size", SqliteType.Integer); var oT = open.Parameters.Add("$t", SqliteType.Integer);

            foreach (var c in changes)
            {
                if (c.New is { } n)
                {
                    uS.Value = setId; uR.Value = c.Key.Root; uP.Value = c.Key.Path; uSize.Value = n.Size;
                    uM.Value = n.MTimeTicks; uA.Value = n.Attributes; uH.Value = n.Hash;
                    upsert.ExecuteNonQuery();
                }
                else
                {
                    dS.Value = setId; dR.Value = c.Key.Root; dP.Value = c.Key.Path;
                    del.ExecuteNonQuery();
                }

                bool contentChanged = c.Old?.Hash != c.New?.Hash;
                if (manifestName != null && contentChanged)
                {
                    if (c.Old != null)
                    {
                        cS.Value = setId; cR.Value = c.Key.Root; cP.Value = c.Key.Path; cT.Value = Ticks(when);
                        close.ExecuteNonQuery();
                    }
                    if (c.New is { } nv)
                    {
                        oS.Value = setId; oR.Value = c.Key.Root; oP.Value = c.Key.Path; oH.Value = nv.Hash;
                        oSize.Value = nv.Size; oT.Value = Ticks(when);
                        open.ExecuteNonQuery();
                    }
                }
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Rebuilds the set's rows from the manifests on the drive (oldest first). Used when the index is missing,
    /// stale (another PC wrote to the drive) or after old versions were pruned.
    /// </summary>
    public void Rebuild(string setId, IEnumerable<(string Name, Manifest Manifest)> manifests)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var table in new[] { "files", "versions" })
            {
                using var clear = _db.CreateCommand();
                clear.Transaction = tx;
                clear.CommandText = $"DELETE FROM {table} WHERE set_id=$s";
                clear.Parameters.AddWithValue("$s", setId);
                clear.ExecuteNonQuery();
            }
            EnsureState(setId, tx);

            var open = new Dictionary<FileKey, (string Hash, long Size, DateTimeOffset First)>(FileKey.Comparer);
            using var ins = _db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO versions(set_id, root, path, hash, size, first_seen, superseded) VALUES($s,$r,$p,$h,$size,$f,$x)";
            var pS = ins.Parameters.Add("$s", SqliteType.Text); var pR = ins.Parameters.Add("$r", SqliteType.Text);
            var pP = ins.Parameters.Add("$p", SqliteType.Text); var pH = ins.Parameters.Add("$h", SqliteType.Text);
            var pSize = ins.Parameters.Add("$size", SqliteType.Integer); var pF = ins.Parameters.Add("$f", SqliteType.Integer);
            var pX = ins.Parameters.Add("$x", SqliteType.Integer);

            void Close(FileKey k, (string Hash, long Size, DateTimeOffset First) v, DateTimeOffset? superseded)
            {
                pS.Value = setId; pR.Value = k.Root; pP.Value = k.Path; pH.Value = v.Hash; pSize.Value = v.Size;
                pF.Value = Ticks(v.First); pX.Value = superseded is { } s ? Ticks(s) : DBNull.Value;
                ins.ExecuteNonQuery();
            }

            string? lastName = null;
            Manifest? last = null;
            foreach (var (name, m) in manifests)
            {
                var map = m.ToMap();
                foreach (var (key, e) in map)
                {
                    if (open.TryGetValue(key, out var cur) && cur.Hash == e.Hash) continue;
                    if (open.TryGetValue(key, out cur)) Close(key, cur, m.CreatedUtc);
                    open[key] = (e.Hash, e.Size, m.CreatedUtc);
                }
                foreach (var key in open.Keys.Where(k => !map.ContainsKey(k)).ToList())
                {
                    Close(key, open[key], m.CreatedUtc);
                    open.Remove(key);
                }
                lastName = name;
                last = m;
            }
            foreach (var (k, v) in open) Close(k, v, null);

            if (last != null)
            {
                using var up = _db.CreateCommand();
                up.Transaction = tx;
                up.CommandText = "INSERT OR REPLACE INTO files(set_id, root, path, size, mtime, attr, hash) VALUES($s,$r,$p,$size,$m,$a,$h)";
                var fS = up.Parameters.Add("$s", SqliteType.Text); var fR = up.Parameters.Add("$r", SqliteType.Text);
                var fP = up.Parameters.Add("$p", SqliteType.Text); var fSize = up.Parameters.Add("$size", SqliteType.Integer);
                var fM = up.Parameters.Add("$m", SqliteType.Integer); var fA = up.Parameters.Add("$a", SqliteType.Integer);
                var fH = up.Parameters.Add("$h", SqliteType.Text);
                foreach (var e in last.Entries)
                {
                    if (e.Root < 0 || e.Root >= last.Roots.Count) continue;
                    fS.Value = setId; fR.Value = last.Roots[e.Root]; fP.Value = e.Path; fSize.Value = e.Size;
                    fM.Value = e.MTimeTicks; fA.Value = e.Attributes; fH.Value = e.Hash;
                    up.ExecuteNonQuery();
                }
            }

            using (var st = _db.CreateCommand())
            {
                st.Transaction = tx;
                st.CommandText = "UPDATE sets_state SET last_manifest=$m WHERE set_id=$s";
                st.Parameters.AddWithValue("$s", setId);
                st.Parameters.AddWithValue("$m", (object?)lastName ?? DBNull.Value);
                st.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<FileVersion> Versions(string setId, FileKey key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT hash, size, first_seen, superseded FROM versions WHERE set_id=$s AND root=$r AND path=$p ORDER BY first_seen DESC";
            cmd.Parameters.AddWithValue("$s", setId);
            cmd.Parameters.AddWithValue("$r", key.Root);
            cmd.Parameters.AddWithValue("$p", key.Path);
            using var r = cmd.ExecuteReader();
            var list = new List<FileVersion>();
            while (r.Read())
                list.Add(new FileVersion(r.GetString(0), r.GetInt64(1), FromTicks(r.GetInt64(2)), r.IsDBNull(3) ? null : FromTicks(r.GetInt64(3))));
            return list;
        }
    }

    // ---- pending (changes seen while the drive was away or paused) ----

    public void AddPending(string setId, IEnumerable<string> fullPaths, DateTimeOffset when)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO pending(set_id, path, queued) VALUES($s,$p,$t)";
            var s = cmd.Parameters.Add("$s", SqliteType.Text); var p = cmd.Parameters.Add("$p", SqliteType.Text); var t = cmd.Parameters.Add("$t", SqliteType.Integer);
            foreach (var path in fullPaths)
            {
                s.Value = setId; p.Value = path; t.Value = Ticks(when);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<string> Pending(string setId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path FROM pending WHERE set_id=$s ORDER BY queued";
            cmd.Parameters.AddWithValue("$s", setId);
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public int PendingCount(string setId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pending WHERE set_id=$s";
            cmd.Parameters.AddWithValue("$s", setId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void ClearPending(string setId, IEnumerable<string>? paths = null)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            if (paths == null)
            {
                cmd.CommandText = "DELETE FROM pending WHERE set_id=$s";
                cmd.Parameters.AddWithValue("$s", setId);
                cmd.ExecuteNonQuery();
            }
            else
            {
                cmd.CommandText = "DELETE FROM pending WHERE set_id=$s AND path=$p";
                var s = cmd.Parameters.Add("$s", SqliteType.Text); var p = cmd.Parameters.Add("$p", SqliteType.Text);
                foreach (var path in paths)
                {
                    s.Value = setId; p.Value = path;
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    public void RemoveSet(string setId)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var table in new[] { "files", "versions", "pending", "sets_state" })
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {table} WHERE set_id=$s";
                cmd.Parameters.AddWithValue("$s", setId);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}

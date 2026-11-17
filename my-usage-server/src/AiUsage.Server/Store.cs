using System.Globalization;
using AiUsage.Data;
using Microsoft.Data.Sqlite;

namespace AiUsage.Server;

/// <summary>One stored reading of a window.</summary>
public readonly record struct Reading(DateTimeOffset At, float Percent, DateTimeOffset? ResetAt, bool Stale);

/// <summary>
/// Every reading, kept. The apps thin their history out because a phone's
/// preferences file has to stay small; here a row is forty bytes and a year of
/// them is a few megabytes, so nothing is thrown away and a chart is cut down to
/// size when it is asked for instead.
///
/// One connection behind one lock. SQLite takes a single writer anyway, and the
/// load is a handful of rows every few minutes.
/// </summary>
public sealed class Store : IDisposable
{
    /// <summary>
    /// A window that is not moving still gets a row this often, so "flat for an
    /// hour" is in the data rather than inferred from a gap.
    /// </summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(15);

    /// <summary>Smaller than this is the provider's own rounding, not usage.</summary>
    private const float Noise = 0.05f;

    private const string SnapshotKey = "snapshot";

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, Reading> _latest = new(StringComparer.Ordinal);
    private (DateTimeOffset At, float Percent)? _latestCombined;

    public Store(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();

        Execute(
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS readings (
                provider TEXT    NOT NULL,
                label    TEXT    NOT NULL,   -- the window's name: "5h", "7d"
                at       INTEGER NOT NULL,   -- Unix milliseconds
                percent  REAL    NOT NULL,   -- remaining, 0-100
                reset_at INTEGER,
                stale    INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, label, at)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS combined (
                at      INTEGER PRIMARY KEY,
                percent REAL NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS kv (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            ) WITHOUT ROWID;
            """);

        LoadLatest();
    }

    /// <summary>The last snapshot written, so a restart resumes instead of starting blank.</summary>
    public Snapshot? LoadSnapshot() => SnapshotCodec.Decode(Get(SnapshotKey));

    /// <summary>
    /// Stores a snapshot and the readings in it. Only the accounts in
    /// <paramref name="fetched"/> (by key) were asked this round; the rest of the
    /// snapshot is carried over from before and has nothing new to say.
    ///
    /// The provider column holds the account key, so the first account of each
    /// provider ("claude") keeps the history it had before there were others.
    /// </summary>
    public void Record(Snapshot snapshot, IReadOnlyCollection<string> fetched)
    {
        var now = snapshot.FetchedAt;

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            foreach (var usage in snapshot.Usages)
            {
                if (usage.State != UsageState.Ok || !fetched.Contains(usage.AccountKey)) continue;
                var provider = usage.AccountKey;

                // A failed fetch replaces the account's entry and takes the plan
                // name with it, so keep the last one that was actually read: the
                // plan history prices a month by it.
                if (usage.Plan is { Length: > 0 } plan) Put("plan:" + provider, plan, transaction);

                foreach (var window in usage.Windows)
                {
                    if (window.RemainingPercent is not { } percent) continue;

                    var reading = new Reading(now, percent, window.ResetAt, usage.Stale);
                    var key = SeriesKey(provider, window.Label);
                    if (_latest.TryGetValue(key, out var last) && !WorthKeeping(last, reading)) continue;

                    using var insert = _connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT OR REPLACE INTO readings (provider, label, at, percent, reset_at, stale)
                        VALUES ($provider, $window, $at, $percent, $reset, $stale)
                        """;
                    insert.Parameters.AddWithValue("$provider", provider);
                    insert.Parameters.AddWithValue("$window", window.Label);
                    insert.Parameters.AddWithValue("$at", now.ToUnixTimeMilliseconds());
                    insert.Parameters.AddWithValue("$percent", percent);
                    insert.Parameters.AddWithValue(
                        "$reset",
                        window.ResetAt is { } reset ? reset.ToUnixTimeMilliseconds() : DBNull.Value);
                    insert.Parameters.AddWithValue("$stale", usage.Stale ? 1 : 0);
                    insert.ExecuteNonQuery();

                    _latest[key] = reading;
                }
            }

            if (CombinedUsage.Of(snapshot).Percent is { } combined
                && (_latestCombined is not { } previous
                    || Math.Abs(previous.Percent - combined) >= Noise
                    || now - previous.At >= Heartbeat))
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR REPLACE INTO combined (at, percent) VALUES ($at, $percent)";
                insert.Parameters.AddWithValue("$at", now.ToUnixTimeMilliseconds());
                insert.Parameters.AddWithValue("$percent", combined);
                insert.ExecuteNonQuery();

                _latestCombined = (now, combined);
            }

            Put(SnapshotKey, SnapshotCodec.Encode(snapshot), transaction);
            transaction.Commit();
        }
    }

    /// <summary>
    /// A reading is kept when it says something the last one did not: the level
    /// moved, the reset moved, it stopped or started being a stand-in, or enough
    /// time has passed that the flat stretch itself is worth a point.
    /// </summary>
    internal static bool WorthKeeping(Reading last, Reading next) =>
        Math.Abs(last.Percent - next.Percent) >= Noise
        || ResetMoved(last.ResetAt, next.ResetAt)
        || last.Stale != next.Stale
        || next.At - last.At >= Heartbeat;

    /// <summary>
    /// Some providers give the reset as "seconds from now", so the same reset
    /// comes back a few seconds apart on every fetch. Only a real move counts.
    /// </summary>
    private static bool ResetMoved(DateTimeOffset? last, DateTimeOffset? next)
    {
        if (last == null || next == null) return last != next;
        return (next.Value - last.Value).Duration() > TimeSpan.FromMinutes(2);
    }

    /// <summary>Every window that has a reading since <paramref name="since"/>, as (provider, window).</summary>
    public IReadOnlyList<(string Provider, string Window)> SeriesKeys(DateTimeOffset since)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT provider, label FROM readings WHERE at >= $since ORDER BY provider, label";
            command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());

            var keys = new List<(string, string)>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) keys.Add((reader.GetString(0), reader.GetString(1)));
            return keys;
        }
    }

    public IReadOnlyList<Reading> Series(string provider, string window, DateTimeOffset since)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT at, percent, reset_at, stale FROM readings
                WHERE provider = $provider AND label = $window AND at >= $since
                ORDER BY at
                """;
            command.Parameters.AddWithValue("$provider", provider);
            command.Parameters.AddWithValue("$window", window);
            command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());

            var readings = new List<Reading>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                readings.Add(new Reading(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                    reader.GetFloat(1),
                    reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    reader.GetInt64(3) != 0));
            }
            return readings;
        }
    }

    public IReadOnlyList<UsagePoint> Combined(DateTimeOffset since)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT at, percent FROM combined WHERE at >= $since ORDER BY at";
            command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());

            var points = new List<UsagePoint>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                points.Add(new UsagePoint(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                    reader.GetFloat(1)));
            }
            return points;
        }
    }

    /// <summary>The last plan name an account was seen on, or null when it never said.</summary>
    public string? Plan(string account) => Get("plan:" + account);

    /// <summary>What an account's plan was set to cost a month, or null when it was never set.</summary>
    public double? Price(string account) =>
        Get("price:" + account) is { } text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Sets what an account costs a month; null forgets it and falls back to the guess.</summary>
    public void SetPrice(string account, double? monthly)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            if (monthly is { } value)
            {
                command.CommandText = "INSERT OR REPLACE INTO kv (key, value) VALUES ($key, $value)";
                command.Parameters.AddWithValue("$value", value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                command.CommandText = "DELETE FROM kv WHERE key = $key";
            }

            command.Parameters.AddWithValue("$key", "price:" + account);
            command.ExecuteNonQuery();
        }
    }

    public long ReadingCount()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM readings";
            return (long)(command.ExecuteScalar() ?? 0L);
        }
    }

    // ---- Plumbing ---------------------------------------------------------

    /// <summary>The newest row of every series, so deciding whether to keep a reading needs no query.</summary>
    private void LoadLatest()
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT r.provider, r.label, r.at, r.percent, r.reset_at, r.stale
                FROM readings r
                JOIN (SELECT provider, label, MAX(at) AS at FROM readings GROUP BY provider, label) newest
                  ON newest.provider = r.provider AND newest.label = r.label AND newest.at = r.at
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                _latest[SeriesKey(reader.GetString(0), reader.GetString(1))] = new Reading(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    reader.GetFloat(3),
                    reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                    reader.GetInt64(5) != 0);
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT at, percent FROM combined ORDER BY at DESC LIMIT 1";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                _latestCombined = (
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                    reader.GetFloat(1));
            }
        }
    }

    /// <summary>The key the apps already use for a series: "claude|5h".</summary>
    public static string SeriesKey(string provider, string window) => $"{provider}|{window}";

    private string? Get(string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM kv WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    private void Put(string key, string value, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO kv (key, value) VALUES ($key, $value)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }
}

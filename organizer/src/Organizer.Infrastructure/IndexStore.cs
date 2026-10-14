using System.Text.Json;
using Microsoft.Data.Sqlite;
using Organizer.Core;

namespace Organizer.Infrastructure;

/// <summary>Application-owned index. Never stored under a scanned source. One serialized writer per instance.</summary>
public sealed class IndexStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public IndexStore(string databasePath)
    {
        databasePath = Path.GetFullPath(databasePath);
        if (PathSafety.HasLinkComponent(databasePath)) throw new IOException("Index path must not contain a symbolic link.");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5 }.ToString());
        try
        {
            _connection.Open();
            Execute("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
            var version = Convert.ToInt32(Scalar("PRAGMA user_version;"));
            if (version > 1) throw new InvalidOperationException($"Index schema {version} is newer than supported version 1.");
            if (version == 0)
            {
                using var tx = _connection.BeginTransaction();
                Execute("""
                    CREATE TABLE scans(id TEXT PRIMARY KEY, root TEXT NOT NULL, completed_utc TEXT NOT NULL, warnings_json TEXT NOT NULL);
                    CREATE TABLE assets(id TEXT PRIMARY KEY, root TEXT NOT NULL, scan_id TEXT NOT NULL REFERENCES scans(id), fingerprint TEXT NOT NULL, json TEXT NOT NULL);
                    CREATE INDEX ix_assets_root ON assets(root);
                    CREATE TABLE reviews(asset_id TEXT PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE, fingerprint TEXT NOT NULL, json TEXT NOT NULL, reviewed_utc TEXT NOT NULL);
                    CREATE TABLE plans(id TEXT PRIMARY KEY, created_utc TEXT NOT NULL, json TEXT NOT NULL, dry_run INTEGER NOT NULL CHECK(dry_run=1));
                    CREATE TABLE tasks(id TEXT PRIMARY KEY, kind TEXT NOT NULL, state TEXT NOT NULL, detail TEXT NOT NULL, updated_utc TEXT NOT NULL);
                    PRAGMA user_version=1;
                    """, tx);
                tx.Commit();
            }
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public Task SaveScanAsync(ScanResult scan, CancellationToken cancellationToken = default) => SerializedAsync(() =>
    {
        var scanId = Guid.NewGuid().ToString("N");
        using var tx = _connection.BeginTransaction();
        Execute("INSERT INTO scans VALUES($id,$root,$utc,$warnings)", tx, ("$id", scanId), ("$root", scan.Root), ("$utc", Now()), ("$warnings", JsonSerializer.Serialize(scan.Warnings)));
        foreach (var asset in scan.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute("DELETE FROM reviews WHERE asset_id=$id AND fingerprint<>$fingerprint", tx, ("$id", asset.Id), ("$fingerprint", asset.Fingerprint));
            if (Scalar("SELECT json FROM reviews WHERE asset_id=$id", tx, ("$id", asset.Id)) is string priorReviewJson)
            {
                var priorReview = JsonSerializer.Deserialize<ReviewedMetadata>(priorReviewJson);
                if (priorReview?.Provenance.TryGetValue(MediaSnapshot.ProvenanceKey, out var priorSnapshot) == true &&
                    priorSnapshot != MediaSnapshot.Fingerprint(asset))
                    Execute("DELETE FROM reviews WHERE asset_id=$id", tx, ("$id", asset.Id));
            }
            Execute("INSERT INTO assets VALUES($id,$root,$scan,$fingerprint,$json) ON CONFLICT(id) DO UPDATE SET root=excluded.root,scan_id=excluded.scan_id,fingerprint=excluded.fingerprint,json=excluded.json", tx,
                ("$id", asset.Id), ("$root", scan.Root), ("$scan", scanId), ("$fingerprint", asset.Fingerprint), ("$json", JsonSerializer.Serialize(asset)));
        }
        if (scan.IsComplete) Execute("DELETE FROM assets WHERE root=$root AND scan_id<>$scan", tx, ("$root", scan.Root), ("$scan", scanId));
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();
    }, cancellationToken);

    public Task SaveReviewAsync(string assetId, ReviewedMetadata metadata, string expectedFingerprint, CancellationToken cancellationToken = default) => SerializedAsync(() =>
    {
        using var tx = _connection.BeginTransaction();
        var fingerprint = Scalar("SELECT fingerprint FROM assets WHERE id=$id", tx, ("$id", assetId)) as string ?? throw new InvalidOperationException("Asset must be indexed before review.");
        if (string.IsNullOrEmpty(expectedFingerprint) || fingerprint != expectedFingerprint)
            throw new InvalidOperationException("The source was rescanned or changed after selection. Rescan and review again.");
        if (metadata.Provenance.TryGetValue(MediaSnapshot.ProvenanceKey, out var expectedSnapshot))
        {
            var indexedJson = Scalar("SELECT json FROM assets WHERE id=$id", tx, ("$id", assetId)) as string;
            var indexedAsset = indexedJson is null ? null : JsonSerializer.Deserialize<MediaAsset>(indexedJson);
            if (indexedAsset is null || MediaSnapshot.Fingerprint(indexedAsset) != expectedSnapshot)
                throw new InvalidOperationException("The media group changed after selection. Rescan and review again.");
        }
        Execute("INSERT INTO reviews VALUES($id,$fingerprint,$json,$utc) ON CONFLICT(asset_id) DO UPDATE SET fingerprint=excluded.fingerprint,json=excluded.json,reviewed_utc=excluded.reviewed_utc", tx,
            ("$id", assetId), ("$fingerprint", fingerprint), ("$json", JsonSerializer.Serialize(metadata)), ("$utc", Now()));
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit();
    }, cancellationToken);

    /// <summary>Withdraw only the exact review being edited; a delayed editor cannot erase a newer review or scan.</summary>
    public async Task<bool> InvalidateReviewAsync(string assetId, string expectedFingerprint, ReviewedMetadata expectedReview, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        ArgumentNullException.ThrowIfNull(expectedReview);
        var removed = false;
        await SerializedAsync(() =>
        {
            using var tx = _connection.BeginTransaction();
            removed = Execute("""
                DELETE FROM reviews WHERE asset_id=$id AND fingerprint=$fingerprint AND json=$review
                AND EXISTS(SELECT 1 FROM assets WHERE id=$id AND fingerprint=$fingerprint)
                """, tx, ("$id", assetId), ("$fingerprint", expectedFingerprint), ("$review", JsonSerializer.Serialize(expectedReview))) == 1;
            cancellationToken.ThrowIfCancellationRequested();
            tx.Commit();
        }, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public Task SavePlanAsync(OrganizationPlan plan, CancellationToken cancellationToken = default) => SerializedAsync(() =>
        Execute("INSERT INTO plans VALUES($id,$utc,$json,1) ON CONFLICT(id) DO UPDATE SET json=excluded.json", null,
            ("$id", plan.Id), ("$utc", Now()), ("$json", JsonSerializer.Serialize(plan))), cancellationToken);

    public Task RecordTaskAsync(string id, string kind, string state, string detail, CancellationToken cancellationToken = default) => SerializedAsync(() =>
        Execute("INSERT INTO tasks VALUES($id,$kind,$state,$detail,$utc) ON CONFLICT(id) DO UPDATE SET state=excluded.state,detail=excluded.detail,updated_utc=excluded.updated_utc", null,
            ("$id", id), ("$kind", kind), ("$state", state), ("$detail", detail), ("$utc", Now())), cancellationToken);

    public async Task<IReadOnlyDictionary<string, ReviewedMetadata>> LoadReviewsAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ReviewedMetadata>();
        await SerializedAsync(() =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT r.asset_id,r.json,a.json FROM reviews r JOIN assets a ON r.asset_id=a.id AND r.fingerprint=a.fingerprint";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var review = JsonSerializer.Deserialize<ReviewedMetadata>(reader.GetString(1))!;
                if (review.Provenance.TryGetValue(MediaSnapshot.ProvenanceKey, out var expectedSnapshot))
                {
                    var indexedAsset = JsonSerializer.Deserialize<MediaAsset>(reader.GetString(2));
                    if (indexedAsset is null || MediaSnapshot.Fingerprint(indexedAsset) != expectedSnapshot) continue;
                }
                result[reader.GetString(0)] = review;
            }
        }, cancellationToken);
        return result;
    }

    private async Task SerializedAsync(Action action, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); await Task.Run(() => { token.ThrowIfCancellationRequested(); action(); }, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private int Execute(string sql, SqliteTransaction? tx = null, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, tx, parameters);
        return command.ExecuteNonQuery();
    }
    private object? Scalar(string sql, SqliteTransaction? tx = null, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, tx, parameters);
        return command.ExecuteScalar();
    }
    private SqliteCommand Command(string sql, SqliteTransaction? tx, (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand(); command.CommandText = sql; command.Transaction = tx;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; _connection.Dispose(); }
        finally { _gate.Release(); }
    }
}

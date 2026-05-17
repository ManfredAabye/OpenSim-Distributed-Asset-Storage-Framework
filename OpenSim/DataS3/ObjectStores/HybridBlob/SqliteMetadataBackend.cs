using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSim.DataS3.ObjectStores.HybridBlob
{
    /// <summary>
    /// SQLite implementation of IMetadataBackend.
    /// Local, zero-config database for blob metadata.
    /// </summary>
    public sealed class SqliteMetadataBackend : IMetadataBackend
    {
        private readonly string _dbPath;
        private readonly object _initSync = new object();
        private bool _initialized;
        private bool _disposed;

        public string BackendName => "SQLite";

        /// <summary>
        /// Creates SQLite metadata backend.
        /// Connection string format: Data Source=path;Version=3
        /// </summary>
        public SqliteMetadataBackend(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentException("Database path cannot be empty", nameof(dbPath));

            _dbPath = dbPath;
        }

        /// <inheritdoc />
        public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default)
        {
            if (_initialized)
                return;

            lock (_initSync)
            {
                if (_initialized)
                    return;

                try
                {
                    using (var conn = GetConnection())
                    {
                        conn.Open();

                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS blob_metadata (
    key TEXT NOT NULL PRIMARY KEY,
    etag TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    content_type TEXT NOT NULL DEFAULT 'application/octet-stream',
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    custom_metadata TEXT
);

CREATE INDEX IF NOT EXISTS idx_modified_utc ON blob_metadata(modified_utc);
";
                            cmd.ExecuteNonQuery();
                        }
                    }

                    _initialized = true;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to initialize SQLite schema at '{_dbPath}'", ex);
                }
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task UpsertMetadataAsync(BlobMetadata metadata, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using (var conn = GetConnection())
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO blob_metadata (key, etag, size_bytes, content_type, created_utc, modified_utc, custom_metadata)
VALUES (@key, @etag, @sizeBytes, @contentType, @createdUtc, @modifiedUtc, @customMetadata)
ON CONFLICT(key) DO UPDATE SET
    etag = @etag,
    size_bytes = @sizeBytes,
    content_type = @contentType,
    modified_utc = @modifiedUtc,
    custom_metadata = @customMetadata;
";
                    AddParameters(cmd, metadata);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<BlobMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using (var conn = GetConnection())
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT etag, size_bytes, content_type, created_utc, modified_utc, custom_metadata
FROM blob_metadata
WHERE key = @key;
";
                    cmd.Parameters.AddWithValue("@key", key);

                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            return null;

                        return ReadMetadata(key, reader);
                    }
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> MetadataExistsAsync(string key, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using (var conn = GetConnection())
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT 1 FROM blob_metadata WHERE key = @key LIMIT 1;";
                    cmd.Parameters.AddWithValue("@key", key);

                    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return result != null;
                }
            }
        }

        /// <inheritdoc />
        public async Task DeleteMetadataAsync(string key, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using (var conn = GetConnection())
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM blob_metadata WHERE key = @key;";
                    cmd.Parameters.AddWithValue("@key", key);

                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<IEnumerable<string>> GetAllKeysAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            var result = new List<string>();

            using (var conn = GetConnection())
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT key FROM blob_metadata ORDER BY key;";

                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            result.Add(reader.GetString(0));
                    }
                }
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<long> GetBlobCountAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using (var conn = GetConnection())
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM blob_metadata;";
                    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return result != null ? Convert.ToInt64(result) : 0L;
                }
            }
        }

        /// <inheritdoc />
        public async Task<long> GetTotalSizeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using (var conn = GetConnection())
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM blob_metadata;";
                    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return result != null ? Convert.ToInt64(result) : 0L;
                }
            }
        }

        private static BlobMetadata ReadMetadata(string key, IDataRecord reader)
        {
            return new BlobMetadata
            {
                Key = key,
                ETag = reader.GetString(0),
                SizeBytes = reader.GetInt64(1),
                ContentType = reader.GetString(2),
                CreatedUtc = DateTimeOffset.Parse(reader.GetString(3)),
                ModifiedUtc = DateTimeOffset.Parse(reader.GetString(4)),
                CustomMetadata = reader.IsDBNull(5) ? null : reader.GetString(5)
            };
        }

        private static void AddParameters(IDbCommand cmd, BlobMetadata metadata)
        {
            cmd.Parameters.Add(new SQLiteParameter("@key", metadata.Key));
            cmd.Parameters.Add(new SQLiteParameter("@etag", metadata.ETag));
            cmd.Parameters.Add(new SQLiteParameter("@sizeBytes", metadata.SizeBytes));
            cmd.Parameters.Add(new SQLiteParameter("@contentType", metadata.ContentType));
            cmd.Parameters.Add(new SQLiteParameter("@createdUtc", metadata.CreatedUtc.UtcDateTime.ToString("o")));
            cmd.Parameters.Add(new SQLiteParameter("@modifiedUtc", metadata.ModifiedUtc.UtcDateTime.ToString("o")));
            cmd.Parameters.Add(new SQLiteParameter("@customMetadata", metadata.CustomMetadata ?? (object)DBNull.Value));
        }

        private SQLiteConnection GetConnection()
        {
            string connectionString = $"Data Source={_dbPath};Version=3;Pooling=True;Max Pool Size=10;";
            return new SQLiteConnection(connectionString);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            SQLiteConnection.ClearAllPools();
        }
    }
}

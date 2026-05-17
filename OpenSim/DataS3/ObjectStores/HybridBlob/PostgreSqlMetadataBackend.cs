using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace OpenSim.DataS3.ObjectStores.HybridBlob
{
    /// <summary>
    /// PostgreSQL implementation of IMetadataBackend.
    /// Enterprise-grade, highly reliable metadata storage.
    /// Works with: PostgreSQL 10+
    /// </summary>
    public sealed class PostgreSqlMetadataBackend : IMetadataBackend
    {
        private readonly string _connectionString;
        private readonly string _tableName;
        private bool _initialized;
        private bool _disposed;
        private readonly object _initSync = new object();

        public string BackendName => "PostgreSQL";

        /// <summary>
        /// Creates PostgreSQL metadata backend.
        /// Connection string example: Host=localhost;Database=opensim;Username=postgres;Password=password;
        /// </summary>
        public PostgreSqlMetadataBackend(string connectionString, string tableName = "blob_metadata")
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be empty", nameof(connectionString));
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("Table name cannot be empty", nameof(tableName));

            _connectionString = connectionString;
            _tableName = tableName;
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
                    using (var conn = new NpgsqlConnection(_connectionString))
                    {
                        conn.Open();

                        // Create table
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $@"
CREATE TABLE IF NOT EXISTS {_tableName} (
    key UUID PRIMARY KEY,
    etag VARCHAR(67) NOT NULL,
    size_bytes BIGINT NOT NULL,
    content_type VARCHAR(255) NOT NULL DEFAULT 'application/octet-stream',
    created_utc TIMESTAMP NOT NULL,
    modified_utc TIMESTAMP NOT NULL,
    custom_metadata TEXT
);

CREATE INDEX IF NOT EXISTS idx_{_tableName}_modified_utc ON {_tableName}(modified_utc DESC);
CREATE INDEX IF NOT EXISTS idx_{_tableName}_content_type ON {_tableName}(content_type);

COMMENT ON TABLE {_tableName} IS 'HybridBlobObjectStore metadata storage';
COMMENT ON COLUMN {_tableName}.key IS 'Blob UUID';
COMMENT ON COLUMN {_tableName}.etag IS 'SHA-256 hash in quotes';
COMMENT ON COLUMN {_tableName}.size_bytes IS 'Size in bytes';
COMMENT ON COLUMN {_tableName}.created_utc IS 'Creation timestamp';
COMMENT ON COLUMN {_tableName}.modified_utc IS 'Last modified timestamp';
COMMENT ON COLUMN {_tableName}.custom_metadata IS 'JSON serialized metadata';
";
                            cmd.ExecuteNonQuery();
                        }
                    }

                    _initialized = true;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to initialize PostgreSQL schema for table '{_tableName}'", ex);
                }
            }
        }

        /// <inheritdoc />
        public async Task UpsertMetadataAsync(BlobMetadata metadata, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (NpgsqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $@"
INSERT INTO {_tableName} (key, etag, size_bytes, content_type, created_utc, modified_utc, custom_metadata)
VALUES (@key, @etag, @sizeBytes, @contentType, @createdUtc, @modifiedUtc, @customMetadata)
ON CONFLICT (key) DO UPDATE SET
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

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (NpgsqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $@"
SELECT etag, size_bytes, content_type, created_utc, modified_utc, custom_metadata
FROM {_tableName}
WHERE key = @key;
";
                    cmd.Parameters.AddWithValue("@key", Guid.Parse(key));

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

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (NpgsqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT 1 FROM {_tableName} WHERE key = @key LIMIT 1;";
                    cmd.Parameters.AddWithValue("@key", Guid.Parse(key));

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

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (NpgsqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $"DELETE FROM {_tableName} WHERE key = @key;";
                    cmd.Parameters.AddWithValue("@key", Guid.Parse(key));

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

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (NpgsqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT key::TEXT FROM {_tableName} ORDER BY key;";

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

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (NpgsqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT COUNT(*) FROM {_tableName};";
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

            using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (NpgsqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT COALESCE(SUM(size_bytes), 0) FROM {_tableName};";
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
                CreatedUtc = new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                ModifiedUtc = new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
                CustomMetadata = reader.IsDBNull(5) ? null : reader.GetString(5)
            };
        }

        private static void AddParameters(NpgsqlCommand cmd, BlobMetadata metadata)
        {
            cmd.Parameters.AddWithValue("@key", Guid.Parse(metadata.Key));
            cmd.Parameters.AddWithValue("@etag", metadata.ETag);
            cmd.Parameters.AddWithValue("@sizeBytes", metadata.SizeBytes);
            cmd.Parameters.AddWithValue("@contentType", metadata.ContentType);
            cmd.Parameters.AddWithValue("@createdUtc", metadata.CreatedUtc.UtcDateTime);
            cmd.Parameters.AddWithValue("@modifiedUtc", metadata.ModifiedUtc.UtcDateTime);
            cmd.Parameters.AddWithValue("@customMetadata", metadata.CustomMetadata ?? (object)DBNull.Value);
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
            NpgsqlConnection.ClearAllPools();
        }
    }
}

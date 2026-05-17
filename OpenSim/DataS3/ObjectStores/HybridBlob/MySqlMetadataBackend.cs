using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;

namespace OpenSim.DataS3.ObjectStores.HybridBlob
{
    /// <summary>
    /// MySQL/MariaDB implementation of IMetadataBackend.
    /// Production-grade, scalable metadata storage.
    /// Works with: MySQL 5.7+, MariaDB 10.2+
    /// </summary>
    public sealed class MySqlMetadataBackend : IMetadataBackend
    {
        private readonly string _connectionString;
        private readonly string _tableName;
        private bool _initialized;
        private bool _disposed;
        private readonly object _initSync = new object();

        public string BackendName => "MySQL";

        /// <summary>
        /// Creates MySQL metadata backend.
        /// Connection string example: Server=localhost;Database=opensim;Uid=root;Pwd=password;
        /// </summary>
        public MySqlMetadataBackend(string connectionString, string tableName = "blob_metadata")
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
                    using (var conn = new MySqlConnection(_connectionString))
                    {
                        conn.Open();

                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = $@"
CREATE TABLE IF NOT EXISTS {_tableName} (
    `key` VARCHAR(36) NOT NULL PRIMARY KEY COMMENT 'Blob UUID',
    `etag` VARCHAR(67) NOT NULL COMMENT 'SHA-256 hash in quotes',
    `size_bytes` BIGINT NOT NULL COMMENT 'Size in bytes',
    `content_type` VARCHAR(255) NOT NULL DEFAULT 'application/octet-stream',
    `created_utc` DATETIME NOT NULL COMMENT 'Creation timestamp',
    `modified_utc` DATETIME NOT NULL COMMENT 'Last modified timestamp',
    `custom_metadata` LONGTEXT NULL COMMENT 'JSON serialized metadata',
    
    INDEX idx_modified_utc (`modified_utc`),
    INDEX idx_content_type (`content_type`),
    
    ENGINE=InnoDB,
    DEFAULT CHARSET=utf8mb4,
    COLLATE=utf8mb4_unicode_ci,
    ROW_FORMAT=COMPRESSED
) COMMENT='HybridBlobObjectStore metadata storage';
";
                            cmd.ExecuteNonQuery();
                        }
                    }

                    _initialized = true;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to initialize MySQL schema for table '{_tableName}'", ex);
                }
            }
        }

        /// <inheritdoc />
        public async Task UpsertMetadataAsync(BlobMetadata metadata, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            using (var conn = new MySqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (MySqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $@"
INSERT INTO {_tableName} (`key`, `etag`, `size_bytes`, `content_type`, `created_utc`, `modified_utc`, `custom_metadata`)
VALUES (@key, @etag, @sizeBytes, @contentType, @createdUtc, @modifiedUtc, @customMetadata)
ON DUPLICATE KEY UPDATE
    `etag` = @etag,
    `size_bytes` = @sizeBytes,
    `content_type` = @contentType,
    `modified_utc` = @modifiedUtc,
    `custom_metadata` = @customMetadata;
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

            using (var conn = new MySqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (MySqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $@"
SELECT `etag`, `size_bytes`, `content_type`, `created_utc`, `modified_utc`, `custom_metadata`
FROM {_tableName}
WHERE `key` = @key;
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

            using (var conn = new MySqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (MySqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT 1 FROM {_tableName} WHERE `key` = @key LIMIT 1;";
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

            using (var conn = new MySqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (MySqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $"DELETE FROM {_tableName} WHERE `key` = @key;";
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

            using (var conn = new MySqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (MySqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT `key` FROM {_tableName} ORDER BY `key`;";

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

            using (var conn = new MySqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (MySqlCommand)conn.CreateCommand())
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

            using (var conn = new MySqlConnection(_connectionString))
            {
                await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

                using (var cmd = (MySqlCommand)conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT COALESCE(SUM(`size_bytes`), 0) FROM {_tableName};";
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
                CreatedUtc = DateTimeOffset.Parse(reader.GetDateTime(3).ToString("o")),
                ModifiedUtc = DateTimeOffset.Parse(reader.GetDateTime(4).ToString("o")),
                CustomMetadata = reader.IsDBNull(5) ? null : reader.GetString(5)
            };
        }

        private static void AddParameters(MySqlCommand cmd, BlobMetadata metadata)
        {
            cmd.Parameters.AddWithValue("@key", metadata.Key);
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
            MySqlConnection.ClearAllPools();
        }
    }
}

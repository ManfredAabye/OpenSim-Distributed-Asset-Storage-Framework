using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Threading;

namespace OpenSim.DataS3.ObjectStores.HybridBlob
{
    /// <summary>
    /// SQLite metadata database for HybridBlobObjectStore.
    /// Stores blob metadata: size, ETag, content-type, timestamps.
    /// </summary>
    public sealed class HybridBlobMetadataDb : IDisposable
    {
        private readonly string _dbPath;
        private readonly object _dbSync = new object();
        private bool _disposed;

        public HybridBlobMetadataDb(string dbPath)
        {
            _dbPath = dbPath;
        }

        /// <summary>
        /// Initializes the database schema if not already present.
        /// </summary>
        public void InitializeSchema()
        {
            lock (_dbSync)
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
            }
        }

        /// <summary>
        /// Inserts or updates blob metadata.
        /// </summary>
        public void UpsertMetadata(BlobMetadata metadata)
        {
            ThrowIfDisposed();

            lock (_dbSync)
            {
                using (var conn = GetConnection())
                {
                    conn.Open();

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
                        cmd.Parameters.AddWithValue("@key", metadata.Key);
                        cmd.Parameters.AddWithValue("@etag", metadata.ETag);
                        cmd.Parameters.AddWithValue("@sizeBytes", metadata.SizeBytes);
                        cmd.Parameters.AddWithValue("@contentType", metadata.ContentType);
                        cmd.Parameters.AddWithValue("@createdUtc", metadata.CreatedUtc.UtcDateTime.ToString("o"));
                        cmd.Parameters.AddWithValue("@modifiedUtc", metadata.ModifiedUtc.UtcDateTime.ToString("o"));
                        cmd.Parameters.AddWithValue("@customMetadata", metadata.CustomMetadata ?? (object)DBNull.Value);

                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// Retrieves blob metadata by key.
        /// </summary>
        public BlobMetadata? GetMetadata(string key)
        {
            ThrowIfDisposed();

            lock (_dbSync)
            {
                using (var conn = GetConnection())
                {
                    conn.Open();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
SELECT etag, size_bytes, content_type, created_utc, modified_utc, custom_metadata
FROM blob_metadata
WHERE key = @key;
";
                        cmd.Parameters.AddWithValue("@key", key);

                        using (var reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                                return null;

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
                    }
                }
            }
        }

        /// <summary>
        /// Checks if blob metadata exists.
        /// </summary>
        public bool MetadataExists(string key)
        {
            ThrowIfDisposed();

            lock (_dbSync)
            {
                using (var conn = GetConnection())
                {
                    conn.Open();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT 1 FROM blob_metadata WHERE key = @key LIMIT 1;";
                        cmd.Parameters.AddWithValue("@key", key);

                        return cmd.ExecuteScalar() != null;
                    }
                }
            }
        }

        /// <summary>
        /// Deletes blob metadata by key.
        /// </summary>
        public void DeleteMetadata(string key)
        {
            ThrowIfDisposed();

            lock (_dbSync)
            {
                using (var conn = GetConnection())
                {
                    conn.Open();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "DELETE FROM blob_metadata WHERE key = @key;";
                        cmd.Parameters.AddWithValue("@key", key);

                        cmd.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// Gets all blob keys (for maintenance/migration tasks).
        /// </summary>
        public IEnumerable<string> GetAllKeys()
        {
            ThrowIfDisposed();

            var result = new List<string>();

            lock (_dbSync)
            {
                using (var conn = GetConnection())
                {
                    conn.Open();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT key FROM blob_metadata ORDER BY key;";

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                                result.Add(reader.GetString(0));
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gets blob count.
        /// </summary>
        public long GetBlobCount()
        {
            ThrowIfDisposed();

            lock (_dbSync)
            {
                using (var conn = GetConnection())
                {
                    conn.Open();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM blob_metadata;";
                        var result = cmd.ExecuteScalar();
                        return result != null ? Convert.ToInt64(result) : 0L;
                    }
                }
            }
        }

        /// <summary>
        /// Gets total blob size in bytes.
        /// </summary>
        public long GetTotalSize()
        {
            ThrowIfDisposed();

            lock (_dbSync)
            {
                using (var conn = GetConnection())
                {
                    conn.Open();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT SUM(size_bytes) FROM blob_metadata;";
                        var result = cmd.ExecuteScalar();
                        return result != null && result != DBNull.Value ? Convert.ToInt64(result) : 0L;
                    }
                }
            }
        }

        /// <summary>
        /// Creates a connection to the SQLite database.
        /// </summary>
        private SQLiteConnection GetConnection()
        {
            string connectionString = $"Data Source={_dbPath};Version=3;Pooling=True;Max Pool Size=10;";
            return new SQLiteConnection(connectionString);
        }

        /// <summary>
        /// Throws ObjectDisposedException if disposed.
        /// </summary>
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenSim.DataS3.Interfaces;

namespace OpenSim.DataS3.ObjectStores.HybridBlob
{
    /// <summary>
    /// Hybrid S3-compatible blob store: Filesystem for blobs + pluggable database for metadata.
    /// Supports SQLite, MySQL, MariaDB, PostgreSQL - stateless and prevents Assets/FSAssets from breaking.
    /// </summary>
    public sealed class HybridBlobObjectStore : IObjectStore, IDisposable
    {
        private readonly string _storagePath;
        private readonly string _databaseType;
        private readonly string _connectionString;
        private readonly string? _tableName;
        private readonly bool _autoCreatePath;
        private IMetadataBackend? _metadataBackend;
        private readonly object _initSync = new object();
        private bool _initialized;
        private bool _disposed;

        /// <summary>
        /// Connection string keys:
        /// - HybridBlobStoragePath: root directory for blob storage (default: ./data/hybrid_blobs)
        /// - HybridBlobDatabaseType: metadata database type - SQLite, MySQL, MariaDB, PostgreSQL (default: SQLite)
        /// - HybridBlobConnectionString: database-specific connection string (default: uses StoragePath)
        /// - HybridBlobTableName: metadata table name (default: blob_metadata)
        /// - HybridBlobAutoCreatePath: create directories if missing (default: true)
        /// 
        /// Examples:
        /// SQLite: HybridBlobStoragePath=/data/blobs;HybridBlobDatabaseType=SQLite
        /// MySQL: HybridBlobStoragePath=/data/blobs;HybridBlobDatabaseType=MySQL;HybridBlobConnectionString=Server=localhost;Database=opensim;Uid=root;Pwd=pass;
        /// PostgreSQL: HybridBlobStoragePath=/data/blobs;HybridBlobDatabaseType=PostgreSQL;HybridBlobConnectionString=Host=localhost;Database=opensim;Username=opensim;Password=pass;
        /// </summary>
        public HybridBlobObjectStore(string? connectionString = null)
        {
            var settings = ParseConnectionString(connectionString);

            _storagePath = settings["HybridBlobStoragePath"] ?? "./data/hybrid_blobs";
            _databaseType = settings["HybridBlobDatabaseType"] ?? "SQLite";
            _tableName = settings.GetValueOrDefault("HybridBlobTableName");
            _autoCreatePath = bool.TryParse(settings.GetValueOrDefault("HybridBlobAutoCreatePath"), out var val) ? val : true;

            // Default connection string based on database type
            if (settings.TryGetValue("HybridBlobConnectionString", out var explicitConnStr))
            {
                _connectionString = explicitConnStr;
            }
            else
            {
                // Auto-generate based on type
                _connectionString = _databaseType.ToLowerInvariant() switch
                {
                    "sqlite" => Path.Combine(_storagePath, "metadata.db"),
                    "mysql" or "mariadb" => "Server=localhost;Database=opensim;Uid=opensim;Pwd=password;Allow Zero DateTime=true;",
                    "postgresql" or "postgres" => "Host=localhost;Database=opensim;Username=opensim;Password=password;",
                    _ => throw new InvalidOperationException($"Unsupported database type: {_databaseType}")
                };
            }
        }

        /// <summary>
        /// Initialize storage paths and metadata database backend.
        /// </summary>
        private void EnsureInitialized()
        {
            if (_initialized)
                return;

            lock (_initSync)
            {
                if (_initialized)
                    return;

                try
                {
                    if (_autoCreatePath)
                    {
                        Directory.CreateDirectory(_storagePath);
                    }

                    // Create metadata backend
                    if (_tableName != null)
                        _metadataBackend = MetadataBackendFactory.Create(_databaseType, _connectionString, _tableName);
                    else
                        _metadataBackend = MetadataBackendFactory.Create(_databaseType, _connectionString);

                    // Initialize schema synchronously
                    _metadataBackend.InitializeSchemaAsync().Wait();
                    _initialized = true;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to initialize HybridBlobObjectStore (Type: {_databaseType}, Path: {_storagePath})", ex);
                }
            }
        }

        /// <inheritdoc />
        public async Task<Stream> GetAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();

            string filePath = GetFilePath(key);

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Blob not found: {key}", filePath);

            // Read file into memory stream to detach from disk
            MemoryStream ms = new MemoryStream();
            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await fs.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
                }
                ms.Position = 0;
                return ms;
            }
            catch (Exception)
            {
                ms.Dispose();
                throw;
            }
        }

        /// <inheritdoc />
        public async Task PutAsync(
            string key,
            Stream data,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();

            string filePath = GetFilePath(key);
            string? directory = Path.GetDirectoryName(filePath);

            if (directory != null && !Directory.Exists(directory))
            {
                if (_autoCreatePath)
                    Directory.CreateDirectory(directory);
                else
                    throw new DirectoryNotFoundException($"Directory does not exist: {directory}");
            }

            // Calculate ETag (SHA-256 hash) and size while writing
            string etag;
            long sizeBytes;

            using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var sha256 = SHA256.Create())
                {
                    using (var hashStream = new CryptoStream(fs, sha256, CryptoStreamMode.Write))
                    {
                        await data.CopyToAsync(hashStream, 81920, cancellationToken).ConfigureAwait(false);
                    }

                    sizeBytes = fs.Length;
                    etag = "\"" + BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLowerInvariant() + "\"";
                }
            }

            // Store metadata
            string contentType = ExtractMetadataValue(metadata, "Content-Type") ?? "application/octet-stream";
            var now = DateTimeOffset.UtcNow;

            var blobMeta = new BlobMetadata
            {
                Key = key,
                ETag = etag,
                SizeBytes = sizeBytes,
                ContentType = contentType,
                CreatedUtc = now,
                ModifiedUtc = now,
                CustomMetadata = SerializeMetadata(metadata)
            };

            await _metadataBackend!.UpsertMetadataAsync(blobMeta, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();

            string filePath = GetFilePath(key);

            if (File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    throw new IOException($"Failed to delete blob file: {filePath}", ex);
                }
            }

            // Delete metadata
            await _metadataBackend!.DeleteMetadataAsync(key, cancellationToken).ConfigureAwait(false);

            // Cleanup empty directories
            string? dir = Path.GetDirectoryName(filePath);
            if (dir != null && dir != _storagePath)
            {
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0)
                        Directory.Delete(dir);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();

            string filePath = GetFilePath(key);
            bool fileExists = File.Exists(filePath);
            bool metadataExists = await _metadataBackend!.MetadataExistsAsync(key, cancellationToken).ConfigureAwait(false);

            // Both should be in sync, but trust filesystem
            return fileExists;
        }

        /// <inheritdoc />
        public async Task<ObjectStat> StatAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureInitialized();

            BlobMetadata? blobMeta = await _metadataBackend!.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
            if (blobMeta == null)
                throw new FileNotFoundException($"Blob metadata not found: {key}");

            string filePath = GetFilePath(key);
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Blob file not found: {key}");

            return new ObjectStat
            {
                SizeBytes = blobMeta.SizeBytes,
                ETag = blobMeta.ETag,
                ContentType = blobMeta.ContentType
            };
        }

        /// <summary>
        /// Gets the full filesystem path for a blob key.
        /// Uses UUID-based directory sharding to avoid filesystem limits.
        /// </summary>
        private string GetFilePath(string key)
        {
            // Validate key format (should be UUID)
            if (string.IsNullOrWhiteSpace(key) || key.Length < 36)
                throw new ArgumentException($"Invalid blob key format: {key}");

            // Shard by first 2 chars for directory distribution
            string shard = key.Substring(0, 2);
            return Path.Combine(_storagePath, shard, key + ".blob");
        }

        /// <summary>
        /// Parses connection string into key-value dictionary.
        /// Format: Key1=Value1;Key2=Value2
        /// </summary>
        private static Dictionary<string, string> ParseConnectionString(string? connectionString)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(connectionString))
                return result;

            foreach (string pair in connectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2)
                    result[kv[0].Trim()] = kv[1].Trim();
            }

            return result;
        }

        /// <summary>
        /// Extracts a metadata value from the metadata dictionary.
        /// </summary>
        private static string? ExtractMetadataValue(IReadOnlyDictionary<string, string>? metadata, string key)
        {
            if (metadata == null)
                return null;

            if (metadata.TryGetValue(key, out var value))
                return value;

            // Try case-insensitive lookup
            foreach (var kvp in metadata)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }

            return null;
        }

        /// <summary>
        /// Serializes metadata dictionary to JSON string for storage.
        /// </summary>
        private static string? SerializeMetadata(IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata == null || metadata.Count == 0)
                return null;

            try
            {
                return JsonSerializer.Serialize(metadata);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            lock (_initSync)
            {
                _metadataBackend?.Dispose();
                _disposed = true;
            }
        }
    }
}

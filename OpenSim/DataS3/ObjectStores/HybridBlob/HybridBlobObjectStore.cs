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
        private Exception? _initializationException;
        private bool _disposed;

        /// <summary>
        /// Connection string keys:
        /// - HybridBlobStoragePath: root directory for blob storage (default: ./data/hybrid_blobs)
        /// - HybridBlobDatabaseType: metadata database type - SQLite, MySQL, MariaDB, PostgreSQL (default: SQLite)
        /// - HybridBlobConnectionString: database-specific connection string
        /// - ConnectionString: fallback to OpenSim/global DB connection string
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

            _connectionString = ResolveMetadataConnectionString(connectionString, settings, _databaseType, _storagePath);
        }

        /// <summary>
        /// Initialize storage paths and metadata database backend.
        /// </summary>
        private void EnsureInitialized()
        {
            if (_initialized)
                return;

            if (_initializationException != null)
                throw new InvalidOperationException(
                    $"HybridBlobObjectStore (Type: {_databaseType}, Path: {_storagePath}) failed to initialize earlier. Fix the database connection configuration and restart.",
                    _initializationException);

            lock (_initSync)
            {
                if (_initialized)
                    return;

                if (_initializationException != null)
                    throw new InvalidOperationException(
                        $"HybridBlobObjectStore (Type: {_databaseType}, Path: {_storagePath}) failed to initialize earlier. Fix the database connection configuration and restart.",
                        _initializationException);

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
                    _initializationException = ex;
                    _metadataBackend?.Dispose();
                    _metadataBackend = null;
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
                    using (var hashStream = new CryptoStream(fs, sha256, CryptoStreamMode.Write, leaveOpen: true))
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
        /// Resolves metadata DB connection string with precedence:
        /// 1) HybridBlobConnectionString
        /// 2) OpenSim/global ConnectionString
        /// 3) Database-type defaults
        ///
        /// Also reconstructs common DB tokens when values were split by simple ';' parsing.
        /// </summary>
        private static string ResolveMetadataConnectionString(
            string? rawInput,
            IReadOnlyDictionary<string, string> settings,
            string databaseType,
            string storagePath)
        {
            // 1) Explicit HybridBlobConnectionString has highest precedence.
            if (settings.TryGetValue("HybridBlobConnectionString", out var hybridConn)
                && !string.IsNullOrWhiteSpace(hybridConn))
                return AppendKnownDbSegments(hybridConn, settings);

            // 2) Full ConnectionString key that looks like a DB connection string.
            if (settings.TryGetValue("ConnectionString", out var openSimConn)
                && !string.IsNullOrWhiteSpace(openSimConn)
                && LooksLikeDirectDbConnectionString(openSimConn))
                return AppendKnownDbSegments(openSimConn, settings);

            // 3) Reconstruct from individual DB tokens that were parsed out of the OpenSim
            //    connection string (e.g. Data Source=, Database=, Password=, User ID=, ...).
            //    This covers the common case where [AssetService]/[DatabaseService].ConnectionString
            //    was merged into the provider connection string without an explicit HybridBlob* prefix.
            string? reconstructed = TryReconstructDbConnectionFromTokens(settings);
            if (!string.IsNullOrWhiteSpace(reconstructed))
                return reconstructed!;

            // 4) Raw input without HybridBlob keys that looks like a direct DB connection string.
            if (!string.IsNullOrWhiteSpace(rawInput) && LooksLikeDirectDbConnectionString(rawInput))
                return rawInput.Trim();

            // 5) Database-type defaults (last resort — should never be reached in a configured system).
            return databaseType.ToLowerInvariant() switch
            {
                "sqlite" => Path.Combine(storagePath, "metadata.db"),
                "mysql" or "mariadb" => "Server=localhost;Database=opensim;Uid=opensim;Pwd=password;Allow Zero DateTime=true;",
                "postgresql" or "postgres" => "Host=localhost;Database=opensim;Username=opensim;Password=password;",
                _ => throw new InvalidOperationException($"Unsupported database type: {databaseType}")
            };
        }

        /// <summary>
        /// Attempts to reconstruct a usable DB connection string from individual parsed tokens
        /// (e.g. after OpenSim's raw ConnectionString has been split on ';' into a settings dict).
        /// Returns null when not enough information is present.
        /// </summary>
        private static string? TryReconstructDbConnectionFromTokens(IReadOnlyDictionary<string, string> settings)
        {
            string? database = GetFirstTokenValue(settings, "Database");
            string? password  = GetFirstTokenValue(settings, "Password", "Pwd");
            if (string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(password))
                return null;

            var sb = new System.Text.StringBuilder();

            // MySQL / SQLite via "Data Source=" (OpenSim default for MySQL)
            string? dataSource = GetFirstTokenValue(settings, "Data Source");
            if (dataSource != null)
            {
                string? userId = GetFirstTokenValue(settings, "User ID", "User Id", "Uid", "Username");
                sb.Append("Data Source=").Append(dataSource).Append(';');
                sb.Append("Database=").Append(database).Append(';');
                if (userId != null) sb.Append("User ID=").Append(userId).Append(';');
                sb.Append("Password=").Append(password).Append(';');
                AppendTokenIfPresent(sb, settings, "Old Guids");
                AppendTokenIfPresent(sb, settings, "SslMode");
                AppendTokenIfPresent(sb, settings, "Allow Zero DateTime");
                AppendTokenIfPresent(sb, settings, "Pooling");
                AppendTokenIfPresent(sb, settings, "Port");
                return sb.ToString();
            }

            // MySQL / MariaDB via "Server="
            string? server = GetFirstTokenValue(settings, "Server");
            if (server != null)
            {
                string? userId = GetFirstTokenValue(settings, "Uid", "User Id", "User ID", "Username");
                sb.Append("Server=").Append(server).Append(';');
                sb.Append("Database=").Append(database).Append(';');
                if (userId != null) sb.Append("Uid=").Append(userId).Append(';');
                sb.Append("Pwd=").Append(password).Append(';');
                AppendTokenIfPresent(sb, settings, "SslMode");
                AppendTokenIfPresent(sb, settings, "Allow Zero DateTime");
                AppendTokenIfPresent(sb, settings, "Old Guids");
                AppendTokenIfPresent(sb, settings, "Port");
                return sb.ToString();
            }

            // PostgreSQL via "Host="
            string? host = GetFirstTokenValue(settings, "Host");
            if (host != null)
            {
                string? username = GetFirstTokenValue(settings, "Username", "User ID", "User Id", "Uid");
                sb.Append("Host=").Append(host).Append(';');
                sb.Append("Database=").Append(database).Append(';');
                if (username != null) sb.Append("Username=").Append(username).Append(';');
                sb.Append("Password=").Append(password).Append(';');
                AppendTokenIfPresent(sb, settings, "Port");
                AppendTokenIfPresent(sb, settings, "SslMode");
                return sb.ToString();
            }

            return null;
        }

        private static string? GetFirstTokenValue(IReadOnlyDictionary<string, string> settings, params string[] keys)
        {
            foreach (string key in keys)
                if (settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v;
            return null;
        }

        private static void AppendTokenIfPresent(System.Text.StringBuilder sb, IReadOnlyDictionary<string, string> settings, string key)
        {
            if (settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                sb.Append(key).Append('=').Append(v).Append(';');
        }

        private static string AppendKnownDbSegments(string baseConn, IReadOnlyDictionary<string, string> settings)
        {
            var knownKeys = new[]
            {
                "Server",
                "Host",
                "Port",
                "Database",
                "Uid",
                "User Id",
                "Username",
                "Pwd",
                "Password",
                "Data Source",
                "Initial Catalog",
                "Integrated Security",
                "SslMode",
                "sslmode",
                "Allow Zero DateTime",
                "Pooling",
                "Min Pool Size",
                "Max Pool Size",
                "Timeout",
                "Command Timeout",
                "Old Guids",
                "Version"
            };

            var normalized = baseConn.Trim();
            if (!normalized.EndsWith(";", StringComparison.Ordinal))
                normalized += ";";

            foreach (var key in knownKeys)
            {
                if (!settings.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                    continue;

                if (normalized.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                normalized += key + "=" + value + ";";
            }

            return normalized;
        }

        private static bool LooksLikeDirectDbConnectionString(string value)
        {
            return value.IndexOf("Server=", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Host=", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Data Source=", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Initial Catalog=", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Database=", StringComparison.OrdinalIgnoreCase) >= 0;
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

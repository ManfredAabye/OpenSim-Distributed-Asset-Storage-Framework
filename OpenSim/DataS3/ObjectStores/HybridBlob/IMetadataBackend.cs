using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSim.DataS3.ObjectStores.HybridBlob
{
    /// <summary>
    /// Abstraction for metadata storage backend (SQLite, MySQL, PostgreSQL, etc).
    /// Allows HybridBlobObjectStore to work with any database system.
    /// </summary>
    public interface IMetadataBackend : IDisposable
    {
        /// <summary>
        /// Initializes database schema (creates tables if needed).
        /// </summary>
        Task InitializeSchemaAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts or updates blob metadata.
        /// </summary>
        Task UpsertMetadataAsync(BlobMetadata metadata, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves blob metadata by key.
        /// Returns null if not found.
        /// </summary>
        Task<BlobMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if blob metadata exists.
        /// </summary>
        Task<bool> MetadataExistsAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes blob metadata by key.
        /// </summary>
        Task DeleteMetadataAsync(string key, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets all blob keys (for maintenance/migration).
        /// </summary>
        Task<IEnumerable<string>> GetAllKeysAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets total number of stored blobs.
        /// </summary>
        Task<long> GetBlobCountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets total size of all blobs in bytes.
        /// </summary>
        Task<long> GetTotalSizeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets metadata backend name (e.g. "SQLite", "MySQL", "PostgreSQL").
        /// </summary>
        string BackendName { get; }
    }

    /// <summary>
    /// Blob metadata model (database-agnostic).
    /// </summary>
    public class BlobMetadata
    {
        /// <summary>Blob UUID key.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>ETag (SHA-256 hash wrapped in quotes).</summary>
        public string ETag { get; set; } = string.Empty;

        /// <summary>Size in bytes.</summary>
        public long SizeBytes { get; set; }

        /// <summary>Content-Type MIME type.</summary>
        public string ContentType { get; set; } = "application/octet-stream";

        /// <summary>Creation timestamp (UTC).</summary>
        public DateTimeOffset CreatedUtc { get; set; }

        /// <summary>Last modified timestamp (UTC).</summary>
        public DateTimeOffset ModifiedUtc { get; set; }

        /// <summary>Serialized custom metadata (JSON).</summary>
        public string? CustomMetadata { get; set; }
    }
}

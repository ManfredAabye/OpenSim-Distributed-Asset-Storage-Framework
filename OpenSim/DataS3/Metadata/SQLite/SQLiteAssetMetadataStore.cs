using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenMetaverse;
using OpenSim.DataS3.Interfaces;
using OpenSim.DataS3.Models;
using OpenSim.DataS3.Sql;

namespace OpenSim.DataS3.Metadata.SQLite
{
    /// <summary>
    /// SQLite-backed metadata store for DataS3.
    /// </summary>
    public sealed class SQLiteAssetMetadataStore : IAssetMetadataStore
    {
        private const string DefaultConnectionString = "URI=file:DataS3Metadata.db";
        private const string SQLiteProviderAlias = "SQLite";

        private readonly string _connectionString;
        private readonly ISqlConnectionFactory _connectionFactory;

        /// <summary>
        /// Creates a SQLite metadata store.
        /// </summary>
        /// <param name="connectionString">SQLite connection string.</param>
        public SQLiteAssetMetadataStore(string? connectionString = null, ISqlConnectionFactory? connectionFactory = null)
        {
            _connectionString = string.IsNullOrWhiteSpace(connectionString)
                ? DefaultConnectionString
                : connectionString;

            _connectionFactory = connectionFactory ?? new DefaultSqlConnectionFactory();
            EnsureSchema();
        }

        /// <inheritdoc />
        public Task<AssetMetadataRecord?> GetAsync(UUID id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using DbConnection conn = CreateConnection();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT asset_id, content_hash, asset_type, name, description, creator_id, flags, " +
                "content_type, size_bytes, storage_provider, storage_bucket, storage_key, compression, checksum " +
                "FROM datas3_metadata WHERE asset_id = @id";
            AddParameter(cmd, "@id", id.ToString());

            using DbDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                return Task.FromResult<AssetMetadataRecord?>(null);

            return Task.FromResult<AssetMetadataRecord?>(ReadRecord(reader));
        }

        /// <inheritdoc />
        public Task StoreAsync(AssetMetadataRecord metadata, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            using DbConnection conn = CreateConnection();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO datas3_metadata (asset_id, content_hash, asset_type, name, description, creator_id, flags, " +
                "content_type, size_bytes, storage_provider, storage_bucket, storage_key, compression, checksum) " +
                "VALUES (@asset_id, @content_hash, @asset_type, @name, @description, @creator_id, @flags, " +
                "@content_type, @size_bytes, @storage_provider, @storage_bucket, @storage_key, @compression, @checksum) " +
                "ON CONFLICT(asset_id) DO UPDATE SET " +
                "content_hash=@content_hash, asset_type=@asset_type, name=@name, description=@description, creator_id=@creator_id, " +
                "flags=@flags, content_type=@content_type, size_bytes=@size_bytes, storage_provider=@storage_provider, " +
                "storage_bucket=@storage_bucket, storage_key=@storage_key, compression=@compression, checksum=@checksum";

            AddParameter(cmd, "@asset_id", metadata.AssetId.ToString());
            AddParameter(cmd, "@content_hash", metadata.ContentHash);
            AddParameter(cmd, "@asset_type", metadata.AssetType);
            AddParameter(cmd, "@name", metadata.Name);
            AddParameter(cmd, "@description", metadata.Description);
            AddParameter(cmd, "@creator_id", metadata.CreatorId);
            AddParameter(cmd, "@flags", metadata.Flags);
            AddParameter(cmd, "@content_type", metadata.ContentType);
            AddParameter(cmd, "@size_bytes", metadata.SizeBytes);
            AddParameter(cmd, "@storage_provider", metadata.StorageProvider);
            AddParameter(cmd, "@storage_bucket", metadata.StorageBucket);
            AddParameter(cmd, "@storage_key", metadata.StorageKey);
            AddParameter(cmd, "@compression", (object?)metadata.Compression ?? DBNull.Value);
            AddParameter(cmd, "@checksum", (object?)metadata.Checksum ?? DBNull.Value);

            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task DeleteAsync(UUID id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using DbConnection conn = CreateConnection();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM datas3_metadata WHERE asset_id = @id";
            AddParameter(cmd, "@id", id.ToString());
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<bool> ExistsAsync(UUID id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using DbConnection conn = CreateConnection();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM datas3_metadata WHERE asset_id = @id LIMIT 1";
            AddParameter(cmd, "@id", id.ToString());
            object? value = cmd.ExecuteScalar();
            return Task.FromResult(value != null && value != DBNull.Value);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AssetMetadataRecord>> ListAsync(int start, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (start < 0)
                start = 0;
            if (count <= 0)
                return Task.FromResult<IReadOnlyList<AssetMetadataRecord>>(Array.Empty<AssetMetadataRecord>());

            using DbConnection conn = CreateConnection();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT asset_id, content_hash, asset_type, name, description, creator_id, flags, " +
                "content_type, size_bytes, storage_provider, storage_bucket, storage_key, compression, checksum " +
                "FROM datas3_metadata ORDER BY asset_id LIMIT @count OFFSET @start";
            AddParameter(cmd, "@count", count);
            AddParameter(cmd, "@start", start);

            List<AssetMetadataRecord> rows = new List<AssetMetadataRecord>();
            using DbDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(ReadRecord(reader));

            return Task.FromResult<IReadOnlyList<AssetMetadataRecord>>(rows);
        }

        /// <inheritdoc />
        public Task<bool> HasOtherReferencesAsync(string storageKey, UUID assetId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using DbConnection conn = CreateConnection();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM datas3_metadata WHERE storage_key = @storage_key AND asset_id <> @asset_id LIMIT 1";
            AddParameter(cmd, "@storage_key", storageKey);
            AddParameter(cmd, "@asset_id", assetId.ToString());

            object? value = cmd.ExecuteScalar();
            return Task.FromResult(value != null && value != DBNull.Value);
        }

        private void EnsureSchema()
        {
            using DbConnection conn = CreateConnection();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS datas3_metadata (" +
                "asset_id TEXT PRIMARY KEY, " +
                "content_hash TEXT NOT NULL, " +
                "asset_type INTEGER NOT NULL, " +
                "name TEXT NOT NULL, " +
                "description TEXT NOT NULL, " +
                "creator_id TEXT NOT NULL, " +
                "flags INTEGER NOT NULL, " +
                "content_type TEXT NOT NULL, " +
                "size_bytes INTEGER NOT NULL, " +
                "storage_provider TEXT NOT NULL, " +
                "storage_bucket TEXT NOT NULL, " +
                "storage_key TEXT NOT NULL, " +
                "compression TEXT NULL, " +
                "checksum TEXT NULL" +
                ")";
            cmd.ExecuteNonQuery();

            using DbCommand ixCmd = conn.CreateCommand();
            ixCmd.CommandText = "CREATE INDEX IF NOT EXISTS ix_datas3_metadata_storage_key ON datas3_metadata(storage_key)";
            ixCmd.ExecuteNonQuery();
        }

        private DbConnection CreateConnection()
        {
            return _connectionFactory.CreateOpenConnection(SQLiteProviderAlias, _connectionString);
        }

        private static void AddParameter(DbCommand cmd, string name, object? value)
        {
            DbParameter p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        private static AssetMetadataRecord ReadRecord(IDataRecord row)
        {
            return new AssetMetadataRecord
            {
                AssetId = UUID.Parse(row["asset_id"].ToString() ?? string.Empty),
                ContentHash = ReadString(row, "content_hash"),
                AssetType = Convert.ToInt32(row["asset_type"]),
                Name = ReadString(row, "name"),
                Description = ReadString(row, "description"),
                CreatorId = ReadString(row, "creator_id"),
                Flags = Convert.ToInt32(row["flags"]),
                ContentType = ReadString(row, "content_type"),
                SizeBytes = Convert.ToInt64(row["size_bytes"]),
                StorageProvider = ReadString(row, "storage_provider"),
                StorageBucket = ReadString(row, "storage_bucket"),
                StorageKey = ReadString(row, "storage_key"),
                Compression = ReadNullableString(row, "compression"),
                Checksum = ReadNullableString(row, "checksum")
            };
        }

        private static string ReadString(IDataRecord row, string field)
        {
            int idx = row.GetOrdinal(field);
            return row.IsDBNull(idx) ? string.Empty : row.GetString(idx);
        }

        private static string? ReadNullableString(IDataRecord row, string field)
        {
            int idx = row.GetOrdinal(field);
            return row.IsDBNull(idx) ? null : row.GetString(idx);
        }
    }
}

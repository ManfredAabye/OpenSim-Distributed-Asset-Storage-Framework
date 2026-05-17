using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using OpenMetaverse;
using OpenSim.DataS3.Sql;
using OpenSim.Framework;

namespace OpenSim.DataS3.Compatibility
{
    /// <summary>
    /// Reads legacy assets from existing SQL stores for migration fallback paths.
    /// </summary>
    public sealed class LegacyAssetFallbackReader
    {
        private readonly string _provider;
        private readonly string _connectionString;
        private readonly ISqlConnectionFactory _connectionFactory;

        /// <summary>
        /// Creates a fallback reader instance.
        /// </summary>
        /// <param name="provider">Provider name, for example MySQL, PGSQL, SQLite.</param>
        /// <param name="connectionString">Provider connection string.</param>
        public LegacyAssetFallbackReader(string provider, string connectionString, ISqlConnectionFactory? connectionFactory = null)
        {
            _provider = provider ?? string.Empty;
            _connectionString = connectionString ?? string.Empty;
            _connectionFactory = connectionFactory ?? new DefaultSqlConnectionFactory();
        }

        /// <summary>
        /// Attempts to read one legacy asset by UUID from the legacy SQL <c>assets</c> table.
        /// </summary>
        /// <param name="assetId">Asset identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Asset if found; otherwise null.</returns>
        public AssetBase? TryGetAsset(UUID assetId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(_connectionString))
                return null;

            using DbConnection conn = CreateConnection();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT name, description, assetType, data, asset_flags, CreatorID " +
                "FROM assets WHERE id = @id LIMIT 1";
            AddParameter(cmd, "@id", assetId.ToString());

            using DbDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            string name = ReadString(reader, "name");
            string description = ReadString(reader, "description");
            int type = ReadInt(reader, "assetType");
            int flags = ReadInt(reader, "asset_flags");
            string creatorId = ReadString(reader, "CreatorID");
            byte[] data = ReadBytes(reader, "data");

            return new AssetBase(assetId, name, (sbyte)type, creatorId)
            {
                Description = description,
                Data = data,
                Flags = (AssetFlags)flags
            };
        }

        /// <summary>
        /// Reads one legacy asset page from the existing SQL <c>assets</c> table.
        /// </summary>
        /// <param name="offset">Zero-based offset.</param>
        /// <param name="limit">Maximum number of rows.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Legacy assets for the requested page.</returns>
        public IReadOnlyList<AssetBase> GetAssetBatch(int offset, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(_connectionString))
                return Array.Empty<AssetBase>();

            if (offset < 0)
                offset = 0;
            if (limit <= 0)
                return Array.Empty<AssetBase>();

            using DbConnection conn = CreateConnection();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, name, description, assetType, data, asset_flags, CreatorID " +
                "FROM assets ORDER BY id LIMIT @limit OFFSET @offset";
            AddParameter(cmd, "@limit", limit);
            AddParameter(cmd, "@offset", offset);

            List<AssetBase> rows = new List<AssetBase>();
            using DbDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                string rawId = ReadString(reader, "id");
                if (!UUID.TryParse(rawId, out UUID assetId))
                    continue;

                string name = ReadString(reader, "name");
                string description = ReadString(reader, "description");
                int type = ReadInt(reader, "assetType");
                int flags = ReadInt(reader, "asset_flags");
                string creatorId = ReadString(reader, "CreatorID");
                byte[] data = ReadBytes(reader, "data");

                rows.Add(new AssetBase(assetId, name, (sbyte)type, creatorId)
                {
                    Description = description,
                    Data = data,
                    Flags = (AssetFlags)flags
                });
            }

            return rows;
        }

        /// <summary>
        /// Stores or updates one legacy asset row in SQL for Dual-Write migration phases.
        /// </summary>
        /// <param name="asset">Asset to persist.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public void UpsertAsset(AssetBase asset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (asset == null)
                throw new ArgumentNullException(nameof(asset));

            if (string.IsNullOrWhiteSpace(_connectionString))
                return;

            using DbConnection conn = CreateConnection();

            // Portable update-then-insert strategy across MySQL/PGSQL/SQLite.
            using DbCommand update = conn.CreateCommand();
            update.CommandText =
                "UPDATE assets SET name=@name, description=@description, assetType=@assetType, local=@local, temporary=@temporary, " +
                "data=@data, access_time=@access_time, asset_flags=@asset_flags, CreatorID=@creator_id WHERE id=@id";
            AddAssetParameters(update, asset, includeCreateTime: false);
            int rows = update.ExecuteNonQuery();
            if (rows > 0)
                return;

            using DbCommand insert = conn.CreateCommand();
            insert.CommandText =
                "INSERT INTO assets (name, description, assetType, local, temporary, data, id, create_time, access_time, asset_flags, CreatorID) " +
                "VALUES (@name, @description, @assetType, @local, @temporary, @data, @id, @create_time, @access_time, @asset_flags, @creator_id)";
            AddAssetParameters(insert, asset, includeCreateTime: true);
            insert.ExecuteNonQuery();
        }

        /// <summary>
        /// Deletes one legacy asset row by UUID.
        /// </summary>
        /// <param name="assetId">Asset identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public void DeleteAsset(UUID assetId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(_connectionString))
                return;

            using DbConnection conn = CreateConnection();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM assets WHERE id = @id";
            AddParameter(cmd, "@id", assetId.ToString());
            cmd.ExecuteNonQuery();
        }

        private DbConnection CreateConnection()
        {
            return _connectionFactory.CreateOpenConnection(_provider, _connectionString);
        }

        private static void AddParameter(DbCommand cmd, string name, object value)
        {
            DbParameter p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        private static void AddAssetParameters(DbCommand cmd, AssetBase asset, bool includeCreateTime)
        {
            int now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() > int.MaxValue
                ? int.MaxValue
                : (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            AddParameter(cmd, "@id", asset.FullID.ToString());
            AddParameter(cmd, "@name", asset.Name ?? string.Empty);
            AddParameter(cmd, "@description", asset.Description ?? string.Empty);
            AddParameter(cmd, "@assetType", asset.Type);
            AddParameter(cmd, "@local", asset.Local ? 1 : 0);
            AddParameter(cmd, "@temporary", asset.Temporary ? 1 : 0);
            AddParameter(cmd, "@data", asset.Data ?? Array.Empty<byte>());
            AddParameter(cmd, "@access_time", now);
            AddParameter(cmd, "@asset_flags", (int)asset.Flags);
            AddParameter(cmd, "@creator_id", asset.CreatorID);

            if (includeCreateTime)
                AddParameter(cmd, "@create_time", now);
        }

        private static string ReadString(IDataRecord row, string field)
        {
            int idx = row.GetOrdinal(field);
            return row.IsDBNull(idx) ? string.Empty : Convert.ToString(row[idx]) ?? string.Empty;
        }

        private static int ReadInt(IDataRecord row, string field)
        {
            int idx = row.GetOrdinal(field);
            return row.IsDBNull(idx) ? 0 : Convert.ToInt32(row[idx]);
        }

        private static byte[] ReadBytes(IDataRecord row, string field)
        {
            int idx = row.GetOrdinal(field);
            if (row.IsDBNull(idx))
                return Array.Empty<byte>();

            object value = row[idx];
            if (value is byte[] bytes)
                return bytes;

            return Array.Empty<byte>();
        }
    }
}

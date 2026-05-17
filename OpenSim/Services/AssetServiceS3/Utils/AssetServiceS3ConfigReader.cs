using Nini.Config;
using OpenSim.Services.AssetServiceS3.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenSim.Services.AssetServiceS3.Utils
{
    /// <summary>
    /// Reads AssetServiceS3-related options from OpenSim configuration.
    /// </summary>
    public static class AssetServiceS3ConfigReader
    {
        /// <summary>
        /// Reads options from config sections.
        /// </summary>
        /// <param name="source">Configuration source.</param>
        /// <param name="configName">Primary section name, usually AssetService.</param>
        /// <returns>Normalized options object.</returns>
        public static AssetServiceS3Options Read(IConfigSource source, string configName)
        {
            IConfig main = source.Configs[configName];
            IConfig storage = source.Configs["AssetStorage"];
            IConfig migration = source.Configs["AssetStorageMigration"];
            IConfig hybridBlob = source.Configs["DataS3HybridBlob"];

            string objectStore = storage?.GetString("ObjectStore", "InMemory") ?? "InMemory";
            string connection = main?.GetString("ConnectionString", string.Empty) ?? string.Empty;

            string? mainObjectStoreConnection = main?.GetString("ObjectStoreConnectionString", null);
            if (!string.IsNullOrWhiteSpace(mainObjectStoreConnection))
                connection = AppendRawConnection(connection, mainObjectStoreConnection!);

            if (storage != null)
            {
                string? objectStoreConnection = storage.GetString("ObjectStoreConnectionString", null);
                if (!string.IsNullOrWhiteSpace(objectStoreConnection))
                    connection = AppendRawConnection(connection, objectStoreConnection!);

                connection = MergeStorageValue(connection, storage, "MinioEndpoint");
                connection = MergeStorageValue(connection, storage, "MinioBucket");
                connection = MergeStorageValue(connection, storage, "MinioAccessKey");
                connection = MergeStorageValue(connection, storage, "MinioSecretKey");
                connection = MergeStorageValue(connection, storage, "MinioRegion");
                connection = MergeStorageValue(connection, storage, "MinioAutoCreateBucket");
                connection = MergeStorageValue(connection, storage, "HybridBlobStoragePath");
                connection = MergeStorageValue(connection, storage, "HybridBlobDatabaseType");
                connection = MergeStorageValue(connection, storage, "HybridBlobConnectionString");
                connection = MergeStorageValue(connection, storage, "HybridBlobTableName");
                connection = MergeStorageValue(connection, storage, "HybridBlobAutoCreatePath");
                connection = MergeStorageValue(connection, storage, "MetadataProvider");
                connection = MergeStorageValue(connection, storage, "MetadataConnectionString");
                connection = MergeStorageValue(connection, storage, "CacheProvider");
                connection = MergeStorageValue(connection, storage, "CacheEntryTtlSeconds");
                connection = MergeStorageValue(connection, storage, "UploadQueueEnabled");
                connection = MergeStorageValue(connection, storage, "UploadQueueWorkers");
                connection = MergeStorageValue(connection, storage, "UploadQueueMaxPending");
                connection = MergeStorageValue(connection, storage, "FallbackReadEnabled");
                connection = MergeStorageValue(connection, storage, "ForceLegacyReadEnabled");
                connection = MergeStorageValue(connection, storage, "ReadThroughMigrationEnabled");
                connection = MergeStorageValue(connection, storage, "LegacyAssetProvider");
                connection = MergeStorageValue(connection, storage, "LegacyAssetConnectionString");
                connection = MergeStorageValue(connection, storage, "DualWriteEnabled");
                connection = MergeStorageValue(connection, storage, "CutoverMode");
                connection = MergeStorageValue(connection, storage, "DirectMigrationEnabled");
                connection = MergeStorageValue(connection, storage, "DirectMigrationBatchSize");
                connection = MergeStorageValue(connection, storage, "DirectMigrationMaxAssets");
            }

            if (migration != null)
            {
                connection = UpsertStorageValue(connection, migration, "FallbackReadEnabled");
                connection = UpsertStorageValue(connection, migration, "ForceLegacyReadEnabled");
                connection = UpsertStorageValue(connection, migration, "ReadThroughMigrationEnabled");
                connection = UpsertStorageValue(connection, migration, "DirectMigrationEnabled");
                connection = UpsertStorageValue(connection, migration, "DualWriteEnabled");
                connection = UpsertStorageValue(connection, migration, "CutoverMode");
                connection = UpsertStorageValue(connection, migration, "DirectMigrationBatchSize");
                connection = UpsertStorageValue(connection, migration, "DirectMigrationMaxAssets");
            }

            if (hybridBlob != null)
            {
                connection = UpsertStorageValue(connection, hybridBlob, "StoragePath", "HybridBlobStoragePath");
                connection = UpsertStorageValue(connection, hybridBlob, "DatabaseType", "HybridBlobDatabaseType");
                connection = UpsertStorageValue(connection, hybridBlob, "ConnectionString", "HybridBlobConnectionString");
                connection = UpsertStorageValue(connection, hybridBlob, "TableName", "HybridBlobTableName");
                connection = UpsertStorageValue(connection, hybridBlob, "AutoCreatePath", "HybridBlobAutoCreatePath");

                if (hybridBlob.GetBoolean("Enabled", false) && !objectStore.Equals("HybridBlob", StringComparison.OrdinalIgnoreCase))
                    objectStore = "HybridBlob";
            }

            return new AssetServiceS3Options
            {
                ObjectStore = objectStore,
                ConnectionString = connection
            };
        }

        private static string MergeStorageValue(string connection, IConfig storage, string key)
        {
            string? value = storage.GetString(key, null);
            if (string.IsNullOrWhiteSpace(value))
                return connection;

            if (HasConnectionKey(connection, key))
                return connection;

            if (string.IsNullOrWhiteSpace(connection))
                return $"{key}={value}";

            return $"{connection};{key}={value}";
        }

        private static bool HasConnectionKey(string connection, string key)
        {
            if (string.IsNullOrWhiteSpace(connection))
                return false;

            IEnumerable<string> tokens = connection.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim());

            foreach (string token in tokens)
            {
                int idx = token.IndexOf('=');
                if (idx <= 0)
                    continue;

                string currentKey = token.Substring(0, idx).Trim();
                if (currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string UpsertStorageValue(string connection, IConfig storage, string key)
        {
            string? value = storage.GetString(key, null);
            if (string.IsNullOrWhiteSpace(value))
                return connection;

            return UpsertConnectionToken(connection, key, value!);
        }

        private static string UpsertStorageValue(string connection, IConfig storage, string sourceKey, string targetKey)
        {
            string? value = storage.GetString(sourceKey, null);
            if (string.IsNullOrWhiteSpace(value))
                return connection;

            return UpsertConnectionToken(connection, targetKey, value!);
        }

        private static string UpsertConnectionToken(string connection, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(connection))
                return $"{key}={value}";

            List<string> tokens = connection.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            bool replaced = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                int idx = token.IndexOf('=');
                if (idx <= 0)
                    continue;

                string currentKey = token.Substring(0, idx).Trim();
                if (!currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;

                tokens[i] = $"{key}={value}";
                replaced = true;
                break;
            }

            if (!replaced)
                tokens.Add($"{key}={value}");

            return string.Join(";", tokens);
        }

        private static string AppendRawConnection(string connection, string rawConnection)
        {
            if (string.IsNullOrWhiteSpace(rawConnection))
                return connection;

            if (string.IsNullOrWhiteSpace(connection))
                return rawConnection.Trim().Trim(';');

            string trimmed = rawConnection.Trim().Trim(';');
            if (string.IsNullOrWhiteSpace(trimmed))
                return connection;

            return $"{connection};{trimmed}";
        }
    }
}

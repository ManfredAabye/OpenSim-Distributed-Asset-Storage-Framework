using System;
using System.Collections.Generic;

namespace OpenSim.DataS3.ObjectStores.HybridBlob
{
    /// <summary>
    /// Factory for creating IMetadataBackend implementations based on database type.
    /// Automatically detects database type from connection string or explicit type parameter.
    /// </summary>
    public static class MetadataBackendFactory
    {
        /// <summary>
        /// Creates a metadata backend from a connection string with explicit database type.
        /// </summary>
        /// <param name="databaseType">Type of database: "SQLite", "MySQL", "MariaDB", "PostgreSQL"</param>
        /// <param name="connectionString">Database-specific connection string</param>
        /// <param name="tableName">Optional table name (default: blob_metadata)</param>
        /// <returns>IMetadataBackend instance</returns>
        public static IMetadataBackend Create(string databaseType, string connectionString, string tableName = "blob_metadata")
        {
            if (string.IsNullOrWhiteSpace(databaseType))
                throw new ArgumentException("Database type cannot be empty", nameof(databaseType));
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be empty", nameof(connectionString));

            var type = databaseType.Trim().ToLowerInvariant();

            return type switch
            {
                "sqlite" => new SqliteMetadataBackend(connectionString),
                "mysql" => new MySqlMetadataBackend(connectionString, tableName),
                "mariadb" => new MySqlMetadataBackend(connectionString, tableName),
                "postgresql" or "postgres" => new PostgreSqlMetadataBackend(connectionString, tableName),
                _ => throw new NotSupportedException($"Unsupported database type: {databaseType}. Supported types: SQLite, MySQL, MariaDB, PostgreSQL")
            };
        }

        /// <summary>
        /// Detects database type from connection string and creates appropriate backend.
        /// Looks for database-specific keywords: Data Source (SQLite), Server (MySQL), Host (PostgreSQL)
        /// </summary>
        /// <param name="connectionString">Database connection string</param>
        /// <param name="tableName">Optional table name (default: blob_metadata)</param>
        /// <returns>IMetadataBackend instance</returns>
        public static IMetadataBackend CreateFromConnectionString(string connectionString, string tableName = "blob_metadata")
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be empty", nameof(connectionString));

            var connStr = connectionString.ToLowerInvariant();

            // Detect SQLite
            if (connStr.Contains("data source") || connStr.EndsWith(".db") || connStr.EndsWith(".sqlite"))
                return new SqliteMetadataBackend(connectionString);

            // Detect PostgreSQL
            if (connStr.Contains("host=") || connStr.Contains("npgsql") || connStr.Contains("postgres"))
                return new PostgreSqlMetadataBackend(connectionString, tableName);

            // Detect MySQL/MariaDB
            if (connStr.Contains("server=") || connStr.Contains("mysql") || connStr.Contains("mariadb"))
                return new MySqlMetadataBackend(connectionString, tableName);

            throw new InvalidOperationException(
                $"Could not detect database type from connection string. " +
                $"Please use Create(databaseType, connectionString) with explicit type. " +
                $"Supported types: SQLite, MySQL, MariaDB, PostgreSQL");
        }

        /// <summary>
        /// Creates backend from configuration dictionary.
        /// Expected keys: DatabaseType, ConnectionString, TableName (optional)
        /// </summary>
        public static IMetadataBackend CreateFromConfig(IReadOnlyDictionary<string, string> config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            if (!config.TryGetValue("DatabaseType", out var dbType))
                throw new InvalidOperationException("Configuration missing required key: DatabaseType");
            if (!config.TryGetValue("ConnectionString", out var connStr))
                throw new InvalidOperationException("Configuration missing required key: ConnectionString");

            var tableName = config.TryGetValue("TableName", out var tn) ? tn : "blob_metadata";

            return Create(dbType, connStr, tableName);
        }

        /// <summary>
        /// Gets list of supported database types.
        /// </summary>
        public static IReadOnlyList<string> SupportedDatabaseTypes => new[]
        {
            "SQLite",
            "MySQL",
            "MariaDB",
            "PostgreSQL"
        };

        /// <summary>
        /// Gets example connection strings for reference.
        /// </summary>
        public static IReadOnlyDictionary<string, string> ExampleConnectionStrings => new Dictionary<string, string>
        {
            { "SQLite", "data/hybrid_blobs/metadata.db" },
            { "MySQL", "Server=localhost;Database=opensim;Uid=root;Pwd=password;Allow Zero DateTime=true;" },
            { "MariaDB", "Server=localhost;Database=opensim;Uid=opensim;Pwd=password;" },
            { "PostgreSQL", "Host=localhost;Database=opensim;Username=opensim;Password=password;" }
        };
    }
}

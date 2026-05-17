using System;
using System.Data.Common;

namespace OpenSim.DataS3.Sql
{
    /// <summary>
    /// Default SQL connection factory with MySQL, PostgreSQL and SQLite provider mappings.
    /// </summary>
    public sealed class DefaultSqlConnectionFactory : ISqlConnectionFactory
    {
        /// <inheritdoc />
        public DbConnection CreateOpenConnection(string provider, string connectionString)
        {
            DbConnection? connection = TryCreateViaFactory(provider, connectionString);
            if (connection == null)
                connection = TryCreateViaReflection(provider, connectionString);

            if (connection == null)
                throw new InvalidOperationException($"No DB provider available for '{provider}'.");

            connection.ConnectionString = connectionString;
            connection.Open();
            return connection;
        }

        private static DbConnection? TryCreateViaFactory(string provider, string connectionString)
        {
            foreach (string invariant in GetProviderInvariants(provider, connectionString))
            {
                try
                {
                    DbProviderFactory factory = DbProviderFactories.GetFactory(invariant);
                    DbConnection? conn = factory.CreateConnection();
                    if (conn != null)
                        return conn;
                }
                catch
                {
                    // Try next registered provider mapping.
                }
            }

            return null;
        }

        private static DbConnection? TryCreateViaReflection(string provider, string connectionString)
        {
            foreach (string typeName in GetProviderConnectionTypes(provider, connectionString))
            {
                Type? type = Type.GetType(typeName, throwOnError: false);
                if (type == null)
                    continue;

                if (Activator.CreateInstance(type) is DbConnection connection)
                    return connection;
            }

            return null;
        }

        private static string[] GetProviderInvariants(string provider, string connectionString)
        {
            string normalized = (provider ?? string.Empty).Trim();

            if (normalized.Equals("MySQL", StringComparison.OrdinalIgnoreCase))
                return new[] { "MySql.Data.MySqlClient" };

            if (normalized.Equals("PGSQL", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "Npgsql" };
            }

            if (normalized.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
                return new[] { "Mono.Data.Sqlite", "System.Data.SQLite", "Microsoft.Data.Sqlite" };

            if ((connectionString ?? string.Empty).IndexOf("URI=file:", StringComparison.OrdinalIgnoreCase) >= 0)
                return new[] { "Mono.Data.Sqlite", "System.Data.SQLite", "Microsoft.Data.Sqlite" };

            // Best-effort fallback order when no provider alias is configured.
            return new[] { "MySql.Data.MySqlClient", "Npgsql", "Mono.Data.Sqlite", "System.Data.SQLite", "Microsoft.Data.Sqlite" };
        }

        private static string[] GetProviderConnectionTypes(string provider, string connectionString)
        {
            string normalized = (provider ?? string.Empty).Trim();

            if (normalized.Equals("MySQL", StringComparison.OrdinalIgnoreCase))
                return new[] { "MySql.Data.MySqlClient.MySqlConnection, MySql.Data" };

            if (normalized.Equals("PGSQL", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "Npgsql.NpgsqlConnection, Npgsql" };
            }

            if (normalized.Equals("SQLite", StringComparison.OrdinalIgnoreCase)
                || (connectionString ?? string.Empty).IndexOf("URI=file:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new[]
                {
                    "Mono.Data.Sqlite.SqliteConnection, Mono.Data.Sqlite",
                    "System.Data.SQLite.SQLiteConnection, System.Data.SQLite",
                    "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite"
                };
            }

            return new[]
            {
                "MySql.Data.MySqlClient.MySqlConnection, MySql.Data",
                "Npgsql.NpgsqlConnection, Npgsql",
                "Mono.Data.Sqlite.SqliteConnection, Mono.Data.Sqlite",
                "System.Data.SQLite.SQLiteConnection, System.Data.SQLite",
                "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite"
            };
        }
    }
}

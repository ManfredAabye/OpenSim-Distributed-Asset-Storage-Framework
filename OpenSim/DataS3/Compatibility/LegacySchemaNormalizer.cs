using System;
using System.Data.Common;
using OpenSim.DataS3.Sql;

namespace OpenSim.DataS3.Compatibility
{
    /// <summary>
    /// Normalizes legacy table names to canonical naming and keeps compatibility views.
    /// </summary>
    public static class LegacySchemaNormalizer
    {
        public static void Normalize(string provider, string connectionString, ISqlConnectionFactory? connectionFactory = null)
        {
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(connectionString))
                return;

            string normalized = provider.Trim();
            if (!normalized.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
                return;

            ISqlConnectionFactory factory = connectionFactory ?? new DefaultSqlConnectionFactory();
            using DbConnection conn = factory.CreateOpenConnection(normalized, connectionString);

            RenameIfNeeded(conn, "AgentPrefs", "AgentPreferences");
            RenameIfNeeded(conn, "usersettings", "UserSettings");

            CreateCompatibilityViewIfNeeded(conn, "AgentPrefs", "AgentPreferences");
            CreateCompatibilityViewIfNeeded(conn, "usersettings", "UserSettings");
        }

        private static void RenameIfNeeded(DbConnection conn, string oldName, string newName)
        {
            if (!TableExists(conn, oldName) || TableExists(conn, newName))
                return;

            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE \"{oldName}\" RENAME TO \"{newName}\"";
            cmd.ExecuteNonQuery();
        }

        private static void CreateCompatibilityViewIfNeeded(DbConnection conn, string oldName, string newName)
        {
            if (!TableExists(conn, newName) || TableExists(conn, oldName) || ViewExists(conn, oldName))
                return;

            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE VIEW \"{oldName}\" AS SELECT * FROM \"{newName}\"";
            cmd.ExecuteNonQuery();
        }

        private static bool TableExists(DbConnection conn, string name)
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND lower(name)=lower(@name) LIMIT 1";
            AddParameter(cmd, "@name", name);
            return cmd.ExecuteScalar() != null;
        }

        private static bool ViewExists(DbConnection conn, string name)
        {
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='view' AND lower(name)=lower(@name) LIMIT 1";
            AddParameter(cmd, "@name", name);
            return cmd.ExecuteScalar() != null;
        }

        private static void AddParameter(DbCommand cmd, string name, object value)
        {
            DbParameter parameter = cmd.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            cmd.Parameters.Add(parameter);
        }
    }
}
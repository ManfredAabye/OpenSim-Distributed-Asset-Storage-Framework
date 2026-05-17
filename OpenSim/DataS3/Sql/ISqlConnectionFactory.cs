using System.Data.Common;

namespace OpenSim.DataS3.Sql
{
    /// <summary>
    /// Creates opened SQL connections for DataS3 using provider aliases.
    /// </summary>
    public interface ISqlConnectionFactory
    {
        /// <summary>
        /// Creates and opens a database connection for the given provider and connection string.
        /// </summary>
        /// <param name="provider">Provider alias, for example MySQL, PGSQL, SQLite.</param>
        /// <param name="connectionString">Provider-specific connection string.</param>
        /// <returns>Opened database connection.</returns>
        DbConnection CreateOpenConnection(string provider, string connectionString);
    }
}

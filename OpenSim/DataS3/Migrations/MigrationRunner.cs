using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using log4net;

namespace OpenSim.DataS3.Migrations
{
    // Local copy for the new Data stack so DataS3 can run without OpenSim.Data dependency.
    public class MigrationRunner
    {
        private static readonly ILog m_log = LogManager.GetLogger(typeof(MigrationRunner));

        protected string _type = string.Empty;
        protected DbConnection _conn = null!;
        protected Assembly _assem = null!;

        private Regex _match_old = null!;
        private Regex _match_new = null!;

        public MigrationRunner()
        {
        }

        public MigrationRunner(DbConnection connection, Assembly assembly, string subtype, string type)
        {
            Initialize(connection, assembly, type, subtype);
        }

        public MigrationRunner(DbConnection connection, Assembly assembly, string type)
        {
            Initialize(connection, assembly, type, string.Empty);
        }

        public void Initialize(DbConnection connection, Assembly assembly, string type, string subtype)
        {
            _type = type;
            _conn = connection;
            _assem = assembly;
            _match_old = new Regex(subtype + @"\.(\d\d\d)_" + _type + @"\.sql");
            string s = string.IsNullOrEmpty(subtype) ? _type : _type + @"\." + subtype;
            _match_new = new Regex(@"\." + s + @"\.migrations(?:\.(?<ver>\d+)$|.*)");
        }

        public void InitMigrationsTable()
        {
            int ver = FindVersion(_conn, "migrations");
            if (ver <= 0)
            {
                if (ver < 0)
                    ExecuteScript("create table migrations(name varchar(100), version int)");

                InsertVersion("migrations", 1);
            }
        }

        protected virtual void ExecuteScript(DbConnection connection, string[] script)
        {
            using (DbCommand cmd = connection.CreateCommand())
            {
                cmd.CommandTimeout = 0;
                foreach (string sql in script)
                {
                    cmd.CommandText = sql;
                    try
                    {
                        cmd.ExecuteNonQuery();
                    }
                    catch (Exception e)
                    {
                        throw new Exception(e.Message + " in SQL: " + sql);
                    }
                }
            }
        }

        protected void ExecuteScript(DbConnection connection, string sql)
        {
            ExecuteScript(connection, new[] { sql });
        }

        protected void ExecuteScript(string sql)
        {
            ExecuteScript(_conn, sql);
        }

        protected void ExecuteScript(string[] script)
        {
            ExecuteScript(_conn, script);
        }

        public void Update()
        {
            InitMigrationsTable();

            int version = FindVersion(_conn, _type);
            SortedList<int, string[]> migrations = GetMigrationsAfter(version);
            if (migrations.Count < 1)
                return;

            m_log.InfoFormat("[MIGRATIONS]: Upgrading {0} to latest revision {1}.", _type, migrations.Keys[migrations.Count - 1]);
            m_log.Info("[MIGRATIONS]: NOTE - this may take a while, don't interrupt this process!");

            foreach (KeyValuePair<int, string[]> kvp in migrations)
            {
                int newVersion = kvp.Key;
                try
                {
                    ExecuteScript(kvp.Value);
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[MIGRATIONS]: Cmd was {0}", e.Message.Replace("\n", " "));
                    m_log.Debug("[MIGRATIONS]: An error has occurred in the migration. If this is first startup you can often ignore it. If database errors persist, fix them manually.");
                    ExecuteScript("ROLLBACK;");
                }

                if (version == 0)
                    InsertVersion(_type, newVersion);
                else
                    UpdateVersion(_type, newVersion);

                version = newVersion;
            }
        }

        public int Version
        {
            get { return FindVersion(_conn, _type); }
            set
            {
                if (Version < 1)
                    InsertVersion(_type, value);
                else
                    UpdateVersion(_type, value);
            }
        }

        protected virtual int FindVersion(DbConnection connection, string type)
        {
            int version = 0;
            using (DbCommand cmd = connection.CreateCommand())
            {
                try
                {
                    cmd.CommandText = "select version from migrations where name='" + type + "' order by version desc";
                    using (DbDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                            version = Convert.ToInt32(reader["version"]);

                        reader.Close();
                    }
                }
                catch
                {
                    version = -1;
                }
            }

            return version;
        }

        private void InsertVersion(string type, int version)
        {
            m_log.InfoFormat("[MIGRATIONS]: Creating {0} at version {1}", type, version);
            ExecuteScript("insert into migrations(name, version) values('" + type + "', " + version + ")");
        }

        private void UpdateVersion(string type, int version)
        {
            m_log.InfoFormat("[MIGRATIONS]: Updating {0} to version {1}", type, version);
            ExecuteScript("update migrations set version=" + version + " where name='" + type + "'");
        }

        private delegate void FlushProc();

        private SortedList<int, string[]> GetMigrationsAfter(int after)
        {
            SortedList<int, string[]> migrations = new SortedList<int, string[]>();

            string[] names = _assem.GetManifestResourceNames();
            if (names.Length == 0)
                return migrations;

            Array.Sort(names);

            Match? m = null;
            string? file = Array.FindLast(names, nm =>
            {
                m = _match_new.Match(nm);
                return m.Success;
            });

            if ((m != null) && !string.IsNullOrEmpty(file))
            {
                int lastVersionFound;
                if (m.Groups.Count > 1 && int.TryParse(m.Groups[1].Value, out lastVersionFound) && lastVersionFound <= after)
                    goto scan_old_style;

                StringBuilder sb = new StringBuilder(4096);
                int version = -1;
                List<string> script = new List<string>();

                FlushProc flush = delegate
                {
                    if (sb.Length > 0)
                    {
                        script.Add(sb.ToString());
                        sb.Length = 0;
                    }

                    if ((version > 0) && (version > after) && (script.Count > 0) && !migrations.ContainsKey(version))
                        migrations[version] = script.ToArray();

                    script.Clear();
                };

                Stream? resource = _assem.GetManifestResourceStream(file);
                if (resource == null)
                    goto scan_old_style;

                using (resource)
                using (StreamReader resourceReader = new StreamReader(resource))
                {
                    int lineNo = 0;
                    while (!resourceReader.EndOfStream)
                    {
                        string? line = resourceReader.ReadLine();
                        lineNo++;

                        if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                            continue;

                        if (line.Trim().Equals(":GO", StringComparison.InvariantCultureIgnoreCase))
                        {
                            if (sb.Length == 0)
                                continue;

                            if (version > after)
                                script.Add(sb.ToString());

                            sb.Length = 0;
                            continue;
                        }

                        if (line.StartsWith(":VERSION ", StringComparison.InvariantCultureIgnoreCase))
                        {
                            flush();

                            int n = line.IndexOf('#');
                            if (n >= 0)
                                line = line.Substring(0, n);

                            if (!int.TryParse(line.Substring(9).Trim(), out version))
                            {
                                m_log.ErrorFormat("[MIGRATIONS]: invalid version marker at {0}: line {1}. Migration failed!", file, lineNo);
                                break;
                            }
                        }
                        else
                        {
                            sb.AppendLine(line);
                        }
                    }

                    flush();
                    if (after < version)
                        after = version;
                }
            }

scan_old_style:
            foreach (string s in names)
            {
                m = _match_old.Match(s);
                if (m.Success)
                {
                    int version = int.Parse(m.Groups[1].ToString());
                    if ((version > after) && !migrations.ContainsKey(version))
                    {
                        Stream? resource = _assem.GetManifestResourceStream(s);
                        if (resource == null)
                            continue;

                        using (resource)
                        using (StreamReader resourceReader = new StreamReader(resource))
                        {
                            string sql = resourceReader.ReadToEnd();
                            migrations.Add(version, new[] { sql });
                        }
                    }
                }
            }

            if (migrations.Count < 1)
                m_log.DebugFormat("[MIGRATIONS]: {0} data tables already up to date at revision {1}", _type, after);

            return migrations;
        }
    }
}

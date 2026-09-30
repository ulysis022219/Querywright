using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Querywright.Core;

namespace Querywright.Ssms
{
    /// <summary>Connection of the active SSMS query window. Holds no password text; see <see cref="Open"/>.</summary>
    internal sealed class ActiveConnection
    {
        internal string Server, Database, User;
        internal bool Integrated, Encrypt = true, TrustServerCertificate;
        internal SecureString Password;
        internal string Key => Server + "\0" + Database + "\0" + (Integrated ? "" : User);

        /// <summary>Traffic stays on this machine (LocalDB, shared memory, local pipe), so TLS adds nothing.</summary>
        internal bool IsLocal
        {
            get
            {
                var host = (Server ?? "").Trim().ToLowerInvariant();
                if (host.StartsWith("lpc:") || host.StartsWith(@"np:\\.\")) return true;
                host = host.Split('\\', ',')[0];
                return host == "(localdb)" || host == "." || host == "(local)" || host == "localhost";
            }
        }

        internal ActiveConnection WithDatabase(string database)
        {
            var copy = (ActiveConnection)MemberwiseClone();
            copy.Database = database;
            return copy;
        }

        /// <summary>An open connection to the server.</summary>
        internal SqlConnection Open()
        {
            var sql = Create();
            try { sql.Open(); return sql; }
            catch (SqlException error) when (error.Number == 20 && Encrypt && IsLocal)
            {
                // ponytail: SqlClient can't encrypt to LocalDB/shared memory (error 20); local-only traffic, so retry plain.
                sql.Dispose();
                Encrypt = false;
                return Open();
            }
            catch { sql.Dispose(); throw; }
        }

        private SqlConnection Create()
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = Server, InitialCatalog = Database ?? "", IntegratedSecurity = Integrated,
                Encrypt = Encrypt, TrustServerCertificate = TrustServerCertificate, ConnectTimeout = 10,
                ApplicationName = "Querywright metadata", Pooling = false
            };
            // Password travels only as a read-only SecureString, never through the connection string.
            return Integrated ? new SqlConnection(builder.ConnectionString)
                : new SqlConnection(builder.ConnectionString, new SqlCredential(User, Password));
        }
    }

    /// <summary>Reads table/view/column names, types and foreign keys from the connected database with one fixed catalog query. Never runs user SQL.</summary>
    internal static class LiveMetadata
    {
        private const int MaxRows = 100_000;
        // Joins sys.types/sys.schemas once instead of per-row TYPE_NAME()/SCHEMA_NAME() calls, and orders by the catalog's
        // own key (object_id, column_id) so large databases need no server-side name sort; names are sorted client-side.
        private static string TypeSql(string c, string t) => t + @".name +
    CASE WHEN " + t + @".name IN ('varchar', 'char', 'varbinary', 'binary')
            THEN '(' + CASE " + c + @".max_length WHEN -1 THEN 'max' ELSE CAST(" + c + @".max_length AS varchar(5)) END + ')'
        WHEN " + t + @".name IN ('nvarchar', 'nchar')
            THEN '(' + CASE " + c + @".max_length WHEN -1 THEN 'max' ELSE CAST(" + c + @".max_length / 2 AS varchar(5)) END + ')'
        WHEN " + t + @".name IN ('decimal', 'numeric')
            THEN '(' + CAST(" + c + @".precision AS varchar(3)) + ',' + CAST(" + c + @".scale AS varchar(3)) + ')'
        ELSE '' END";

        // Independent sections: only the columns are essential. A failing optional section degrades (no foreign keys, keep the
        // old procedure or database list) instead of failing the whole load. Each starts with its own lock timeout.
        private static readonly string ColumnsQuery = @"SET LOCK_TIMEOUT 3000;
SELECT s.name, o.name, c.name, " + TypeSql("c", "t") + @",
    CAST(CASE WHEN c.is_identity = 1 OR c.is_computed = 1 OR c.system_type_id = 189 THEN 1 ELSE 0 END AS bit),
    CAST(CASE o.type WHEN 'U' THEN 0 ELSE 1 END AS bit),
    CASE c.is_nullable WHEN 1 THEN 'NULL' ELSE 'NOT NULL' END + ISNULL(' DEFAULT ' + LEFT(d.definition, 200), '')
FROM sys.objects AS o
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
JOIN sys.columns AS c ON c.object_id = o.object_id
LEFT JOIN sys.types AS t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints AS d ON d.object_id = c.default_object_id
WHERE o.type IN ('U', 'V', 'IF', 'TF') AND o.is_ms_shipped = 0
ORDER BY o.object_id, c.column_id;";

        private static readonly string SynonymsQuery = @"SET LOCK_TIMEOUT 3000;
SELECT s.name, o.name, c.name, " + TypeSql("c", "t") + @",
    CAST(CASE WHEN c.is_identity = 1 OR c.is_computed = 1 OR c.system_type_id = 189 THEN 1 ELSE 0 END AS bit), CAST(1 AS bit),
    CASE c.is_nullable WHEN 1 THEN 'NULL' ELSE 'NOT NULL' END + ISNULL(' DEFAULT ' + LEFT(d.definition, 200), '')
FROM sys.synonyms AS o
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
JOIN sys.columns AS c ON c.object_id = OBJECT_ID(o.base_object_name)
LEFT JOIN sys.types AS t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints AS d ON d.object_id = c.default_object_id
WHERE PARSENAME(o.base_object_name, 4) IS NULL AND ISNULL(PARSENAME(o.base_object_name, 3), DB_NAME()) = DB_NAME()
ORDER BY o.object_id, c.column_id;";

        private const string ForeignKeysQuery = @"SET LOCK_TIMEOUT 3000;
SELECT fk.object_id, ps.name, p.name, pc.name, rs.name, r.name, rc.name
FROM sys.foreign_keys AS fk
JOIN sys.foreign_key_columns AS k ON k.constraint_object_id = fk.object_id
JOIN sys.objects AS p ON p.object_id = k.parent_object_id
JOIN sys.schemas AS ps ON ps.schema_id = p.schema_id
JOIN sys.columns AS pc ON pc.object_id = k.parent_object_id AND pc.column_id = k.parent_column_id
JOIN sys.objects AS r ON r.object_id = k.referenced_object_id
JOIN sys.schemas AS rs ON rs.schema_id = r.schema_id
JOIN sys.columns AS rc ON rc.object_id = k.referenced_object_id AND rc.column_id = k.referenced_column_id
WHERE p.is_ms_shipped = 0
ORDER BY fk.object_id, k.constraint_column_id;";

        private static readonly string ProceduresQuery = @"SET LOCK_TIMEOUT 3000;
SELECT s.name, o.name, p.name, " + TypeSql("p", "t") + @", p.is_output, p.has_default_value, CAST(CASE WHEN o.type IN ('P', 'PC') THEN 0 ELSE 1 END AS bit)
FROM sys.objects AS o
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
LEFT JOIN sys.parameters AS p ON p.object_id = o.object_id AND p.parameter_id > 0
LEFT JOIN sys.types AS t ON t.user_type_id = p.user_type_id
WHERE o.type IN ('P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT') AND o.is_ms_shipped = 0
ORDER BY o.object_id, p.parameter_id;";

        private const string DatabasesQuery = "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 ORDER BY name;";

        /// <summary>Why the last load failed or degraded, as safe text (exception type, SQL number, section); empty when it was clean.</summary>
        internal static string LastProblem = "";

        private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<SchemaTable>>> cache =
            new ConcurrentDictionary<string, Task<IReadOnlyList<SchemaTable>>>();

        private static readonly ConcurrentDictionary<string, IReadOnlyList<SchemaProcedure>> procedureCache =
            new ConcurrentDictionary<string, IReadOnlyList<SchemaProcedure>>();

        private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> databaseCache =
            new ConcurrentDictionary<string, IReadOnlyList<string>>();

        /// <summary>Progress of the running catalog load, for the refresh command's status bar: percent and a step label.</summary>
        internal static (int Percent, string Step) Progress = (0, "connecting");

        /// <summary>The last good tables per connection, served while a refresh reloads so completion never goes empty.</summary>
        private static readonly ConcurrentDictionary<string, IReadOnlyList<SchemaTable>> stale =
            new ConcurrentDictionary<string, IReadOnlyList<SchemaTable>>();

        // Procedures and databases keep their old lists until the reload replaces them.
        internal static void Refresh()
        {
            foreach (var entry in cache)
                if (entry.Value.Status == TaskStatus.RanToCompletion && entry.Value.Result != null) stale[entry.Key] = entry.Value.Result;
            cache.Clear();
        }

        /// <summary>Databases on the server, read with the catalog; null until that load completes.</summary>
        internal static IReadOnlyList<string> DatabaseNames(ActiveConnection connection) =>
            connection != null && databaseCache.TryGetValue(connection.Key, out var databases) ? databases : null;

        /// <summary>Stored procedures read with the catalog; null until that load completes.</summary>
        internal static IReadOnlyList<SchemaProcedure> Procedures(ActiveConnection connection) =>
            connection != null && procedureCache.TryGetValue(connection.Key, out var procedures) ? procedures : null;

        /// <summary>The (possibly running) catalog load for a connection; null result when unavailable.</summary>
        internal static Task<IReadOnlyList<SchemaTable>> LoadAsync(ActiveConnection connection) =>
            cache.GetOrAdd(connection.Key, _ => Task.Run(() => Load(connection)));

        /// <summary>Loaded tables, or null while loading / unavailable. Starts a background load on first request.</summary>
        internal static IReadOnlyList<SchemaTable> TryGet(ActiveConnection connection)
        {
            if (connection == null) return null;
            var task = LoadAsync(connection);
            #pragma warning disable VSTHRD002 // completed task: no wait
            return task.Status == TaskStatus.RanToCompletion && task.Result != null ? task.Result
                : stale.TryGetValue(connection.Key, out var previous) ? previous : null;
#pragma warning restore VSTHRD002
        }

        /// <summary>Waits up to <paramref name="timeout"/> for the load; for explicit commands, not typing.</summary>
        internal static async Task<IReadOnlyList<SchemaTable>> GetAsync(ActiveConnection connection, TimeSpan timeout)
        {
            if (connection == null) return null;
            var task = LoadAsync(connection);
            return await Task.WhenAny(task, Task.Delay(timeout)) == task ? await task : null;
        }

        /// <summary>Online databases on the connection's server that the login can open. Read-only.</summary>
        internal static IReadOnlyList<string> Databases(ActiveConnection connection)
        {
            var names = new List<string>();
            using (var sql = connection.Open())
            {
                using (var command = new SqlCommand(DatabasesQuery, sql) { CommandTimeout = 10 })
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) names.Add(reader.GetString(0));
            }
            return names;
        }

        /// <summary>Module text for F12 (OBJECT_DEFINITION; null for tables or no permission). Read-only, parameterized.</summary>
        internal static string Definition(ActiveConnection connection, string schema, string name)
        {
            string Quote(string part) => "[" + part.Replace("]", "]]") + "]";
            using (var sql = connection.Open())
            {
                using (var command = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(@name));", sql) { CommandTimeout = 10 })
                {
                    command.Parameters.Add("@name", SqlDbType.NVarChar, 1000).Value = (schema == null ? "" : Quote(schema) + ".") + Quote(name);
                    return command.ExecuteScalar() as string;
                }
            }
        }

        // Size without the type name, e.g. 50, max, 18,2, 7; empty when the type takes none.
        private const string SizeSql = @"CASE WHEN TYPE_NAME(c.system_type_id) IN ('varchar', 'char', 'varbinary', 'binary')
            THEN CASE c.max_length WHEN -1 THEN 'max' ELSE CAST(c.max_length AS varchar(5)) END
        WHEN TYPE_NAME(c.system_type_id) IN ('nvarchar', 'nchar')
            THEN CASE c.max_length WHEN -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(5)) END
        WHEN TYPE_NAME(c.system_type_id) IN ('decimal', 'numeric') THEN CAST(c.precision AS varchar(3)) + ',' + CAST(c.scale AS varchar(3))
        WHEN TYPE_NAME(c.system_type_id) IN ('datetime2', 'time', 'datetimeoffset') THEN CAST(c.scale AS varchar(3))
        ELSE '' END";

        private static readonly string DetailsQuery = @"SET LOCK_TIMEOUT 3000;
DECLARE @id int = OBJECT_ID(@name);
SELECT RTRIM(o.type), OBJECT_DEFINITION(o.object_id), ds.name, SCHEMA_NAME(o.schema_id), DB_NAME(),
    CAST(COALESCE(m.uses_ansi_nulls, t.uses_ansi_nulls, 1) AS bit), CAST(ISNULL(m.uses_quoted_identifier, 1) AS bit)
FROM sys.objects AS o
LEFT JOIN sys.indexes AS i ON i.object_id = o.object_id AND i.index_id < 2
LEFT JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
LEFT JOIN sys.tables AS t ON t.object_id = o.object_id
WHERE o.object_id = @id;
SELECT c.name, TYPE_NAME(c.user_type_id), CASE WHEN c.user_type_id = c.system_type_id THEN " + SizeSql + @" ELSE '' END, c.is_nullable,
    c.collation_name, CAST(ic.seed_value AS nvarchar(40)) + N', ' + CAST(ic.increment_value AS nvarchar(40)),
    cc.definition, CAST(ISNULL(cc.is_persisted, 0) AS bit), dc.name, dc.definition
FROM sys.columns AS c
LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
WHERE c.object_id = @id ORDER BY c.column_id;
SELECT k.name, CAST(CASE k.type WHEN 'PK' THEN 1 ELSE 0 END AS bit), CAST(CASE i.type WHEN 1 THEN 1 ELSE 0 END AS bit), c.name, ic.is_descending_key, ds.name
FROM sys.key_constraints AS k
JOIN sys.indexes AS i ON i.object_id = k.parent_object_id AND i.index_id = k.unique_index_id
JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
LEFT JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
WHERE k.parent_object_id = @id ORDER BY k.type DESC, k.name, ic.key_ordinal;
SELECT f.name, pc.name, SCHEMA_NAME(r.schema_id), r.name, rc.name, f.delete_referential_action_desc, f.update_referential_action_desc
FROM sys.foreign_keys AS f
JOIN sys.foreign_key_columns AS fc ON fc.constraint_object_id = f.object_id
JOIN sys.columns AS pc ON pc.object_id = fc.parent_object_id AND pc.column_id = fc.parent_column_id
JOIN sys.objects AS r ON r.object_id = f.referenced_object_id
JOIN sys.columns AS rc ON rc.object_id = fc.referenced_object_id AND rc.column_id = fc.referenced_column_id
WHERE f.parent_object_id = @id ORDER BY f.name, fc.constraint_column_id;
SELECT name, definition FROM sys.check_constraints WHERE parent_object_id = @id ORDER BY name;
SELECT p.name, TYPE_NAME(p.user_type_id) + CASE WHEN p.user_type_id = p.system_type_id AND " + SizeSql.Replace("c.", "p.") + @" <> ''
    THEN '(' + " + SizeSql.Replace("c.", "p.") + @" + ')' ELSE '' END, p.is_output
FROM sys.parameters AS p WHERE p.object_id = @id AND p.parameter_id > 0 ORDER BY p.parameter_id;";

        internal sealed class ObjectDetails
        {
            internal string Type = "";
            internal string Definition;
            internal string Filegroup;
            internal string Schema;
            internal string Database;
            internal bool AnsiNulls = true, QuotedIdentifier = true;
            internal readonly List<ScriptColumn> Columns = new List<ScriptColumn>();
            internal readonly List<string> Constraints = new List<string>();
            internal readonly List<(string Name, string Type, bool Output)> Parameters = new List<(string, string, bool)>();
        }

        /// <summary>Hover popup: type, definition, columns, constraints and parameters of one object; null when it does not exist. Read-only, parameterized.</summary>
        internal static ObjectDetails Details(ActiveConnection connection, string schema, string name)
        {
            string Quote(string part) => "[" + part.Replace("]", "]]") + "]";
            string Text(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var details = new ObjectDetails();
            using (var sql = connection.Open())
            {
                using (var command = new SqlCommand(DetailsQuery, sql) { CommandTimeout = 15 })
                {
                    command.Parameters.Add("@name", SqlDbType.NVarChar, 1000).Value = (schema == null ? "" : Quote(schema) + ".") + Quote(name);
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) return null;
                        details.Type = reader.GetString(0); details.Definition = Text(reader, 1); details.Filegroup = Text(reader, 2); details.Schema = Text(reader, 3);
                        details.Database = Text(reader, 4); details.AnsiNulls = reader.GetBoolean(5); details.QuotedIdentifier = reader.GetBoolean(6);
                        reader.NextResult();
                        while (reader.Read())
                            details.Columns.Add(new ScriptColumn(reader.GetString(0), reader.GetString(1), Text(reader, 2), reader.GetBoolean(3))
                            {
                                Collation = Text(reader, 4), Identity = Text(reader, 5), Computed = Text(reader, 6), Persisted = reader.GetBoolean(7),
                                DefaultName = Text(reader, 8), Default = Text(reader, 9),
                            });
                        reader.NextResult();
                        var keys = new List<(string Name, bool Primary, bool Clustered, string Column, bool Descending, string Filegroup)>();
                        while (reader.Read()) keys.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3), reader.GetBoolean(4), Text(reader, 5)));
                        details.Constraints.AddRange(keys.GroupBy(k => k.Name).Select(g =>
                            ObjectScript.Key(g.Key, g.First().Primary, g.First().Clustered, g.Select(k => (k.Column, k.Descending)), g.First().Filegroup)));
                        reader.NextResult();
                        var foreign = new List<(string Name, string Column, string RefSchema, string RefTable, string RefColumn, string Delete, string Update)>();
                        while (reader.Read()) foreign.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), Text(reader, 5), Text(reader, 6)));
                        details.Constraints.AddRange(foreign.GroupBy(k => k.Name).Select(g => ObjectScript.ForeignKey(g.Key, g.Select(k => k.Column),
                            g.First().RefSchema, g.First().RefTable, g.Select(k => k.RefColumn), g.First().Delete, g.First().Update)));
                        reader.NextResult();
                        while (reader.Read()) details.Constraints.Add(ObjectScript.Check(reader.GetString(0), reader.GetString(1)));
                        reader.NextResult();
                        while (reader.Read()) details.Parameters.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
                    }
                }
            }
            return details;
        }

        private const string DependentsQuery = @"SET LOCK_TIMEOUT 3000;
SELECT DISTINCT TOP (500) SCHEMA_NAME(o.schema_id), o.name, OBJECT_DEFINITION(o.object_id)
FROM sys.sql_expression_dependencies AS d
JOIN sys.objects AS o ON o.object_id = d.referencing_id
WHERE d.referencing_class = 1 AND d.referenced_id = OBJECT_ID(@name) AND d.referencing_id <> d.referenced_id
    AND o.type IN ('P', 'V', 'FN', 'IF', 'TF', 'TR');";

        /// <summary>Modules that reference the object, with their definitions (null when encrypted or not permitted). Read-only, parameterized.</summary>
        internal static IReadOnlyList<(string Schema, string Name, string Definition)> Dependents(ActiveConnection connection, string schema, string name)
        {
            string Quote(string part) => "[" + part.Replace("]", "]]") + "]";
            var result = new List<(string, string, string)>();
            using (var sql = connection.Open())
            {
                using (var command = new SqlCommand(DependentsQuery, sql) { CommandTimeout = 15 })
                {
                    command.Parameters.Add("@name", SqlDbType.NVarChar, 1000).Value = Quote(schema) + "." + Quote(name);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            result.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }
            return result;
        }

        /// <summary>Primary-key columns of a table in key order; empty when it has none. Read-only, parameterized.</summary>
        internal static IReadOnlyList<string> PrimaryKey(ActiveConnection connection, string schema, string name)
        {
            string Quote(string part) => "[" + part.Replace("]", "]]") + "]";
            var result = new List<string>();
            using (var sql = connection.Open())
            {
                using (var command = new SqlCommand(@"SET LOCK_TIMEOUT 3000;
SELECT c.name FROM sys.indexes AS i
JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(@name) AND i.is_primary_key = 1 ORDER BY ic.key_ordinal;", sql) { CommandTimeout = 15 })
                {
                    command.Parameters.Add("@name", SqlDbType.NVarChar, 1000).Value = Quote(schema) + "." + Quote(name);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read()) result.Add(reader.GetString(0));
                }
            }
            return result;
        }

        // Read-only: sys.dm_sql_referenced_entities raises when a module no longer binds (missing table, column, etc.).
        // The table variable and cursor live in tempdb for this batch only; no user object is touched.
        private const string InvalidObjectsQuery = @"SET NOCOUNT ON; SET LOCK_TIMEOUT 3000;
DECLARE @issues TABLE (s sysname, n sysname, problem nvarchar(2048));
DECLARE @id int, @s sysname, @n sysname, @name nvarchar(600), @count int;
DECLARE modules CURSOR LOCAL FAST_FORWARD FOR
    SELECT TOP (2000) o.object_id, SCHEMA_NAME(o.schema_id), o.name
    FROM sys.objects AS o JOIN sys.sql_modules AS m ON m.object_id = o.object_id
    WHERE o.is_ms_shipped = 0 AND o.type IN ('P', 'V', 'FN', 'IF', 'TF', 'TR') ORDER BY 2, 3;
OPEN modules;
FETCH NEXT FROM modules INTO @id, @s, @n;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @name = QUOTENAME(@s) + '.' + QUOTENAME(@n);
    BEGIN TRY
        SELECT @count = COUNT(*) FROM sys.dm_sql_referenced_entities(@name, 'OBJECT');
    END TRY
    BEGIN CATCH
        INSERT @issues VALUES (@s, @n, ERROR_MESSAGE());
    END CATCH;
    FETCH NEXT FROM modules INTO @id, @s, @n;
END;
CLOSE modules; DEALLOCATE modules;
SELECT s, n, problem FROM @issues
UNION
SELECT SCHEMA_NAME(o.schema_id), o.name, N'References missing object ' + ISNULL(d.referenced_schema_name + N'.', N'') + d.referenced_entity_name
FROM sys.sql_expression_dependencies AS d
JOIN sys.objects AS o ON o.object_id = d.referencing_id
WHERE d.referencing_class = 1 AND d.referenced_class = 1 AND d.referenced_id IS NULL
    AND d.is_caller_dependent = 0 AND d.is_ambiguous = 0
    AND d.referenced_server_name IS NULL AND d.referenced_database_name IS NULL
    AND d.referenced_entity_name NOT LIKE N'#%' AND o.is_ms_shipped = 0
    AND OBJECT_ID(QUOTENAME(ISNULL(d.referenced_schema_name, SCHEMA_NAME(o.schema_id))) + N'.' + QUOTENAME(d.referenced_entity_name)) IS NULL
    AND (d.referenced_schema_name IS NOT NULL OR OBJECT_ID(N'[dbo].' + QUOTENAME(d.referenced_entity_name)) IS NULL);";

        /// <summary>Modules in the connected database that no longer bind. Read-only; nothing is compiled, altered or executed.</summary>
        internal static IReadOnlyList<(string Schema, string Name, string Problem)> InvalidObjects(ActiveConnection connection)
        {
            var result = new List<(string, string, string)>();
            using (var sql = connection.Open())
            {
                using (var command = new SqlCommand(InvalidObjectsQuery, sql) { CommandTimeout = 120 })
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        result.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
            }
            return result;
        }

        private const int MaxAttempts = 3;

        // Transient: cold LocalDB or pipe, network blips, timeouts, lock timeout, deadlock victim, Azure throttling and failover.
        private static bool Transient(SqlException error)
        {
            switch (error.Number)
            {
                case 233: case 53: case 2: case 64: case 121: case 258: case 1205: case 1222: case 4060: case 10053: case 10054: case 10060:
                case 10928: case 10929: case 40197: case 40501: case 40613: case 49918: case 49919: case 49920:
                    return true;
                default: return false;
            }
        }

        private static string Describe(Exception error) => error.GetType().Name + (error is SqlException sql ? " " + sql.Number : "");

        private static string Text(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

        // Reads one result set into a list, capped; the rest is drained so the reader can close cleanly.
        private static List<T> ReadAll<T>(SqlConnection sql, string query, int timeout, Func<SqlDataReader, T> row, out bool capped)
        {
            var list = new List<T>();
            capped = false;
            using (var command = new SqlCommand(query, sql) { CommandTimeout = timeout })
            using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
            {
                while (reader.Read())
                {
                    if (list.Count >= MaxRows) { capped = true; break; }
                    list.Add(row(reader));
                }
            }
            return list;
        }

        private static IReadOnlyList<SchemaTable> Load(ActiveConnection connection, int attempt = 0)
        {
            if (attempt == 0 && !stale.ContainsKey(connection.Key) && DiskCachePath(connection) is string cached && File.Exists(cached))
                try
                {
                    using (var reader = File.OpenText(cached))
                    if (SchemaDiskCache.Read(reader) is var (tables, procedures))
                    {
                        stale[connection.Key] = tables;
                        procedureCache.TryAdd(connection.Key, procedures);
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            try
            {
                Progress = (0, "connecting");
                var problems = new List<string>();
                var columns = new List<(string Schema, string Table, string Column, string Type, bool Generated, bool View, string Note)>();
                var keys = new List<(int Id, string Schema, string Table, string Column, string RefSchema, string RefTable, string RefColumn)>();
                List<(string Schema, string Procedure, string Name, string Type, bool Output, bool Default, bool Function)> parameters = null;
                List<string> databases = null;
                bool capped;
                using (var sql = connection.Open())
                {
                    Progress = (10, "reading columns");
                    // Essential: an error here propagates to the retry logic below.
                    columns = ReadAll(sql, ColumnsQuery, 60, r => (Text(r, 0), Text(r, 1), Text(r, 2), Text(r, 3), !r.IsDBNull(4) && r.GetBoolean(4), !r.IsDBNull(5) && r.GetBoolean(5), Text(r, 6)), out capped);
                    Progress = (45, "reading synonyms");
                    try
                    {
                        if (!capped)
                            columns.AddRange(ReadAll(sql, SynonymsQuery, 30, r => (Text(r, 0), Text(r, 1), Text(r, 2), Text(r, 3), !r.IsDBNull(4) && r.GetBoolean(4), true, Text(r, 6)), out _));
                    }
                    catch (Exception error) when (IsRecoverable(error)) { problems.Add("synonyms " + Describe(error)); }
                    Progress = (55, "reading foreign keys");
                    try { keys = ReadAll(sql, ForeignKeysQuery, 30, r => (r.IsDBNull(0) ? 0 : r.GetInt32(0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5), Text(r, 6)), out _); }
                    catch (Exception error) when (IsRecoverable(error)) { problems.Add("foreign keys " + Describe(error)); }
                    Progress = (70, "reading procedures");
                    try { parameters = ReadAll(sql, ProceduresQuery, 30, r => (Text(r, 0), Text(r, 1), Text(r, 2), Text(r, 3), !r.IsDBNull(4) && r.GetBoolean(4), !r.IsDBNull(5) && r.GetBoolean(5), !r.IsDBNull(6) && r.GetBoolean(6)), out _); }
                    catch (Exception error) when (IsRecoverable(error)) { problems.Add("procedures " + Describe(error)); }
                    Progress = (85, "reading databases");
                    try { databases = ReadAll(sql, DatabasesQuery, 15, r => r.GetString(0), out _); }
                    catch (Exception error) when (IsRecoverable(error)) { problems.Add("databases " + Describe(error)); }
                }
                Progress = (95, "indexing " + columns.Count + " columns");
                if (capped && columns.Count > 0)
                {
                    // The last table may be cut mid-way; drop it rather than offer a partial column list.
                    var last = columns[columns.Count - 1];
                    columns.RemoveAll(c => c.Schema == last.Schema && c.Table == last.Table);
                }
                var built = CatalogAssembler.Tables(columns.Select(c => ((string)c.Schema, (string)c.Table, (string)c.Column, (string)c.Type, c.Generated, c.View, (string)c.Note)),
                    keys.Select(k => (k.Id, (string)k.Schema, (string)k.Table, (string)k.Column, (string)k.RefSchema, (string)k.RefTable, (string)k.RefColumn)));
                if (built.Skipped > 0) ActivityLog.TryLogWarning("Querywright", "Live metadata skipped " + built.Skipped + " objects with unusable names");
                // ponytail: has_default_value is only set for CLR procedures; T-SQL defaults come from script procedures or show as values.
                if (databases != null) databaseCache[connection.Key] = databases;
                if (parameters != null)
                {
                    IEnumerable<SchemaProcedure> Build(bool function) => CatalogAssembler.Procedures(parameters.Where(p => p.Function == function)
                        .Select(p => ((string)p.Schema, (string)p.Procedure, (string)p.Name, (string)p.Type, p.Output, p.Default)));
                    procedureCache[connection.Key] = Build(false).Concat(Build(true).Select(f => new SchemaProcedure(f.Schema, f.Name, f.Parameters.ToArray(), isFunction: true))).ToArray();
                }
                LastProblem = problems.Count == 0 ? "" : "partial: " + string.Join(", ", problems);
                ActivityLog.TryLogInformation("Querywright", "Live metadata loaded: " + built.Tables.Length + " tables"
                    + (capped ? " (column cap reached)" : "") + (problems.Count == 0 ? "" : " (" + LastProblem + ")"));
                stale.TryRemove(connection.Key, out _);
                if (!capped && DiskCachePath(connection) is string path)
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        using (var writer = File.CreateText(path + ".tmp")) SchemaDiskCache.Write(writer, built.Tables, procedureCache.TryGetValue(connection.Key, out var saved) ? saved : null);
                        File.Copy(path + ".tmp", path, true); File.Delete(path + ".tmp");
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
                return built.Tables;
            }
            catch (SqlException error) when (attempt < MaxAttempts - 1 && Transient(error) && (error.Number != -2 || attempt == 0))
            {
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(2 * (attempt + 1)));
                return Load(connection, attempt + 1);
            }
            catch (Exception error) when (IsRecoverable(error))
            {
                // Type and SQL error number only: messages can echo server or login names.
                LastProblem = Describe(error);
                ActivityLog.TryLogWarning("Querywright", "Live metadata unavailable: " + LastProblem);
                // Back off 30 s before the next attempt; Refresh clears the cache for an immediate retry.
                if (cache.TryGetValue(connection.Key, out var failed))
                    _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
                        ((ICollection<KeyValuePair<string, Task<IReadOnlyList<SchemaTable>>>>)cache).Remove(
                            new KeyValuePair<string, Task<IReadOnlyList<SchemaTable>>>(connection.Key, failed)), TaskScheduler.Default);
                return null;
            }
        }

        /// <summary>Schema cache file for a connection, named by a hash so no server, database or login appears on disk; null when the option is off.</summary>
        private static string DiskCacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "SchemaCache");

        /// <summary>Removes every cached schema file; called when "Cache schema on disk" is turned off.</summary>
        internal static void ClearDiskCache()
        {
            try { if (Directory.Exists(DiskCacheFolder)) Directory.Delete(DiskCacheFolder, true); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { ActivityLog.TryLogWarning("Querywright", "Schema cache not cleared: " + error.GetType().Name); }
        }

        private static string DiskCachePath(ActiveConnection connection)
        {
            if (WorkbenchPackage.Instance?.Options?.CacheSchemaOnDisk != true) return null;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return Path.Combine(DiskCacheFolder, BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(connection.Key))).Replace("-", "") + ".txt");
        }

        private static bool IsRecoverable(Exception error) => !(error is OutOfMemoryException);

        private static Type serviceCacheType;

        internal static object ActiveConnectionInfo()
        {
            var serviceCache = serviceCacheType ?? (serviceCacheType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache", false))
                .FirstOrDefault(t => t != null));
            var factory = Property(null, serviceCache, "ScriptFactory");
            var active = Property(factory, factory?.GetType(), "CurrentlyActiveWndConnectionInfo");
            return Property(active, active?.GetType(), "UIConnectionInfo");
        }

        /// <summary>Server and database of the active query window, any authentication type; no credentials read.</summary>
        internal static (string Server, string Database)? CaptureNames()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var info = ActiveConnectionInfo();
                var server = Property(info, info?.GetType(), "ServerName") as string;
                if (string.IsNullOrEmpty(server)) return null;
                return (server, (Property(info, info.GetType(), "AdvancedOptions") as NameValueCollection)?["DATABASE"]);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                LogOnce("Connection name unavailable: " + error.GetType().Name);
                return null;
            }
        }

        /// <summary>Connection of the active query window via SSMS's ServiceCache (no public API). Null when not connected.</summary>
        internal static ActiveConnection Capture()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var info = ActiveConnectionInfo();
                if (info == null) return null;
                var type = info.GetType();
                var options = Property(info, type, "AdvancedOptions") as NameValueCollection;
                int authentication = Convert.ToInt32(Property(info, type, "AuthenticationType") ?? -1);
                var connection = new ActiveConnection
                {
                    Server = Property(info, type, "ServerName") as string,
                    User = Property(info, type, "UserName") as string,
                    Database = options?["DATABASE"],
                    Integrated = authentication == 0
                };
                if (string.IsNullOrEmpty(connection.Server)) return null;
                if (authentication == 1)
                {
                    // Copy SSMS's SecureString; never dispose it, SSMS owns it.
                    if (Property(info, type, "InMemoryPassword") is SecureString secure) connection.Password = secure.Copy();
                    else if (Property(info, type, "Password") is string plain)
                    {
                        connection.Password = new SecureString();
                        foreach (char c in plain) connection.Password.AppendChar(c);
                    }
                    else return null;
                    connection.Password.MakeReadOnly();
                }
                else if (authentication != 0)
                {
                    LogOnce("Live metadata skipped: authentication type " + authentication + " not supported");
                    return null;
                }
                if (options != null)
                {
                    connection.Encrypt = Flag(options, "ENCRYPT_CONNECTION", "ENCRYPT") ?? true;
                    connection.TrustServerCertificate = Flag(options, "TRUST_SERVER_CERTIFICATE", "TRUSTSERVERCERTIFICATE") ?? false;
                    LogOnce("Connection option keys: " + string.Join(",", options.AllKeys));
                }
                return connection;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                LogOnce("Live metadata connection lookup failed: " + error.GetType().Name);
                return null;
            }
        }

        private static bool? Flag(NameValueCollection options, params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = options[key];
                if (value == null) continue;
                if (bool.TryParse(value, out var flag)) return flag;
                if (value.Equals("Mandatory", StringComparison.OrdinalIgnoreCase) || value.Equals("Strict", StringComparison.OrdinalIgnoreCase)) return true;
                if (value.Equals("Optional", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return null;
        }

        private static readonly HashSet<string> logged = new HashSet<string>();
        private static void LogOnce(string message)
        {
            lock (logged) if (!logged.Add(message)) return;
            ActivityLog.TryLogInformation("Querywright", message);
        }

        /// <summary>Public, non-public or explicit-interface property; SSMS types vary between releases.</summary>
        private static object Property(object target, Type type, string name)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            var property = type.GetProperty(name, flags)
                ?? type.GetInterfaces().Select(i => i.GetProperty(name)).FirstOrDefault(p => p != null)
                ?? type.GetProperties(flags).FirstOrDefault(p => p.Name.EndsWith("." + name, StringComparison.Ordinal));
            return property?.GetValue(property.GetGetMethod(true)?.IsStatic == true ? null : target);
        }
    }
}

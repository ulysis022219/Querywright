using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Data.SqlClient;
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

        internal SqlConnection Open()
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
        private static string TypeSql(string c) => @"TYPE_NAME(" + c + @".user_type_id) +
    CASE WHEN TYPE_NAME(" + c + @".user_type_id) IN ('varchar', 'char', 'varbinary', 'binary')
            THEN '(' + CASE " + c + @".max_length WHEN -1 THEN 'max' ELSE CAST(" + c + @".max_length AS varchar(5)) END + ')'
        WHEN TYPE_NAME(" + c + @".user_type_id) IN ('nvarchar', 'nchar')
            THEN '(' + CASE " + c + @".max_length WHEN -1 THEN 'max' ELSE CAST(" + c + @".max_length / 2 AS varchar(5)) END + ')'
        WHEN TYPE_NAME(" + c + @".user_type_id) IN ('decimal', 'numeric')
            THEN '(' + CAST(" + c + @".precision AS varchar(3)) + ',' + CAST(" + c + @".scale AS varchar(3)) + ')'
        ELSE '' END";

        private static readonly string CatalogQuery = @"SET LOCK_TIMEOUT 3000;
SELECT s.name, o.name, c.name, " + TypeSql("c") + @",
    CAST(CASE WHEN c.is_identity = 1 OR c.is_computed = 1 OR TYPE_NAME(c.system_type_id) = 'timestamp' THEN 1 ELSE 0 END AS bit)
FROM sys.objects AS o
JOIN sys.schemas AS s ON s.schema_id = o.schema_id
JOIN sys.columns AS c ON c.object_id = o.object_id
WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
ORDER BY s.name, o.name, c.column_id;
SELECT fk.object_id, SCHEMA_NAME(p.schema_id), p.name, pc.name, SCHEMA_NAME(r.schema_id), r.name, rc.name
FROM sys.foreign_keys AS fk
JOIN sys.foreign_key_columns AS k ON k.constraint_object_id = fk.object_id
JOIN sys.objects AS p ON p.object_id = k.parent_object_id
JOIN sys.columns AS pc ON pc.object_id = k.parent_object_id AND pc.column_id = k.parent_column_id
JOIN sys.objects AS r ON r.object_id = k.referenced_object_id
JOIN sys.columns AS rc ON rc.object_id = k.referenced_object_id AND rc.column_id = k.referenced_column_id
WHERE p.is_ms_shipped = 0
ORDER BY fk.object_id, k.constraint_column_id;
SELECT SCHEMA_NAME(o.schema_id), o.name, p.name, " + TypeSql("p") + @", p.is_output, p.has_default_value
FROM sys.objects AS o
LEFT JOIN sys.parameters AS p ON p.object_id = o.object_id AND p.parameter_id > 0
WHERE o.type IN ('P', 'PC') AND o.is_ms_shipped = 0
ORDER BY 1, 2, p.parameter_id;";

        private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<SchemaTable>>> cache =
            new ConcurrentDictionary<string, Task<IReadOnlyList<SchemaTable>>>();

        private static readonly ConcurrentDictionary<string, IReadOnlyList<SchemaProcedure>> procedureCache =
            new ConcurrentDictionary<string, IReadOnlyList<SchemaProcedure>>();

        internal static void Refresh() { cache.Clear(); procedureCache.Clear(); }

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
            return task.Status == TaskStatus.RanToCompletion ? task.Result : null;
#pragma warning restore VSTHRD002
        }

        /// <summary>Waits up to <paramref name="timeout"/> for the load; for explicit commands, not typing.</summary>
        internal static async Task<IReadOnlyList<SchemaTable>> GetAsync(ActiveConnection connection, TimeSpan timeout)
        {
            if (connection == null) return null;
            var task = LoadAsync(connection);
            return await Task.WhenAny(task, Task.Delay(timeout)) == task ? await task : null;
        }

        /// <summary>Module text for F12 (OBJECT_DEFINITION; null for tables or no permission). Read-only, parameterized.</summary>
        internal static string Definition(ActiveConnection connection, string schema, string name)
        {
            string Quote(string part) => "[" + part.Replace("]", "]]") + "]";
            using (var sql = connection.Open())
            {
                sql.Open();
                using (var command = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(@name));", sql) { CommandTimeout = 10 })
                {
                    command.Parameters.Add("@name", SqlDbType.NVarChar, 1000).Value = (schema == null ? "" : Quote(schema) + ".") + Quote(name);
                    return command.ExecuteScalar() as string;
                }
            }
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
                sql.Open();
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
                sql.Open();
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
                sql.Open();
                using (var command = new SqlCommand(InvalidObjectsQuery, sql) { CommandTimeout = 120 })
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        result.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
            }
            return result;
        }

        private static IReadOnlyList<SchemaTable> Load(ActiveConnection connection, int attempt = 0)
        {
            try
            {
                var columns = new List<(string Schema, string Table, string Column, string Type, bool Generated)>();
                var parameters = new List<(string Schema, string Procedure, string Name, string Type, bool Output, bool Default)>();
                var keys = new List<(int Id, string Schema, string Table, string Column, string RefSchema, string RefTable, string RefColumn)>();
                using (var sql = connection.Open())
                {
                    sql.Open();
                    using (var command = new SqlCommand(CatalogQuery, sql) { CommandTimeout = 15 })
                    using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                    {
                        while (reader.Read() && columns.Count < MaxRows)
                            columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4)));
                        while (reader.Read()) { } // drain capped rows
                        if (reader.NextResult())
                            while (reader.Read() && keys.Count < MaxRows)
                                keys.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                                    reader.GetString(4), reader.GetString(5), reader.GetString(6)));
                        while (reader.Read()) { }
                        if (reader.NextResult())
                            while (reader.Read() && parameters.Count < MaxRows)
                                parameters.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                                    reader.IsDBNull(3) ? null : reader.GetString(3), !reader.IsDBNull(4) && reader.GetBoolean(4), !reader.IsDBNull(5) && reader.GetBoolean(5)));
                    }
                }
                var foreignKeys = keys.GroupBy(k => k.Id).ToLookup(g => (g.First().Schema, g.First().Table),
                    g => new SchemaForeignKey(g.Select(k => k.Column).ToArray(), g.First().RefSchema, g.First().RefTable, g.Select(k => k.RefColumn).ToArray()));
                var tables = columns.GroupBy(c => (c.Schema, c.Table))
                    .Select(g => new SchemaTable(g.Key.Schema, g.Key.Table, g.Select(c => c.Column).ToArray(),
                        g.Select(c => c.Type).ToArray(), foreignKeys[g.Key].ToArray(), g.Select(c => c.Generated).ToArray())).ToArray();
                // ponytail: has_default_value is only set for CLR procedures; T-SQL defaults come from script procedures or show as values.
                procedureCache[connection.Key] = parameters.GroupBy(p => (p.Schema, p.Procedure))
                    .Select(g => new SchemaProcedure(g.Key.Schema, g.Key.Procedure, g.Where(p => p.Name != null)
                        .Select(p => new SchemaParameter(p.Name, p.Type, p.Output, p.Default)).ToArray())).ToArray();
                ActivityLog.TryLogInformation("Querywright", "Live metadata loaded: " + tables.Length + " tables");
                return tables;
            }
            catch (SqlException error) when (error.Number == 20 && connection.Encrypt && connection.IsLocal)
            {
                // ponytail: SqlClient can't encrypt to LocalDB/shared memory (error 20); local-only traffic, so retry plain.
                connection.Encrypt = false;
                return Load(connection, attempt);
            }
            // Pipe/network not ready yet (cold LocalDB, server starting): retry shortly instead of backing off 30 s.
            catch (SqlException error) when (attempt < 3 && (error.Number == 233 || error.Number == 53 || error.Number == 2 || error.Number == -2 || error.Number == 10054))
            {
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(2 * (attempt + 1)));
                return Load(connection, attempt + 1);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                // Type and SQL error number only: messages can echo server or login names.
                ActivityLog.TryLogWarning("Querywright", "Live metadata unavailable: " + error.GetType().Name
                    + (error is SqlException sqlError ? " " + sqlError.Number : ""));
                // Back off 30 s before the next attempt; Refresh clears the cache for an immediate retry.
                if (cache.TryGetValue(connection.Key, out var failed))
                    _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
                        ((ICollection<KeyValuePair<string, Task<IReadOnlyList<SchemaTable>>>>)cache).Remove(
                            new KeyValuePair<string, Task<IReadOnlyList<SchemaTable>>>(connection.Key, failed)), TaskScheduler.Default);
                return null;
            }
        }

        private static Type serviceCacheType;

        private static object ActiveConnectionInfo()
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
                    var secure = Property(info, type, "InMemoryPassword") as SecureString;
                    if (secure == null && Property(info, type, "Password") is string plain)
                    {
                        secure = new SecureString();
                        foreach (char c in plain) secure.AppendChar(c);
                    }
                    if (secure == null) return null;
                    connection.Password = secure.Copy();
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

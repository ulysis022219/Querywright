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
        private const string CatalogQuery = @"SET LOCK_TIMEOUT 3000;
SELECT s.name, o.name, c.name, TYPE_NAME(c.user_type_id) +
    CASE WHEN TYPE_NAME(c.user_type_id) IN ('varchar', 'char', 'varbinary', 'binary')
            THEN '(' + CASE c.max_length WHEN -1 THEN 'max' ELSE CAST(c.max_length AS varchar(5)) END + ')'
        WHEN TYPE_NAME(c.user_type_id) IN ('nvarchar', 'nchar')
            THEN '(' + CASE c.max_length WHEN -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(5)) END + ')'
        WHEN TYPE_NAME(c.user_type_id) IN ('decimal', 'numeric')
            THEN '(' + CAST(c.precision AS varchar(3)) + ',' + CAST(c.scale AS varchar(3)) + ')'
        ELSE '' END
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
ORDER BY fk.object_id, k.constraint_column_id;";

        private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<SchemaTable>>> cache =
            new ConcurrentDictionary<string, Task<IReadOnlyList<SchemaTable>>>();

        internal static void Refresh() => cache.Clear();

        /// <summary>Loaded tables, or null while loading / unavailable. Starts a background load on first request.</summary>
        internal static IReadOnlyList<SchemaTable> TryGet(ActiveConnection connection)
        {
            if (connection == null) return null;
            var task = cache.GetOrAdd(connection.Key, _ => Task.Run(() => Load(connection)));
            #pragma warning disable VSTHRD002 // completed task: no wait
            return task.Status == TaskStatus.RanToCompletion ? task.Result : null;
#pragma warning restore VSTHRD002
        }

        /// <summary>Waits up to <paramref name="timeout"/> for the load; for explicit commands, not typing.</summary>
        internal static async Task<IReadOnlyList<SchemaTable>> GetAsync(ActiveConnection connection, TimeSpan timeout)
        {
            if (connection == null) return null;
            var task = cache.GetOrAdd(connection.Key, _ => Task.Run(() => Load(connection)));
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

        private static IReadOnlyList<SchemaTable> Load(ActiveConnection connection)
        {
            try
            {
                var columns = new List<(string Schema, string Table, string Column, string Type)>();
                var keys = new List<(int Id, string Schema, string Table, string Column, string RefSchema, string RefTable, string RefColumn)>();
                using (var sql = connection.Open())
                {
                    sql.Open();
                    using (var command = new SqlCommand(CatalogQuery, sql) { CommandTimeout = 15 })
                    using (var reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                    {
                        while (reader.Read() && columns.Count < MaxRows)
                            columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
                        while (reader.Read()) { } // drain capped rows
                        if (reader.NextResult())
                            while (reader.Read() && keys.Count < MaxRows)
                                keys.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                                    reader.GetString(4), reader.GetString(5), reader.GetString(6)));
                    }
                }
                var foreignKeys = keys.GroupBy(k => k.Id).ToLookup(g => (g.First().Schema, g.First().Table),
                    g => new SchemaForeignKey(g.Select(k => k.Column).ToArray(), g.First().RefSchema, g.First().RefTable, g.Select(k => k.RefColumn).ToArray()));
                var tables = columns.GroupBy(c => (c.Schema, c.Table))
                    .Select(g => new SchemaTable(g.Key.Schema, g.Key.Table, g.Select(c => c.Column).ToArray(),
                        g.Select(c => c.Type).ToArray(), foreignKeys[g.Key].ToArray())).ToArray();
                ActivityLog.TryLogInformation("Querywright", "Live metadata loaded: " + tables.Length + " tables");
                return tables;
            }
            catch (SqlException error) when (error.Number == 20 && connection.Encrypt && connection.IsLocal)
            {
                // ponytail: SqlClient can't encrypt to LocalDB/shared memory (error 20); local-only traffic, so retry plain.
                connection.Encrypt = false;
                return Load(connection);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                // Type and SQL error number only: messages can echo server or login names.
                ActivityLog.TryLogWarning("Querywright", "Live metadata unavailable: " + error.GetType().Name
                    + (error is SqlException sqlError ? " " + sqlError.Number : ""));
                cache.TryRemove(connection.Key, out _); // retry on next request
                return null;
            }
        }

        private static Type serviceCacheType;

        /// <summary>Connection of the active query window via SSMS's ServiceCache (no public API). Null when not connected.</summary>
        internal static ActiveConnection Capture()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var serviceCache = serviceCacheType ?? (serviceCacheType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache", false))
                    .FirstOrDefault(t => t != null));
                var factory = Property(null, serviceCache, "ScriptFactory");
                var active = Property(factory, factory?.GetType(), "CurrentlyActiveWndConnectionInfo");
                var info = Property(active, active?.GetType(), "UIConnectionInfo");
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

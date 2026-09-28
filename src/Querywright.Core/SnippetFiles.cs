using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Querywright.Core
{
    public static class SnippetFiles
    {
        private const string Proc = "\r\nAS\r\nBEGIN\r\n    SET NOCOUNT ON;\r\n    $CURSOR$\r\nEND;\r\n";
        private const string ScalarFn = " (@param int)\r\nRETURNS int\r\nAS\r\nBEGIN\r\n    RETURN $CURSOR$;\r\nEND;\r\n";

        // File name (without .sql) is the Tab shortcut.
        private static readonly KeyValuePair<string, string>[] Defaults =
        {
            Pair("ssf", "SELECT * FROM $CURSOR$"),
            Pair("sst", "SELECT TOP (10) * FROM $CURSOR$"),
            Pair("ss0", "SELECT * FROM $CURSOR$ WHERE 1 = 0;"),
            Pair("st100", "SELECT TOP (100) * FROM $CURSOR$"),
            Pair("scf", "SELECT COUNT(*) FROM $CURSOR$"),
            Pair("sd", "SELECT DISTINCT $CURSOR$\r\nFROM table_name;"),
            Pair("smf", "SELECT MAX($CURSOR$)\r\nFROM table_name;"),
            Pair("ii", "INSERT INTO $CURSOR$ (column_name)\r\nVALUES (NULL);"),
            Pair("df", "DELETE FROM $CURSOR$"),
            Pair("ij", "INNER JOIN $CURSOR$ ON "),
            Pair("lj", "LEFT JOIN $CURSOR$ ON "),
            Pair("rj", "RIGHT JOIN $CURSOR$ ON "),
            Pair("fj", "FULL JOIN $CURSOR$ ON "),
            Pair("cj", "CROSS JOIN $CURSOR$"),
            Pair("j", "JOIN $CURSOR$ ON "),
            Pair("loj", "LEFT OUTER JOIN $CURSOR$ ON "),
            Pair("roj", "RIGHT OUTER JOIN $CURSOR$ ON "),
            Pair("foj", "FULL OUTER JOIN $CURSOR$ ON "),
            Pair("gb", "GROUP BY $CURSOR$"),
            Pair("ob", "ORDER BY $CURSOR$"),
            Pair("be", "BEGIN\r\n    $SELECTEDTEXT$$CURSOR$\r\nEND;\r\n"),
            Pair("bt", "BEGIN TRANSACTION;\r\n$CURSOR$"),
            Pair("ctr", "COMMIT TRANSACTION;\r\n$CURSOR$"),
            Pair("rt", "ROLLBACK TRANSACTION;\r\n$CURSOR$"),
            Pair("tc", "BEGIN TRY\r\n    BEGIN TRANSACTION;\r\n    $SELECTEDTEXT$$CURSOR$\r\n    COMMIT TRANSACTION;\r\nEND TRY\r\nBEGIN CATCH\r\n    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;\r\n    THROW;\r\nEND CATCH;\r\n"),
            Pair("cte", "WITH cte AS (\r\n    $CURSOR$\r\n)\r\nSELECT * FROM cte;"),
            Pair("ct", "CREATE TABLE dbo.$SELECTIONSTART$TableName$SELECTIONEND$\r\n(\r\n    Id int NOT NULL PRIMARY KEY,\r\n    $CURSOR$\r\n);\r\n"),
            Pair("ctt", "CREATE TABLE #$SELECTIONSTART$Temp$SELECTIONEND$\r\n(\r\n    Id int NOT NULL,\r\n    $CURSOR$\r\n);\r\n"),
            Pair("cv", "CREATE VIEW dbo.$SELECTIONSTART$ViewName$SELECTIONEND$\r\nAS\r\n$CURSOR$"),
            Pair("cp", "CREATE PROCEDURE dbo.$SELECTIONSTART$ProcedureName$SELECTIONEND$" + Proc),
            Pair("csf", "CREATE FUNCTION dbo.$SELECTIONSTART$FunctionName$SELECTIONEND$" + ScalarFn),
            Pair("ctf", "CREATE FUNCTION dbo.$SELECTIONSTART$FunctionName$SELECTIONEND$ (@param int)\r\nRETURNS @result TABLE (Id int NOT NULL)\r\nAS\r\nBEGIN\r\n    $CURSOR$\r\n    RETURN;\r\nEND;\r\n"),
            Pair("citf", "CREATE FUNCTION dbo.$SELECTIONSTART$FunctionName$SELECTIONEND$ (@param int)\r\nRETURNS TABLE\r\nAS\r\nRETURN\r\n(\r\n    $CURSOR$\r\n);\r\n"),
            Pair("at", "ALTER TABLE $CURSOR$"),
            Pair("ata", "ALTER TABLE $SELECTIONSTART$table_name$SELECTIONEND$ ADD $CURSOR$;"),
            Pair("atd", "ALTER TABLE $SELECTIONSTART$table_name$SELECTIONEND$ DROP COLUMN $CURSOR$;"),
            Pair("ac", "ALTER TABLE $SELECTIONSTART$table_name$SELECTIONEND$ ALTER COLUMN $CURSOR$;"),
            Pair("ap", "ALTER PROCEDURE dbo.$SELECTIONSTART$ProcedureName$SELECTIONEND$" + Proc),
            Pair("af", "ALTER FUNCTION dbo.$SELECTIONSTART$FunctionName$SELECTIONEND$" + ScalarFn),
            Pair("dt", "DROP TABLE IF EXISTS $CURSOR$;"),
            Pair("dp", "DROP PROCEDURE IF EXISTS $CURSOR$;"),
            Pair("dv", "DROP VIEW IF EXISTS $CURSOR$;"),
            Pair("dfn", "DROP FUNCTION IF EXISTS $CURSOR$;"),
            Pair("di", "DROP INDEX IF EXISTS $CURSOR$ ON table_name;"),
            Pair("inn", "IS NOT NULL"),
            Pair("lk", "LIKE N'%$CURSOR$%'"),
            Pair("isns", "ISNULL($CURSOR$, N'')"),
            Pair("isnn", "ISNULL($CURSOR$, 0)"),
            Pair("rnum", "ROW_NUMBER() OVER (ORDER BY $CURSOR$)"),
            Pair("cw", "CASE\r\n    WHEN $CURSOR$ THEN NULL\r\n    ELSE NULL\r\nEND"),
            Pair("ifs", "IF EXISTS (SELECT 1 FROM $CURSOR$)\r\nBEGIN\r\n    $SELECTEDTEXT$\r\nEND;\r\n"),
            Pair("today", "CAST(GETDATE() AS date)"),
            Pair("trim", "TRIM($CURSOR$)"),
            Pair("sph", "EXEC sp_help N'$CURSOR$';"),
            Pair("spt", "EXEC sp_helptext N'$CURSOR$';"),
            Pair("w2", "EXEC sp_who2;"),
        };

        private static KeyValuePair<string, string> Pair(string shortcut, string body) =>
            new KeyValuePair<string, string>(shortcut, body);

        public static void Initialize(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Choose a snippet folder.", nameof(folder));
            Directory.CreateDirectory(folder);
            Seed(Path.Combine(folder, "Select.sql"), "SELECT $SELECTIONSTART$column_name$SELECTIONEND$\r\nFROM table_name;$CURSOR$");
            Seed(Path.Combine(folder, "Create procedure.sql"), "CREATE PROCEDURE dbo.$SELECTIONSTART$ProcedureName$SELECTIONEND$\r\nAS\r\nBEGIN\r\n    SET NOCOUNT ON;\r\n    $CURSOR$\r\nEND;\r\n");
            foreach (var pair in Defaults)
                Seed(Path.Combine(folder, pair.Key + ".sql"), pair.Value);
        }

        // (shortcut, first non-empty line) for completion; unreadable or oversized files are skipped.
        public static IReadOnlyList<KeyValuePair<string, string>> List(string folder)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return result;
            var names = new List<string>();
            foreach (var path in Directory.EnumerateFiles(folder, "*.sql"))
            {
                // "*.sql" also matches ".sqlx" on Windows.
                if (!string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileNameWithoutExtension(path);
                if (IsShortcut(name)) names.Add(name);
            }
            names.Sort(StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (result.Count == 500) break;
                try
                {
                    var path = Path.Combine(folder, name + ".sql");
                    if (new FileInfo(path).Length > 4_000_000) continue;
                    result.Add(new KeyValuePair<string, string>(name, FirstLine(path)));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is DecoderFallbackException) { }
            }
            return result;
        }

        private static bool IsShortcut(string name)
        {
            if (name.Length == 0) return false;
            foreach (char c in name)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_')) return false;
            return true;
        }

        private static string FirstLine(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    if (line.Length <= 80) return line;
                    return line.Substring(0, char.IsHighSurrogate(line[79]) ? 79 : 80);
                }
                return "";
            }
        }

        /// <summary>Returns the shortcut word ending at <paramref name="caret"/>, or null.</summary>
        public static string? ShortcutBefore(string text, int caret)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (caret < 0 || caret > text.Length) throw new ArgumentOutOfRangeException(nameof(caret));
            int start = caret;
            while (start > 0 && caret - start < 64 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
            if (start == caret || (start > 0 && (text[start - 1] == '@' || text[start - 1] == '#' || text[start - 1] == '.' || text[start - 1] == '['))) return null;
            return text.Substring(start, caret - start);
        }

        /// <summary>Path of the snippet named by <paramref name="shortcut"/>, or null.</summary>
        public static string? FindShortcut(string folder, string shortcut)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrEmpty(shortcut)) return null;
            foreach (char c in shortcut) if (!char.IsLetterOrDigit(c) && c != '_') return null;
            string path = Path.Combine(folder, shortcut + ".sql");
            return File.Exists(path) ? path : null;
        }

        private static void Seed(string path, string text)
        {
            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path)) { return; }
            using (file)
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                writer.Write(text);
        }

        public static string Read(string path)
        {
            if (!string.Equals(Path.GetExtension(path), ".sql", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Snippets must be .sql files.", nameof(path));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 4_000_000) throw new IOException("Snippet file exceeds 4 MB.");
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                {
                    var text = reader.ReadToEnd();
                    if (text.Length > 1_000_000) throw new IOException("Snippet exceeds 1,000,000 characters.");
                    return text;
                }
            }
        }
    }
}

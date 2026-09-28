using System;
using System.IO;
using System.Text;

namespace Querywright.Core
{
    public static class SnippetFiles
    {
        public static void Initialize(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Choose a snippet folder.", nameof(folder));
            Directory.CreateDirectory(folder);
            Seed(Path.Combine(folder, "Select.sql"), "SELECT $SELECTIONSTART$column_name$SELECTIONEND$\r\nFROM table_name;$CURSOR$");
            // File name is the Tab-expansion shortcut, as in SQL Prompt (ssf + Tab).
            Seed(Path.Combine(folder, "ssf.sql"), "SELECT * FROM $CURSOR$");
            Seed(Path.Combine(folder, "sst.sql"), "SELECT TOP 100 * FROM $CURSOR$");
            Seed(Path.Combine(folder, "scf.sql"), "SELECT COUNT(*) FROM $CURSOR$");
            Seed(Path.Combine(folder, "ii.sql"), "INSERT INTO $CURSOR$");
            Seed(Path.Combine(folder, "ups.sql"), "UPDATE $CURSOR$\r\nSET ");
            Seed(Path.Combine(folder, "df.sql"), "DELETE FROM $CURSOR$");
            Seed(Path.Combine(folder, "be.sql"), "BEGIN\r\n    $CURSOR$\r\nEND");
            Seed(Path.Combine(folder, "Create procedure.sql"), "CREATE PROCEDURE dbo.$SELECTIONSTART$ProcedureName$SELECTIONEND$\r\nAS\r\nBEGIN\r\n    SET NOCOUNT ON;\r\n    $CURSOR$\r\nEND;\r\n");
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

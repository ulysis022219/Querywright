using System;
using System.IO;
using System.Text;

namespace SqlWorkbench.Core
{
    public static class SnippetFiles
    {
        public static void Initialize(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Choose a snippet folder.", nameof(folder));
            Directory.CreateDirectory(folder);
            Seed(Path.Combine(folder, "Select.sql"), "SELECT $SELECTIONSTART$column_name$SELECTIONEND$\r\nFROM table_name;$CURSOR$");
            Seed(Path.Combine(folder, "Create procedure.sql"), "CREATE PROCEDURE dbo.$SELECTIONSTART$ProcedureName$SELECTIONEND$\r\nAS\r\nBEGIN\r\n    SET NOCOUNT ON;\r\n    $CURSOR$\r\nEND;\r\n");
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UlysiSQL.Core;

namespace UlysiSQL.Ssms
{
    /// <summary>Model choices only; URL keys are hashes, never API keys or conversation text.</summary>
    internal static class AiModels
    {
        private static readonly string FilePath = Path.Combine(Product.DataFolder, "ai-models.json");
        private static Dictionary<string, object> saved;
        internal static string Key(string baseUrl)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(baseUrl.Trim().TrimEnd('/')))).Replace("-", "");
        }
        private static Dictionary<string, object> Saved
        {
            get
            {
                if (saved != null) return saved;
                try
                {
                    if (System.IO.File.Exists(FilePath) && new FileInfo(FilePath).Length <= 1_000_000)
                        saved = MiniJson.Parse(System.IO.File.ReadAllText(FilePath)) as Dictionary<string, object>;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException) { }
                return saved ?? (saved = new Dictionary<string, object>());
            }
        }
        internal static string Selected(string baseUrl) => MiniJson.Text(MiniJson.Get(Saved, Key(baseUrl)), "selected");
        internal static IReadOnlyList<string> Recent(string baseUrl) =>
            (MiniJson.Get(MiniJson.Get(Saved, Key(baseUrl)), "recent") as IEnumerable<object>)?.OfType<string>().Take(10).ToArray() ?? Array.Empty<string>();
        internal static void Remember(string baseUrl, string model)
        {
            model = model.Trim();
            if (model.Length == 0 || model.Length > 1024 || model.Any(char.IsControl)) throw new InvalidOperationException("Choose a valid model identifier.");
            var recent = new[] { model }.Concat(Recent(baseUrl)).Distinct(StringComparer.Ordinal).Take(10).ToArray();
            Saved[Key(baseUrl)] = new Dictionary<string, object> { ["selected"] = model, ["recent"] = recent };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temporary = FilePath + ".tmp";
            try
            {
                System.IO.File.WriteAllText(temporary, MiniJson.Write(Saved));
                if (System.IO.File.Exists(FilePath)) System.IO.File.Replace(temporary, FilePath, null);
                else System.IO.File.Move(temporary, FilePath);
            }
            finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
        }
    }
}

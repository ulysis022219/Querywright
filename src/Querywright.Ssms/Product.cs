using System;
using System.IO;

namespace Querywright.Ssms
{
    /// <summary>The name users see and the folder that holds local data. Assemblies, namespaces, option storage
    /// and updater files keep the Querywright name so that existing installs keep their settings and can update.</summary>
    internal static class Product
    {
        internal const string Name = "UlysiSQL";

        /// <summary>%LOCALAPPDATA%\UlysiSQL; installs from before the rename keep using %LOCALAPPDATA%\Querywright,
        /// where their settings, snippets, tab history, models and API key already are and saved option paths point.</summary>
        internal static string DataFolder => dataFolder.Value;

        private static readonly Lazy<string> dataFolder = new Lazy<string>(() =>
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string current = Path.Combine(local, Name), legacy = Path.Combine(local, "Querywright");
            return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
        });
    }
}

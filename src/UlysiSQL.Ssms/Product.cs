using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace UlysiSQL.Ssms
{
    /// <summary>The name users see, the folder that holds local data and the places where installs from before the
    /// Querywright to UlysiSQL rename keep their settings.</summary>
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

        /// <summary>Option pages saved before the rename are stored under their Querywright type names: read those
        /// until the page is saved under the new name.</summary>
        internal static string SettingsPath(string path, Func<string, bool> exists)
        {
            string legacy = path.Replace(@"\UlysiSQL.Ssms.", @"\Querywright.Ssms.");
            return legacy != path && !exists(path) && exists(legacy) ? legacy : path;
        }

        /// <summary>Whether SSMS has a user settings collection at this path; false when the store cannot be read.</summary>
        internal static bool UserSettingsExist(string collection)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return new ShellSettingsManager(ServiceProvider.GlobalProvider).GetReadOnlySettingsStore(SettingsScope.UserSettings).CollectionExists(collection);
            }
            catch (Exception error) when (error is COMException || error is InvalidOperationException || error is ArgumentException) { return false; }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Querywright.Core;

namespace Querywright.Ssms
{
    /// <summary>At most daily (or on demand from the menu), asks GitHub for the latest release and shows an info bar when it is newer. Sends no user data.</summary>
    internal sealed class UpdateCheck : IVsInfoBarUIEvents
    {
        private const string Releases = Updates.Repository + "releases/";
        private string tag;
        private WorkbenchPackage package;
        private bool started;

        /// <summary>Version from the installed extension.vsixmanifest (CI stamps the build number there); null when unreadable.</summary>
        internal static string InstalledVersion()
        {
            try
            {
                string manifest = Path.Combine(Path.GetDirectoryName(typeof(UpdateCheck).Assembly.Location), "extension.vsixmanifest");
                return Regex.Match(File.ReadAllText(manifest), "<Identity [^>]*Version=\"([^\"]+)\"").Groups[1].Value;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static async Task RunAsync(WorkbenchPackage package, bool manual = false)
        {
            try
            {
                string stamp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "update-check.txt");
                if (!manual && File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < TimeSpan.FromDays(1)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(stamp));
                File.WriteAllText(stamp, "");

                string installed = InstalledVersion();
                string json;
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Querywright-update-check");
                    json = await client.GetStringAsync("https://api.github.com/repos/ulysis022219/SqlWorkbench/releases/latest").ConfigureAwait(false);
                }
                string tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"(v[0-9.]+)\"").Groups[1].Value;
                if (!Updates.IsNewer(tag, installed))
                {
                    if (manual) await ShowAsync(package, "You have the latest version (" + installed + ").", OLEMSGICON.OLEMSGICON_INFO);
                    return;
                }

                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                var shell = await package.GetServiceAsync(typeof(SVsShell)) as IVsShell;
                var factory = await package.GetServiceAsync(typeof(SVsInfoBarUIFactory)) as IVsInfoBarUIFactory;
                if (shell == null || factory == null
                    || shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object host) != 0 || !(host is IVsInfoBarHost bar)) return;
                var model = new InfoBarModel("Querywright " + tag + " is available (installed " + installed + "). Install the update, then save your work and close SSMS when prompted.",
                    new[] { new InfoBarHyperlink("Install update"), new InfoBarHyperlink("Release notes") }, KnownMonikers.StatusInformation);
                var element = factory.CreateInfoBar(model);
                element.Advise(new UpdateCheck { tag = tag, package = package }, out _);
                bar.AddInfoBar(element);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                ActivityLog.TryLogWarning("Querywright", "Update check skipped: " + error.GetType().Name);
                if (manual) await ShowAsync(package, "Could not check for updates: " + error.Message, OLEMSGICON.OLEMSGICON_WARNING);
            }
        }

        private static async Task ShowAsync(WorkbenchPackage package, string message, OLEMSGICON icon)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.ShowMessageBox(package, message, "Querywright update", icon, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        public void OnActionItemClicked(IVsInfoBarUIElement element, IVsInfoBarActionItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (item.Text == "Release notes")
                {
                    Process.Start(Releases + "tag/" + tag);
                    return;
                }
                if (started) return;
                if (!Regex.IsMatch(tag, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z"))
                    throw new InvalidOperationException("This release does not support direct updates. Open Release notes to install it.");

                // Stage our installed helper scripts outside the extension directory: VSIXInstaller replaces it.
                string source = Path.Combine(Path.GetDirectoryName(typeof(UpdateCheck).Assembly.Location), "Updater");
                string staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Querywright", "Updates", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                foreach (string name in new[] { "Update-Querywright.ps1", "Update-Release.ps1", "Install-Development.ps1", "Test-Package.ps1" })
                    File.Copy(Path.Combine(source, name), Path.Combine(staging, name));
                string ssmsDirectory;
                using (var current = Process.GetCurrentProcess()) ssmsDirectory = Path.GetDirectoryName(current.MainModule.FileName);
                string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                Process.Start(new ProcessStartInfo(powershell,
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(staging, "Update-Querywright.ps1")
                    + "\" -Tag \"" + tag + "\" -SsmsDirectory \"" + ssmsDirectory + "\"") { UseShellExecute = true });
                started = true;
                element.Close();
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                VsShellUtilities.ShowMessageBox(package, "Could not start the update: " + error.Message
                    + "\r\nYou can retry or open Release notes for the manual installer.", "Querywright update",
                    OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        public void OnClosed(IVsInfoBarUIElement element) { }
    }
}

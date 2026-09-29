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
    /// <summary>At most daily, asks GitHub for the latest release and shows an info bar when it is newer. Sends no user data.</summary>
    internal sealed class UpdateCheck : IVsInfoBarUIEvents
    {
        private const string Releases = "https://github.com/ulysis022219/SqlWorkbench/releases/";
        private string url = Releases + "latest";

        internal static async Task RunAsync(WorkbenchPackage package)
        {
            try
            {
                string stamp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Querywright", "update-check.txt");
                if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < TimeSpan.FromDays(1)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(stamp));
                File.WriteAllText(stamp, "");

                string manifest = Path.Combine(Path.GetDirectoryName(typeof(UpdateCheck).Assembly.Location), "extension.vsixmanifest");
                string installed = Regex.Match(File.ReadAllText(manifest), "<Identity [^>]*Version=\"([^\"]+)\"").Groups[1].Value;
                string json;
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Querywright-update-check");
                    json = await client.GetStringAsync("https://api.github.com/repos/ulysis022219/SqlWorkbench/releases/latest").ConfigureAwait(false);
                }
                string tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"(v[0-9.]+)\"").Groups[1].Value;
                if (!Updates.IsNewer(tag, installed)) return;

                await package.JoinableTaskFactory.SwitchToMainThreadAsync();
                var shell = await package.GetServiceAsync(typeof(SVsShell)) as IVsShell;
                var factory = await package.GetServiceAsync(typeof(SVsInfoBarUIFactory)) as IVsInfoBarUIFactory;
                if (shell == null || factory == null
                    || shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object host) != 0 || !(host is IVsInfoBarHost bar)) return;
                var model = new InfoBarModel("Querywright " + tag + " is available (installed " + installed + "). Close SSMS and run Install.cmd from the new zip.",
                    new[] { new InfoBarHyperlink("Download") }, KnownMonikers.StatusInformation);
                var element = factory.CreateInfoBar(model);
                element.Advise(new UpdateCheck { url = Releases + "tag/" + tag }, out _);
                bar.AddInfoBar(element);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                ActivityLog.TryLogWarning("Querywright", "Update check skipped: " + error.GetType().Name);
            }
        }

        public void OnActionItemClicked(IVsInfoBarUIElement element, IVsInfoBarActionItem item)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Process.Start(url);
            element.Close();
        }

        public void OnClosed(IVsInfoBarUIElement element) { }
    }
}

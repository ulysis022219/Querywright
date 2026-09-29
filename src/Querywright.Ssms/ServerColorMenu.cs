using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;
using Querywright.Core;

namespace Querywright.Ssms
{
    /// <summary>
    /// Adds "Querywright: server color..." to the Object Explorer right-click menu of a server node. Object Explorer has no
    /// public menu API, so its WinForms tree is found by reflection and the item is added to the menu once it is shown.
    /// </summary>
    internal static class ServerColorMenu
    {
        private const string ItemName = "QuerywrightServerColor";
        private static TreeView tree;

        internal static string Status = "not attached";

        internal static void Start()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // ponytail: Object Explorer may be created after the package loads; retry every 5 s until its tree exists.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (sender, args) => { if (TryAttach()) timer.Stop(); };
            timer.Start();
            if (TryAttach()) timer.Stop();
        }

        private static bool TryAttach()
        {
            try
            {
                var cache = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache", false)).FirstOrDefault(t => t != null);
                if (cache == null) { Status = "no ServiceCache type"; return false; }
                var explorer = cache.GetMethod("GetObjectExplorer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
                // SSMS 22: the explorer is only reachable as a service.
                var service = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService", false)).FirstOrDefault(t => t != null);
                if (explorer == null && service != null) explorer = ServiceProvider.GlobalProvider.GetService(service);
                if (explorer == null) { Status = "no object explorer" + (service == null ? " service type" : ""); return false; }
                var property = explorer.GetType().GetProperty("Tree", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property == null) { Status = "no Tree property on " + explorer.GetType().FullName; return false; }
                var value = property.GetValue(explorer);
                tree = value as TreeView;
                if (tree == null) { Status = "Tree is " + (value?.GetType().FullName ?? "null"); return false; }
                new ContextMenuWatcher(tree);
                Status = "attached";
                return true;
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                Status = "attach failed: " + error.GetType().Name;
                return false;
            }
        }

        /// <summary>Sees WM_CONTEXTMENU (mouse or keyboard) and adds the item to the menu Object Explorer just opened.</summary>
        private sealed class ContextMenuWatcher : NativeWindow
        {
            internal ContextMenuWatcher(TreeView tree)
            {
                if (tree.IsHandleCreated) AssignHandle(tree.Handle);
                tree.HandleCreated += (sender, args) => AssignHandle(tree.Handle);
                tree.HandleDestroyed += (sender, args) => ReleaseHandle();
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == 0x007B) tree.BeginInvoke(new Action(AddToOpenMenu)); // WM_CONTEXTMENU
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        /// <summary>Self-test: opens the first server node's menu as a right-click would and reports what it holds.</summary>
        internal static async System.Threading.Tasks.Task<string> ProbeAsync(WorkbenchPackage package)
        {
            for (int i = 0; i < 30 && (tree == null || tree.Nodes.Count == 0); i++) { await System.Threading.Tasks.Task.Delay(1000); await package.JoinableTaskFactory.SwitchToMainThreadAsync(); }
            if (tree == null || tree.Nodes.Count == 0) return Status + (tree == null ? "" : ", no server nodes");
            tree.SelectedNode = tree.Nodes[0];
            var point = tree.PointToScreen(new System.Drawing.Point(tree.Nodes[0].Bounds.X + 5, tree.Nodes[0].Bounds.Y + 5));
            SendMessage(tree.Handle, 0x007B, tree.Handle, (IntPtr)((point.Y << 16) | (point.X & 0xFFFF)));
            await System.Threading.Tasks.Task.Delay(1500);
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var menus = OpenMenus();
            string result = menus.Any(m => m.Items.ContainsKey(ItemName)) ? "menu item added" : "no item; open menus " + menus.Length + ", tree " + tree.GetType().Name + ", strip " + (tree.ContextMenuStrip?.GetType().Name ?? "none");
            foreach (var menu in menus) menu.Close();
            return result;
        }

        /// <summary>Open ContextMenuStrips, from WinForms' internal list of live tool strips.</summary>
        internal static ContextMenuStrip[] OpenMenus()
        {
            var strips = typeof(ToolStripManager).GetProperty("ToolStrips", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IList;
            if (strips == null) return Array.Empty<ContextMenuStrip>();
            // The collection holds weak references; its indexer returns the live strip.
            return Enumerable.Range(0, strips.Count).Select(i => strips[i]).OfType<ContextMenuStrip>().Where(s => s.Visible).ToArray();
        }

        private static void AddToOpenMenu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var node = tree?.SelectedNode;
            // Server nodes are the tree's roots; text is "server (SQL Server 16.0... - login)".
            if (node == null || node.Parent != null) return;
            int paren = node.Text.IndexOf(" (", StringComparison.Ordinal);
            string server = paren > 0 ? node.Text.Substring(0, paren) : node.Text;
            foreach (var menu in OpenMenus().Where(m => !m.Items.ContainsKey(ItemName)))
            {
                string pattern = ColorRules.ServerPattern(server);
                string current = ColorRules.Get(WorkbenchPackage.Instance?.TabColorRules, pattern);
                var item = new ToolStripMenuItem("Querywright: server color...") { Name = ItemName };
                item.Click += (sender, args) => Pick(pattern, current);
                if (current != null)
                    item.DropDownItems.AddRange(new ToolStripItem[]
                    {
                        new ToolStripMenuItem("Change color...", null, (sender, args) => Pick(pattern, current)),
                        new ToolStripMenuItem("Clear color", null, (sender, args) => Save(pattern, null)),
                    });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(item);
            }
        }

        private static void Pick(string pattern, string current)
        {
            using (var dialog = new ColorDialog { FullOpen = true, AnyColor = true })
            {
                try { if (current != null) dialog.Color = System.Drawing.ColorTranslator.FromHtml(current); } catch (Exception) { }
                if (dialog.ShowDialog() != DialogResult.OK) return;
                var c = dialog.Color;
                Save(pattern, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
            }
        }

        private static void Save(string pattern, string color)
        {
            try { WorkbenchPackage.Instance?.SetTabColorRules(ColorRules.Set(WorkbenchPackage.Instance.TabColorRules, pattern, color)); }
            catch (ArgumentException error) { MessageBox.Show(error.Message, "Querywright", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}

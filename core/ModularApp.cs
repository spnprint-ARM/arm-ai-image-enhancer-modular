using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Management;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class ModularApp
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { ReadCatalog(); }
        catch (Exception ex)
        {
            if (args.Length > 0 && args[0] == "--self-check") return 2;
            MessageBox.Show("The module catalog could not be read.\n\n" + ex.Message,
                "ARM AI Image Enhancer Modular", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        if (args.Length > 0 && args[0] == "--self-check") return 0;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }

    private static Dictionary<string, object> ReadCatalog()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "module-catalog.json");
        if (!File.Exists(path)) throw new FileNotFoundException("The modular package catalog is missing.", path);
        JavaScriptSerializer parser = new JavaScriptSerializer();
        Dictionary<string, object> catalog = parser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        if (catalog == null || !catalog.ContainsKey("schema_version") || !catalog.ContainsKey("packages"))
            throw new InvalidDataException("The modular package catalog is incomplete.");
        if (Convert.ToInt32(catalog["schema_version"]) != 1 || !(catalog["packages"] is ArrayList))
            throw new InvalidDataException("The modular package catalog format is unsupported.");
        return catalog;
    }

    private sealed class MainForm : Form
    {
        private readonly Label adapterInfo;
        private readonly Label status;
        private readonly FlowLayoutPanel packages;

        internal MainForm()
        {
            Text = "ARM AI Image Enhancer — Modular";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(680, 480);
            ClientSize = new Size(760, 570);
            Font = new Font("Tahoma", 9F);

            Label title = new Label { Left = 22, Top = 18, Width = 710, Height = 34,
                Text = "ARM AI IMAGE ENHANCER", Font = new Font("Tahoma", 18F, FontStyle.Bold) };
            Label subtitle = new Label { Left = 23, Top = 54, Width = 700, Height = 25,
                Text = "Modular Edition — AI components are managed separately" };
            Controls.Add(title); Controls.Add(subtitle);

            GroupBox deviceBox = new GroupBox { Left = 18, Top = 92, Width = 724, Height = 112, Text = "This computer" };
            adapterInfo = new Label { Left = 14, Top = 24, Width = 690, Height = 48,
                Text = "Reading graphics adapter information…", AutoEllipsis = true };
            Button refresh = new Button { Left = 568, Top = 73, Width = 136, Height = 26, Text = "Refresh devices" };
            refresh.Click += delegate { StartDeviceScan(); };
            deviceBox.Controls.Add(adapterInfo); deviceBox.Controls.Add(refresh); Controls.Add(deviceBox);

            GroupBox componentBox = new GroupBox { Left = 18, Top = 214, Width = 724, Height = 274, Text = "Optional components" };
            packages = new FlowLayoutPanel { Left = 12, Top = 23, Width = 696, Height = 236,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            componentBox.Controls.Add(packages); Controls.Add(componentBox);
            PopulatePackages();

            status = new Label { Left = 22, Top = 503, Width = 710, Height = 40,
                Text = "CPU is the compatibility baseline. GPU acceleration is enabled only after a runtime check.", AutoEllipsis = true };
            Controls.Add(status);
            StartDeviceScan();
        }

        private void PopulatePackages()
        {
            ArrayList items = (ArrayList)ReadCatalog()["packages"];
            foreach (object value in items)
            {
                Dictionary<string, object> item = value as Dictionary<string, object>;
                if (item == null) continue;
                string id = Convert.ToString(item.ContainsKey("id") ? item["id"] : "unnamed package");
                if (id == "core") continue;
                bool ready = IsInstallReady(item);
                string description = ready ? "Verified package is available to install." : "This optional package has not been published yet.";
                Panel row = new Panel { Width = 676, Height = 46, Margin = new Padding(3, 2, 3, 2) };
                row.Controls.Add(new Label { Left = 4, Top = 4, Width = 225, Height = 20,
                    Text = id, Font = new Font("Tahoma", 9F, FontStyle.Bold) });
                row.Controls.Add(new Label { Left = 232, Top = 4, Width = 320, Height = 36,
                    Text = description, AutoEllipsis = true });
                row.Controls.Add(new Label { Left = 558, Top = 4, Width = 108, Height = 20,
                    Text = ready ? "Available" : "Not published", TextAlign = ContentAlignment.MiddleRight });
                packages.Controls.Add(row);
            }
        }

        private static bool IsInstallReady(Dictionary<string, object> item)
        {
            object url, digest, version, size, license;
            return item.TryGetValue("download", out url) && url is string && ((string)url).StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && item.TryGetValue("sha256", out digest) && digest is string && ((string)digest).Length == 64
                && item.TryGetValue("version", out version) && version != null
                && item.TryGetValue("archive_size_bytes", out size) && size is int && (int)size > 0
                && item.TryGetValue("license", out license) && license != null;
        }

        private void StartDeviceScan()
        {
            adapterInfo.Text = "Checking device information…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string report = ReadAdapterReport();
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (!IsDisposed) { adapterInfo.Text = report; status.Text = "Inventory is advisory; actual GPU support requires an inference check."; }
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private static string ReadAdapterReport()
        {
            List<string> names = new List<string>();
            string source = "WMI/CIM";
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController"))
                using (ManagementObjectCollection rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows)
                    {
                        string name = Convert.ToString(row["Name"]);
                        string version = Convert.ToString(row["DriverVersion"]);
                        if (!String.IsNullOrWhiteSpace(name)) names.Add("• " + name + (String.IsNullOrWhiteSpace(version) ? "" : " (" + version + ")"));
                    }
                }
            }
            catch { names.Clear(); source = "Windows registry (advisory)"; }
            if (names.Count == 0)
            {
                try
                {
                    using (RegistryKey root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video"))
                    {
                        if (root != null)
                            foreach (string guid in root.GetSubKeyNames())
                            using (RegistryKey group = root.OpenSubKey(guid))
                            {
                                if (group == null) continue;
                                foreach (string subkey in group.GetSubKeyNames())
                                using (RegistryKey adapter = group.OpenSubKey(subkey))
                                {
                                    if (adapter == null) continue;
                                    string name = Convert.ToString(adapter.GetValue("DriverDesc"));
                                    string version = Convert.ToString(adapter.GetValue("DriverVersion"));
                                    if (!String.IsNullOrWhiteSpace(name) && !names.Contains("• " + name + (String.IsNullOrWhiteSpace(version) ? "" : " (" + version + ")")))
                                        names.Add("• " + name + (String.IsNullOrWhiteSpace(version) ? "" : " (" + version + ")"));
                                }
                            }
                    }
                }
                catch { }
            }
            if (names.Count == 0) return "Adapter information is not accessible. CPU remains the safe baseline.";
            return String.Join(Environment.NewLine, names.ToArray()) + Environment.NewLine + "Inventory source: " + source + "; adapter capability is not yet validated.";
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.IO.Compression;
using Microsoft.Win32;

internal static class ModularApp
{
    [STAThread]
    private static int Main(string[] args)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
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
        private readonly Label upscaleStatus;
        private readonly Button upscaleAction;
        private readonly ProgressBar upscaleProgress;
        private readonly Label upscalePercent;
        private readonly string moduleRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArmAI", "ImageEnhancer-Modular", "modules");
        private const string PackageAssetName = "module-upscale-ncnn-vulkan-windows-x64.zip";
        private const string PackageId = "upscale-ncnn-vulkan-windows-x64";

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
            Panel upscaleRow = new Panel { Width = 676, Height = 76, Margin = new Padding(3, 2, 3, 2) };
            upscaleRow.Controls.Add(new Label { Left = 4, Top = 4, Width = 420, Height = 20,
                Text = "Real-ESRGAN NCNN/Vulkan — Windows x64", Font = new Font("Tahoma", 9F, FontStyle.Bold) });
            upscaleStatus = new Label { Left = 4, Top = 27, Width = 506, Height = 38,
                Text = "Checking the latest verified package…", AutoEllipsis = true };
            upscaleAction = new Button { Left = 526, Top = 7, Width = 138, Height = 28, Text = "Check package" };
            upscaleAction.Click += PackageActionClick;
            upscaleProgress = new ProgressBar { Left = 526, Top = 42, Width = 100, Height = 18, Visible = false };
            upscalePercent = new Label { Left = 630, Top = 42, Width = 34, Height = 18, TextAlign = ContentAlignment.MiddleRight, Visible = false };
            upscaleRow.Controls.Add(upscaleStatus); upscaleRow.Controls.Add(upscaleAction);
            upscaleRow.Controls.Add(upscaleProgress); upscaleRow.Controls.Add(upscalePercent);
            packages.Controls.Add(upscaleRow);
            packages.Controls.Add(new Label { Width = 660, Height = 30, Text = "Face recovery and other model packs will appear here after their packages are released." });
            CheckInstalledPackage();

            status = new Label { Left = 22, Top = 503, Width = 710, Height = 40,
                Text = "CPU is the compatibility baseline. GPU acceleration is enabled only after a runtime check.", AutoEllipsis = true };
            Controls.Add(status);
            StartDeviceScan();
            CheckPackageAsync();
        }

        private void CheckInstalledPackage()
        {
            string installed = Path.Combine(moduleRoot, PackageId, "realesrgan-ncnn-vulkan.exe");
            if (File.Exists(installed))
            {
                upscaleStatus.Text = "Installed. Vulkan acceleration depends on a compatible GPU driver; CPU fallback is not included.";
                upscaleAction.Text = "Installed";
                upscaleAction.Enabled = false;
            }
        }

        private void CheckPackageAsync()
        {
            if (IsDisposed) return;
            if (File.Exists(Path.Combine(moduleRoot, PackageId, "realesrgan-ncnn-vulkan.exe")))
            {
                upscaleStatus.Text = "Installed. Vulkan acceleration depends on a compatible GPU driver; image-processing integration is still in development.";
                upscaleAction.Text = "Installed"; upscaleAction.Enabled = false;
                return;
            }
            upscaleAction.Enabled = false;
            upscaleStatus.Text = "Checking GitHub for the latest package…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/spnprint-ARM/arm-ai-image-enhancer-modular/releases/latest");
                    request.UserAgent = "ARM-AI-Image-Enhancer-Modular";
                    request.Accept = "application/vnd.github+json";
                    using (WebResponse response = request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        Dictionary<string, object> release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                        ArrayList assets = release["assets"] as ArrayList;
                        Dictionary<string, object> found = null;
                        if (assets != null)
                            foreach (object raw in assets)
                            {
                                Dictionary<string, object> asset = raw as Dictionary<string, object>;
                                if (asset != null && String.Equals(Convert.ToString(asset["name"]), PackageAssetName, StringComparison.Ordinal)) { found = asset; break; }
                            }
                        if (found == null) { UpdatePackageState("The AI package has not been published yet.", "Check again", true); return; }
                        string digest = Convert.ToString(found.ContainsKey("digest") ? found["digest"] : "");
                        string url = Convert.ToString(found.ContainsKey("browser_download_url") ? found["browser_download_url"] : "");
                        long size = Convert.ToInt64(found.ContainsKey("size") ? found["size"] : 0);
                        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71 || !IsHexDigest(digest.Substring(7)) || size < 1 || !url.StartsWith("https://github.com/spnprint-ARM/arm-ai-image-enhancer-modular/releases/download/", StringComparison.OrdinalIgnoreCase))
                        { UpdatePackageState("GitHub package metadata is incomplete; download disabled for safety.", "Check again", true); return; }
                        UpdatePackageState("Package available (" + FormatSize(size) + "). Vulkan-capable GPU required; compatibility is not yet validated.", "Install", true);
                        packageUrl = url; packageDigest = digest.Substring(7).ToLowerInvariant(); packageSize = size;
                    }
                }
                catch (Exception ex) { UpdatePackageState("Could not check GitHub: " + ex.Message, "Retry", true); }
            });
        }

        private string packageUrl;
        private string packageDigest;
        private long packageSize;

        private void UpdatePackageState(string message, string action, bool enabled)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { if (!IsDisposed) { upscaleStatus.Text = message; upscaleAction.Text = action; upscaleAction.Enabled = enabled; } }); }
            catch (InvalidOperationException) { }
        }

        private void PackageActionClick(object sender, EventArgs e)
        {
            if (String.Equals(upscaleAction.Text, "Install", StringComparison.Ordinal) && !String.IsNullOrEmpty(packageUrl)) InstallPackageAsync();
            else CheckPackageAsync();
        }

        private void InstallPackageAsync()
        {
            string staging = Path.Combine(moduleRoot, PackageId + ".staging");
            string archive = Path.Combine(moduleRoot, PackageId + ".download.zip");
            upscaleAction.Enabled = false; upscaleProgress.Visible = true; upscalePercent.Visible = true;
            upscaleProgress.Value = 0; upscalePercent.Text = "0%"; upscaleStatus.Text = "Downloading and verifying package…";
            ThreadPool.QueueUserWorkItem(delegate
            {
                string backup = Path.Combine(moduleRoot, PackageId + ".previous");
                string target = Path.Combine(moduleRoot, PackageId);
                try
                {
                    Directory.CreateDirectory(moduleRoot);
                    if (Directory.Exists(staging)) Directory.Delete(staging, true);
                    if (File.Exists(archive)) File.Delete(archive);
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(packageUrl);
                    request.UserAgent = "ARM-AI-Image-Enhancer-Modular";
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream input = response.GetResponseStream())
                    using (FileStream output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[65536]; long total = 0; int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read); total += read;
                            int percent = (int)Math.Min(100, total * 100 / packageSize);
                            UpdateProgress(percent);
                        }
                        output.Flush();
                    }
                    FileInfo info = new FileInfo(archive);
                    if (info.Length != packageSize) throw new InvalidDataException("Downloaded package size does not match GitHub metadata.");
                    string actual;
                    using (FileStream file = File.OpenRead(archive)) using (SHA256 sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                    if (!String.Equals(actual, packageDigest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 check failed. The package was not installed.");
                    Directory.CreateDirectory(staging);
                    using (ZipArchive zip = ZipFile.OpenRead(archive))
                    {
                        string prefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                        foreach (ZipArchiveEntry entry in zip.Entries)
                        {
                            string destination = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The package contains an unsafe archive path.");
                            if (String.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                            Directory.CreateDirectory(Path.GetDirectoryName(destination));
                            entry.ExtractToFile(destination, true);
                        }
                    }
                    if (!File.Exists(Path.Combine(staging, "realesrgan-ncnn-vulkan.exe"))) throw new InvalidDataException("The package is missing the expected Real-ESRGAN executable.");
                    if (Directory.Exists(backup)) Directory.Delete(backup, true);
                    if (Directory.Exists(target)) Directory.Move(target, backup);
                    try { Directory.Move(staging, target); }
                    catch { if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target); throw; }
                    if (Directory.Exists(backup)) Directory.Delete(backup, true);
                    File.Delete(archive);
                    UpdatePackageState("Installed. This adds the Vulkan engine; image-processing integration is still in development.", "Installed", false);
                }
                catch (Exception ex)
                {
                    try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                    try { if (File.Exists(archive)) File.Delete(archive); } catch { }
                    UpdatePackageState("Installation failed; temporary files were cleaned. " + ex.Message, "Retry", true);
                }
                finally { UpdateProgress(-1); }
            });
        }

        private void UpdateProgress(int value)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { if (IsDisposed) return; if (value < 0) { upscaleProgress.Visible = false; upscalePercent.Visible = false; } else { upscaleProgress.Value = Math.Max(0, Math.Min(100, value)); upscalePercent.Text = value + "%"; } }); }
            catch (InvalidOperationException) { }
        }

        private static string FormatSize(long bytes) { return bytes >= 1073741824 ? (bytes / 1073741824.0).ToString("0.0") + " GB" : (bytes / 1048576.0).ToString("0") + " MB"; }

        private static bool IsHexDigest(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
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

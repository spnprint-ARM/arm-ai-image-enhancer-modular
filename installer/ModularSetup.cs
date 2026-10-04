using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class ModularSetup
{
    private static System.Threading.Mutex setupMutex;
    private const string Magic = "ARMIMOD1";
    private const int FooterLength = 48;
    private const string AppFile = "ArmAIImageEnhancerModular.exe";
    private const string UninstallerFile = "UninstallModular.exe";
    private const string IconEntry = "_internal/assets/ARM.ico";
    private const string InstallPath = @"C:\Program Files\ArmAI\ImageEnhancer-Modular";
    private const string ProductKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ArmAIImageEnhancerModular";
    private const long MaximumExpandedBytes = 1073741824L;
    private const long SafetyBytes = 104857600L;

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string exeName = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
        if (args.Length > 0 && args[0] == "--verify-payload")
        {
            string targetFile = args.Length > 1 ? args[1] : Application.ExecutablePath;
            try
            {
                long expanded; VerifyPayload(targetFile, out expanded);
                using (Image qr = ReadQrImage(targetFile)) { }
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                string logPath = Environment.GetEnvironmentVariable("ARMAI_MODULAR_VERIFY_LOG");
                if (!String.IsNullOrEmpty(logPath))
                    try { File.WriteAllText(logPath, ex.ToString()); } catch { }
                Environment.ExitCode = 2;
            }
            return;
        }
        if ((args.Length > 0 && args[0] == "--uninstall") || exeName == "UninstallModular")
        {
            Uninstall();
            return;
        }
        bool installWorker = args.Length > 0 && args[0] == "--install-worker";
        bool createDesktopShortcut = args.Length < 2 || args[1] != "0";
        bool created;
        setupMutex = new System.Threading.Mutex(true, installWorker ? @"Local\ArmAIImageEnhancerModularSetupWorker" : @"Local\ArmAIImageEnhancerModularSetup", out created);
        if (!created)
        {
            MessageBox.Show("The Modular Edition setup is already running.", "ARM AI Image Enhancer Modular Setup",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { Application.Run(new StartupForm(installWorker, createDesktopShortcut)); }
        catch (Exception ex)
        {
            MessageBox.Show("Unable to prepare the Modular Edition installer.\n\n" + ex.Message,
                "ARM AI Image Enhancer Modular Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed class StartupForm : Form
    {
        private readonly bool installWorker;
        private readonly bool createDesktopShortcut;

        internal StartupForm(bool worker, bool desktopShortcut)
        {
            installWorker = worker;
            createDesktopShortcut = desktopShortcut;
            Text = "ARM AI Image Enhancer Modular V1.11 — Setup";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(470, 125);
            Controls.Add(new Label { Left = 18, Top = 16, Width = 434, Height = 28,
                Text = "ARM AI IMAGE ENHANCER — MODULAR", Font = new Font("Tahoma", 11, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter });
            Controls.Add(new Label { Left = 18, Top = 49, Width = 434, Height = 20,
                Text = "Preparing setup — please wait; do not open this file again…", Font = new Font("Tahoma", 9), TextAlign = ContentAlignment.MiddleCenter });
            Controls.Add(new ProgressBar { Left = 18, Top = 80, Width = 434, Height = 18,
                Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24 });
            Shown += delegate { System.Threading.ThreadPool.QueueUserWorkItem(delegate { Inspect(); }); };
        }

        private void Inspect()
        {
            try
            {
                long expanded;
                VerifyPayload(Application.ExecutablePath, out expanded);
                Image qr = ReadQrImage(Application.ExecutablePath);
                BeginInvoke((Action)delegate
                {
                    Hide();
                    using (InstallForm form = new InstallForm(expanded, qr, installWorker, createDesktopShortcut)) form.ShowDialog();
                    Close();
                });
            }
            catch (Exception ex)
            {
                BeginInvoke((Action)delegate
                {
                    MessageBox.Show(this, "Installer integrity check failed. The application was not installed.\n\n" + ex.Message,
                        "ARM AI Image Enhancer Modular Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                });
            }
        }
    }

    private static void VerifyPayload(string installerPath, out long expanded)
    {
        expanded = 0;
        using (FileStream source = File.OpenRead(installerPath))
        {
            long start, length;
            byte[] expected;
            ReadBounds(source, out start, out length, out expected);
            using (SegmentStream segment = new SegmentStream(source, start, length))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] actual = sha.ComputeHash(segment);
                if (!FixedTimeEquals(actual, expected)) throw new InvalidDataException("Setup payload SHA-256 does not match.");
            }
            using (SegmentStream segment = new SegmentStream(source, start, length))
            using (ZipArchive archive = new ZipArchive(segment, ZipArchiveMode.Read, true))
            {
                bool appFound = false, iconFound = false, uninstallerFound = false, qrFound = false;
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!names.Add(entry.FullName)) throw new InvalidDataException("Duplicate file in setup payload: " + entry.FullName);
                    expanded = checked(expanded + entry.Length);
                    if (expanded > MaximumExpandedBytes) throw new InvalidDataException("Setup payload expands beyond the safety limit.");
                    RejectUnsafePath(entry.FullName);
                    if (entry.FullName == AppFile && entry.Length > 0) appFound = true;
                    if (entry.FullName == IconEntry && entry.Length > 0) iconFound = true;
                    if (entry.FullName == UninstallerFile && entry.Length > 0) uninstallerFound = true;
                    if (entry.FullName == "assets/QR.jpg" && entry.Length > 0) qrFound = true;
                }
                if (!appFound) throw new InvalidDataException("Required application entry is missing: " + AppFile);
                if (!iconFound) throw new InvalidDataException("Required application icon is missing: " + IconEntry);
                if (!uninstallerFound) throw new InvalidDataException("Required uninstaller entry is missing: " + UninstallerFile);
                if (!qrFound) throw new InvalidDataException("Required PromptPay QR image is missing.");
            }
        }
        if (expanded <= 0) throw new InvalidDataException("Setup payload is empty.");
    }

    private static Image ReadQrImage(string installerPath)
    {
        using (FileStream source = File.OpenRead(installerPath))
        {
            long start, length; byte[] digest;
            ReadBounds(source, out start, out length, out digest);
            using (SegmentStream segment = new SegmentStream(source, start, length))
            using (ZipArchive archive = new ZipArchive(segment, ZipArchiveMode.Read, true))
            {
                ZipArchiveEntry entry = archive.GetEntry("assets/QR.jpg");
                if (entry == null) throw new InvalidDataException("PromptPay QR image is missing.");
                using (Stream input = entry.Open())
                using (MemoryStream memory = new MemoryStream())
                {
                    input.CopyTo(memory);
                    memory.Position = 0;
                    using (Image decoded = Image.FromStream(memory)) return new Bitmap(decoded);
                }
            }
        }
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        int diff = 0;
        for (int i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
        return diff == 0;
    }

    private static void ReadBounds(Stream source, out long start, out long length, out byte[] digest)
    {
        if (source.Length < FooterLength) throw new InvalidDataException("Setup footer is missing.");
        source.Position = source.Length - FooterLength;
        byte[] footer = new byte[FooterLength];
        ReadExactly(source, footer, 0, footer.Length);
        if (System.Text.Encoding.ASCII.GetString(footer, 0, 8) != Magic) throw new InvalidDataException("Setup marker is invalid.");
        start = BitConverter.ToInt64(footer, 8);
        length = source.Length - FooterLength - start;
        digest = new byte[32];
        Buffer.BlockCopy(footer, 16, digest, 0, digest.Length);
        if (start <= 0 || length <= 0 || start >= source.Length - FooterLength) throw new InvalidDataException("Setup payload bounds are invalid.");
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = stream.Read(buffer, offset, count);
            if (read <= 0) throw new EndOfStreamException("Setup file ended unexpectedly.");
            offset += read;
            count -= read;
        }
    }

    private static void RejectUnsafePath(string entryName)
    {
        if (String.IsNullOrEmpty(entryName) || entryName.IndexOf('\0') >= 0 || entryName.StartsWith("/", StringComparison.Ordinal) ||
            entryName.Contains(":") || entryName.StartsWith("\\", StringComparison.Ordinal))
            throw new InvalidDataException("Unsafe setup path: " + entryName);
        string normalized = entryName.Replace('\\', '/');
        foreach (string part in normalized.Split('/')) if (part == "..") throw new InvalidDataException("Unsafe setup path: " + entryName);
    }

    private sealed class InstallForm : Form
    {
        private readonly long expanded;
        private readonly Label status;
        private readonly ProgressBar progress;
        private readonly Button install;
        private readonly Button cancel;
        private readonly CheckBox desktop;
        private readonly Image qrImage;

        private readonly bool installWorker;

        internal InstallForm(long expandedBytes, Image qr, bool worker, bool desktopShortcut)
        {
            installWorker = worker;
            expanded = expandedBytes;
            qrImage = qr;
            Text = "ARM AI Image Enhancer Modular V1.11 — Setup";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(800, 500);
            Controls.Add(new Label { Left = 22, Top = 16, Width = 756, Height = 32,
                Text = "Install ARM AI Image Enhancer — Modular V1.11", Font = new Font("Tahoma", 14, FontStyle.Bold) });
            Controls.Add(new Label { Left = 22, Top = 55, Width = 475, Height = 42,
                Text = "Install location: " + InstallPath + "\nThe optional Vulkan engine can be downloaded after installation.", Font = new Font("Tahoma", 9) });
            Controls.Add(new Label { Left = 22, Top = 104, Width = 475, Height = 132,
                Text = "Important information\n• Windows 10/11, 64-bit\n• Optional Real-ESRGAN Vulkan component downloads after setup\n• Requires a compatible Vulkan GPU and graphics driver; no CPU fallback\n• Batch JPEG/PNG/WebP, output as source/PNG/JPG/TIFF, stable 4x only\n• 2x and 3x are temporarily unavailable due to tile artifacts\n• Device inventory does not confirm Vulkan compatibility",
                Font = new Font("Tahoma", 9) });
            double sizeMb = expanded / (1024.0 * 1024.0);
            long free = new DriveInfo(Path.GetPathRoot(InstallPath)).AvailableFreeSpace;
            long required = expanded + SafetyBytes;
            Controls.Add(new Label { Left = 22, Top = 244, Width = 475, Height = 52,
                Text = String.Format("Core files: {0:N2} MB\nFree on C: {1:N2} GB   •   Required: {2:N2} GB (core plus 100 MB working reserve)", sizeMb, free / 1073741824.0, required / 1073741824.0),
                Font = new Font("Tahoma", 9, FontStyle.Bold) });
            Controls.Add(new Label { Left = 522, Top = 58, Width = 250, Height = 62,
                Text = "ช่วยสนับสนุนการพัฒนาโปรแกรม\n5 บาท 10 บาท ได้หมดครับ\nแล้วแต่ศรัทธา 😆", Font = new Font("Tahoma", 10, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter });
            PictureBox qrBox = new PictureBox { Left = 562, Top = 124, Width = 170, Height = 250,
                SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Image = qrImage };
            Controls.Add(qrBox);
            Controls.Add(new Label { Left = 522, Top = 380, Width = 250, Height = 24,
                Text = "Scan PromptPay QR to support", Font = new Font("Tahoma", 9), TextAlign = ContentAlignment.MiddleCenter });
            status = new Label { Left = 22, Top = 306, Width = 475, Height = 22, Text = "Ready to install.", Font = new Font("Tahoma", 9) };
            progress = new ProgressBar { Left = 22, Top = 333, Width = 475, Height = 18, Minimum = 0, Maximum = 100, Visible = false };
            desktop = new CheckBox { Left = 22, Top = 439, Width = 300, Height = 26, Text = "Create a desktop shortcut", Checked = true, Font = new Font("Tahoma", 9) };
            desktop.Checked = desktopShortcut;
            install = new Button { Left = 342, Top = 435, Width = 100, Height = 32, Text = "Install", Enabled = free >= required };
            cancel = new Button { Left = 454, Top = 435, Width = 100, Height = 32, Text = "Cancel" };
            install.Click += delegate { BeginInstall(); };
            cancel.Click += delegate { Close(); };
            Controls.Add(status); Controls.Add(progress); Controls.Add(desktop); Controls.Add(install); Controls.Add(cancel);
            if (!install.Enabled) status.Text = "Not enough free space on the installation drive.";
            FormClosed += delegate { if (qrImage != null) qrImage.Dispose(); qrBox.Image = null; };
            if (installWorker) Shown += delegate { BeginInvoke((Action)BeginInstall); };
        }

        private void BeginInstall()
        {
            if (!IsAdministrator())
            {
                try
                {
                    ProcessStartInfo elevated = new ProcessStartInfo(Application.ExecutablePath,
                        "--install-worker " + (desktop.Checked ? "1" : "0"));
                    elevated.Verb = "runas";
                    elevated.UseShellExecute = true;
                    Process.Start(elevated);
                    Close();
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 1223)
                    {
                        MessageBox.Show(this, "Administrator approval was cancelled. The installer is still open; click Install again when ready.",
                            "Installation not started", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    MessageBox.Show(this, "Could not start the elevated installer.\n\n" + ex.Message,
                        "Installation not started", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not start the elevated installer.\n\n" + ex.Message,
                        "Installation not started", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                return;
            }
            string driveRoot = Path.GetPathRoot(InstallPath);
            long free = new DriveInfo(driveRoot).AvailableFreeSpace;
            if (free < expanded + SafetyBytes)
            {
                MessageBox.Show(this, "Not enough free space. Free at least " + ((expanded + SafetyBytes) / 1073741824.0).ToString("N1") + " GB and try again.",
                    "Insufficient disk space", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            install.Enabled = false;
            cancel.Enabled = false;
            ControlBox = false;
            progress.Visible = true;
            status.Text = "Preparing files…";
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { InstallFiles(); });
        }

        private static bool IsAdministrator()
        {
            using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private void SetProgress(string text, int value)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)delegate { if (!IsDisposed) { status.Text = text; progress.Value = Math.Max(0, Math.Min(100, value)); } });
        }

        private void InstallFiles()
        {
            // Stage beside the destination so activation is a same-directory move.
            // Moving from per-user Temp into Program Files can fail when Temp has
            // restrictive or customized ACLs.
            string installParent = Path.GetDirectoryName(InstallPath);
            string stage = Path.Combine(installParent, ".ArmAIImageEnhancerModular-stage_" + Guid.NewGuid().ToString("N"));
            string backup = InstallPath + ".previous_" + Guid.NewGuid().ToString("N");
            bool oldMoved = false, newMoved = false, committed = false;
            try
            {
                Directory.CreateDirectory(installParent);
                Directory.CreateDirectory(stage);
                ExtractToStage(stage);
                ValidateStage(stage);
                SetProgress("Activating verified core files…", 95);
                if (Directory.Exists(InstallPath)) { Directory.Move(InstallPath, backup); oldMoved = true; }
                Directory.Move(stage, InstallPath);
                newMoved = true;
                ValidateStage(InstallPath);
                SetProgress("Creating shortcuts…", 98);
                string warning = CreateShortcuts(desktop.Checked);
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ProductKey))
                    {
                        key.SetValue("DisplayName", "ARM AI Image Enhancer Modular");
                        key.SetValue("DisplayVersion", "V1.11");
                        key.SetValue("InstallLocation", InstallPath);
                        key.SetValue("DisplayIcon", Path.Combine(InstallPath, "_internal", "assets", "ARM.ico"));
                        key.SetValue("UninstallString", "\"" + Path.Combine(InstallPath, "UninstallModular.exe") + "\"");
                    }
                }
                catch (Exception e) { warning += (warning.Length == 0 ? "" : "\n") + "Uninstall registration could not be saved: " + e.Message; }
                committed = true;
                if (oldMoved && Directory.Exists(backup))
                    try { DeleteTree(backup); } catch (Exception e) { warning += (warning.Length == 0 ? "" : "\n") + "Previous files could not be removed: " + e.Message; }
                SetProgress("Installation complete.", 100);
                BeginInvoke((Action)delegate
                {
                    MessageBox.Show(this, "Modular core installed at:\n" + InstallPath + (String.IsNullOrEmpty(warning) ? "" : "\n\n" + warning),
                        "Installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                    try { Process.Start(Path.Combine(InstallPath, AppFile)); }
                    catch (Exception e) { MessageBox.Show("The app installed, but could not be opened automatically.\n\n" + e.Message, "Open app", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                });
            }
            catch (Exception ex)
            {
                string cleanup = "";
                if (!committed && newMoved && Directory.Exists(InstallPath))
                    try { DeleteTree(InstallPath); } catch (Exception e) { cleanup += "\nCould not remove partial install: " + e.Message; }
                if (!committed && oldMoved && Directory.Exists(backup) && !Directory.Exists(InstallPath))
                    try { Directory.Move(backup, InstallPath); } catch (Exception e) { cleanup += "\nCould not restore previous install: " + e.Message; }
                if (Directory.Exists(stage))
                    try { DeleteTree(stage); } catch (Exception e) { cleanup += "\nCould not remove temporary files: " + e.Message; }
                BeginInvoke((Action)delegate
                {
                    MessageBox.Show(this, "Installation failed. Temporary files were cleaned and the previous version was restored when possible.\n\n" + ex.Message + cleanup,
                        "Installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                });
            }
        }

        private void ExtractToStage(string stage)
        {
            using (FileStream source = File.OpenRead(Assembly.GetExecutingAssembly().Location))
            {
                long start, length; byte[] ignored;
                ReadBounds(source, out start, out length, out ignored);
                using (SegmentStream segment = new SegmentStream(source, start, length))
                using (ZipArchive archive = new ZipArchive(segment, ZipArchiveMode.Read, true))
                {
                    long total = 0, done = 0; int shown = -1;
                    foreach (ZipArchiveEntry entry in archive.Entries) total = checked(total + entry.Length);
                    byte[] buffer = new byte[1024 * 1024];
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        RejectUnsafePath(entry.FullName);
                        if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Symbolic links are not allowed in setup payloads.");
                        string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string destination = Path.GetFullPath(Path.Combine(stage, relative));
                        string prefix = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe setup destination.");
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(destination); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        using (Stream input = entry.Open())
                        using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                output.Write(buffer, 0, count); done += count;
                                int percent = total == 0 ? 94 : (int)(done * 94 / total);
                                if (percent != shown) { shown = percent; SetProgress("Extracting files… " + percent + "%", percent); }
                            }
                        }
                    }
                }
            }
        }

        private static void ValidateStage(string folder)
        {
            string app = Path.Combine(folder, AppFile);
            string ico = Path.Combine(folder, IconEntry.Replace('/', Path.DirectorySeparatorChar));
            string uninstall = Path.Combine(folder, "UninstallModular.exe");
            if (!File.Exists(app) || new FileInfo(app).Length < 1024) throw new FileNotFoundException("Application executable is missing or incomplete.", app);
            if (!File.Exists(ico) || new FileInfo(ico).Length < 100) throw new FileNotFoundException("Installed icon is missing or incomplete.", ico);
            if (!File.Exists(uninstall) || new FileInfo(uninstall).Length < 1024) throw new FileNotFoundException("Uninstaller is missing or incomplete.", uninstall);
        }

        private static string CreateShortcuts(bool makeDesktop)
        {
            try
            {
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string menu = Path.Combine(programs, "ARM AI Image Enhancer Modular");
                Directory.CreateDirectory(menu);
                string app = Path.Combine(InstallPath, AppFile);
                string icon = Path.Combine(InstallPath, "_internal", "assets", "ARM.ico");
                CreateShortcut(Path.Combine(menu, "ARM AI Image Enhancer Modular.lnk"), app, icon);
                string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ARM AI Image Enhancer Modular.lnk");
                if (makeDesktop) CreateShortcut(desktopPath, app, icon);
                else if (File.Exists(desktopPath)) File.Delete(desktopPath);
                return "";
            }
            catch (Exception ex) { return "The app installed, but shortcut creation failed: " + ex.Message; }
        }

        private static void CreateShortcut(string linkPath, string app, string icon)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Windows Script Host shortcut support is unavailable.");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            Type type = shortcut.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { app });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { InstallPath });
            type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { icon + ",0" });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }

        private static void DeleteTree(string path)
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return; }
            if (!Directory.Exists(path)) return;
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, true);
        }
    }

    private static void Uninstall()
    {
        if (MessageBox.Show("Remove ARM AI Image Enhancer Modular? User data and downloaded modules will be kept.",
                "Uninstall Modular Edition", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(ProductKey, false);
            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ARM AI Image Enhancer Modular");
            if (Directory.Exists(menu)) Directory.Delete(menu, true);
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ARM AI Image Enhancer Modular.lnk");
            if (File.Exists(desktop)) File.Delete(desktop);
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + root + "\"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false; Process.Start(psi);
        }
        catch (Exception ex) { MessageBox.Show("Uninstall failed.\n\n" + ex.Message, "ARM AI Image Enhancer Modular", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private sealed class SegmentStream : Stream
    {
        private readonly Stream source; private readonly long start; private readonly long length; private long position;
        internal SegmentStream(Stream stream, long offset, long count) { source = stream; start = offset; length = count; position = 0; source.Position = start; }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return length; } }
        public override long Position { get { return position; } set { Seek(value, SeekOrigin.Begin); } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= length) return 0;
            int read = source.Read(buffer, offset, (int)Math.Min(count, length - position)); position += read; return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : length + offset;
            if (target < 0 || target > length) throw new IOException("Seek outside setup payload.");
            position = target; source.Position = start + position; return position;
        }
        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}

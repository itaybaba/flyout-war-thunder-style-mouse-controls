using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("War Thunder Style Mouse Controls v1 - Optional Installer")]
[assembly: AssemblyProduct("War Thunder Style Mouse Controls")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Standalone .NET Framework installer. The original mod DLL is the only embedded payload.
internal static class OptionalInstaller
{
    internal const string Title = "War Thunder Style Mouse Controls v1 - Optional Installer";
    internal const string LoaderUrl = "https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3";
    internal static IEnumerable<string> Values(string text, string key)
    {
        foreach (Match m in Regex.Matches(text, "\"" + Regex.Escape(key) + "\"\\s+\"((?:\\\\.|[^\"\\\\])*)\""))
            yield return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
    }
    internal static bool IsFlyout(string folder)
    {
        return !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "Flyout.exe"));
    }
    internal static List<string> FindInLibraries(IEnumerable<string> steamRoots)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in steamRoots.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            libraries.Add(root);
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (!File.Exists(vdf)) continue;
                string text = File.ReadAllText(vdf);
                foreach (string path in Values(text, "path")) libraries.Add(path);
                // Old Steam library list format uses numbered key/value entries.
                foreach (Match m in Regex.Matches(text, "\"[0-9]+\"\\s+\"((?:\\\\.|[^\"\\\\])*)\""))
                {
                    string path = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (Path.IsPathRooted(path)) libraries.Add(path);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string library in libraries)
        {
            string steamapps = Path.Combine(library, "steamapps");
            try
            {
                if (!Directory.Exists(steamapps)) continue;
                foreach (string manifest in Directory.GetFiles(steamapps, "appmanifest_*.acf"))
                {
                    string text = File.ReadAllText(manifest);
                    if (!Values(text, "name").Any(n => n.Equals("Flyout", StringComparison.OrdinalIgnoreCase))) continue;
                    string installDir = Values(text, "installdir").FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(installDir)) continue;
                    string common = Path.GetFullPath(Path.Combine(steamapps, "common")) + Path.DirectorySeparatorChar;
                    string candidate = Path.GetFullPath(Path.Combine(common, installDir));
                    if (candidate.StartsWith(common, StringComparison.OrdinalIgnoreCase) && IsFlyout(candidate)) found.Add(candidate);
                }
                string standard = Path.Combine(steamapps, "common", "Flyout");
                if (IsFlyout(standard)) found.Add(standard);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
        return found.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
    internal static List<string> Detect()
    {
        var roots = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                try
                {
                    using (var registry = RegistryKey.OpenBaseKey(hive, view))
                    using (var key = registry.OpenSubKey(@"Software\Valve\Steam"))
                        if (key != null)
                            foreach (string value in new[] { "SteamPath", "InstallPath" })
                                roots.Add(key.GetValue(value) as string);
                }
                catch (System.Security.SecurityException) { }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        return FindInLibraries(roots);
    }
    internal static byte[] Payload()
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FlyoutMouseAim.dll"))
        using (var buffer = new MemoryStream())
        {
            if (stream == null) throw new IOException("The installer payload is missing. Download the installer again.");
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
    internal static string Install(string folder, bool checkRunning)
    {
        if (!IsFlyout(folder)) throw new IOException("Select the folder containing Flyout.exe.");
        if (!File.Exists(Path.Combine(folder, "MelonLoader", "net6", "MelonLoader.dll")))
            throw new IOException("Install MelonLoader 0.7.3 first, start Flyout once to finish loader setup, then run this installer again.");
        if (checkRunning && Process.GetProcessesByName("Flyout").Length > 0)
            throw new IOException("Close Flyout before installing. Your current flight will not be interrupted by this installer.");
        byte[] payload = Payload();
        string mods = Path.Combine(folder, "Mods");
        string target = Path.Combine(mods, "FlyoutMouseAim.dll");
        Directory.CreateDirectory(mods);
        string backup = null;
        if (File.Exists(target))
        {
            string backupDir = Path.Combine(folder, "UserData", "FlyoutMouseAim-Backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(backupDir);
            backup = Path.Combine(backupDir, "FlyoutMouseAim.dll");
            File.Copy(target, backup, false);
        }
        string temporary = Path.Combine(mods, ".FlyoutMouseAim-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, payload);
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
            using (var sha = SHA256.Create())
                if (!sha.ComputeHash(File.ReadAllBytes(target)).SequenceEqual(sha.ComputeHash(payload)))
                    throw new IOException("Installed file failed verification. Restore the backup and try again.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return "Installed v1.0.0 to:\n" + target + "\n\nStart Flyout, launch your aircraft, then press F2.\nExisting settings were preserved." +
            (backup == null ? "" : "\n\nPrevious mod backed up to:\n" + backup);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest();
        if (args.Contains("--detect-only"))
        {
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer-detected-paths.txt"), Detect());
            return 0;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new InstallerWindow());
        return 0;
    }
    private static int SelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "FlyoutOptionalInstallerTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            string steam = Path.Combine(root, "Steam"), library = Path.Combine(root, "Other Library");
            string apps = Path.Combine(library, "steamapps"), game = Path.Combine(apps, "common", "Flyout");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "Flyout.exe"), "fixture");
            string encoded = library.Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" { \"path\" \"" + encoded + "\" } }");
            File.WriteAllText(Path.Combine(apps, "appmanifest_1.acf"), "\"AppState\" { \"name\" \"Flyout\" \"installdir\" \"Flyout\" }");
            if (!FindInLibraries(new[] { steam }).Contains(game)) throw new Exception("Multi-library detection failed.");
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" \"" + encoded + "\" }");
            if (!FindInLibraries(new[] { steam }).Contains(game)) throw new Exception("Legacy-library detection failed.");
            bool rejected = false;
            try { Install(game, false); } catch (IOException) { rejected = true; }
            if (!rejected) throw new Exception("Missing-loader guard failed.");
            Directory.CreateDirectory(Path.Combine(game, "MelonLoader", "net6"));
            File.WriteAllText(Path.Combine(game, "MelonLoader", "net6", "MelonLoader.dll"), "fixture-loader");
            Directory.CreateDirectory(Path.Combine(game, "UserData"));
            string settings = Path.Combine(game, "UserData", "FlyoutMouseAim.json");
            File.WriteAllText(settings, "unchanged-settings");
            Install(game, false);
            string dll = Path.Combine(game, "Mods", "FlyoutMouseAim.dll");
            if (!File.ReadAllBytes(dll).SequenceEqual(Payload())) throw new Exception("Payload installation failed.");
            File.WriteAllText(dll, "previous-version");
            Install(game, false);
            var backups = Directory.GetFiles(Path.Combine(game, "UserData", "FlyoutMouseAim-Backups"), "FlyoutMouseAim.dll", SearchOption.AllDirectories);
            if (backups.Length != 1 || File.ReadAllText(backups[0]) != "previous-version") throw new Exception("Upgrade backup failed.");
            if (File.ReadAllText(settings) != "unchanged-settings") throw new Exception("Settings preservation failed.");
            rejected = false;
            try { Install(root, false); } catch (IOException) { rejected = true; }
            if (!rejected) throw new Exception("Wrong-folder guard failed.");
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer-self-test.txt"), "PASS: new/legacy Steam libraries, missing loader, fresh install, upgrade, backup, payload integrity, settings preservation, wrong-folder guard.\n");
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer-self-test.txt"), "FAIL: " + e);
            return 1;
        }
        finally
        {
            // Only the unique fixture directory created by this invocation is removed.
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}

internal sealed class InstallerWindow : Form
{
    private readonly ComboBox path = new ComboBox();
    public InstallerWindow()
    {
        Text = OptionalInstaller.Title;
        ClientSize = new Size(620, 270);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        Controls.Add(new Label { Text = "Optional installer for War Thunder Style Mouse Controls v1", AutoSize = false, Bounds = new Rectangle(20, 20, 580, 30), Font = new Font(Font, FontStyle.Bold) });
        Controls.Add(new Label { Text = "Installs the mod DLL into Flyout's Mods folder. Requires MelonLoader 0.7.3.\nClose Flyout first. Existing settings are kept; an older DLL is backed up.", Bounds = new Rectangle(20, 60, 580, 55) });
        path.Bounds = new Rectangle(20, 135, 475, 30);
        path.DropDownStyle = ComboBoxStyle.DropDown;
        foreach (string folder in OptionalInstaller.Detect()) path.Items.Add(folder);
        if (path.Items.Count > 0) path.SelectedIndex = 0;
        Controls.Add(path);
        var browse = new Button { Text = "Browse...", Bounds = new Rectangle(505, 132, 95, 32) };
        browse.Click += delegate { using (var dialog = new FolderBrowserDialog { Description = "Select the folder containing Flyout.exe", ShowNewFolderButton = false }) if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.SelectedPath; };
        Controls.Add(browse);
        var loader = new LinkLabel { Text = "Get MelonLoader", Bounds = new Rectangle(20, 185, 190, 28) };
        loader.LinkClicked += delegate { Process.Start(new ProcessStartInfo(OptionalInstaller.LoaderUrl) { UseShellExecute = true }); };
        Controls.Add(loader);
        var install = new Button { Text = "Install mod", Bounds = new Rectangle(360, 215, 115, 35) };
        install.Click += delegate
        {
            try { MessageBox.Show(this, OptionalInstaller.Install(path.Text.Trim().Trim('"'), true), "Installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch (Exception e) { MessageBox.Show(this, e.Message + "\n\nIf this folder needs administrator access, rerun the installer as administrator or use the manual ZIP.", "Installation could not finish", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        Controls.Add(install);
        var close = new Button { Text = "Close", Bounds = new Rectangle(485, 215, 115, 35) };
        close.Click += delegate { Close(); };
        Controls.Add(close);
        AcceptButton = install;
        CancelButton = close;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UpsGuardian;

internal static class UpdateTests
{
    private static int passed;

    private static int Main()
    {
        Run("semver-beta-ordering", SemVerBetaOrdering);
        Run("manifest-asset-selection", ManifestAssetSelection);
        Run("stable-channel-excludes-prerelease", StableChannelExcludesPrerelease);
        Run("checksum-rejects-wrong-hash", ChecksumRejectsWrongHash);
        Run("archive-accepts-only-runtime-files", ArchiveAcceptsOnlyRuntimeFiles);
        Run("archive-rejects-zip-slip-data-and-case-collision", ArchiveRejectsUnsafeNames);
        Run("transaction-rolls-back-on-replacement-failure", TransactionRollsBack);
        Run("verified-rollback-removes-backup", VerifiedRollbackRemovesBackup);
        Run("parent-timeout-aborts-without-killing-parent", ParentTimeoutAborts);
        Run("arguments-are-quoted-for-process-start", ArgumentsAreQuoted);
        Console.WriteLine("Passed " + passed + " update checks; no network, install, GUI, or power action was performed.");
        return 0;
    }

    private static void SemVerBetaOrdering()
    {
        Assert(UpdateService.CompareVersionsForTests("1.0.0-beta.2", "1.0.0-beta.11") < 0, "numeric beta identifiers use numeric ordering");
        Assert(UpdateService.CompareVersionsForTests("1.0.0-beta.2", "1.0.0-beta.1") > 0, "beta.2 follows beta.1");
        Assert(UpdateService.CompareVersionsForTests("1.0.0", "1.0.0-rc.1") > 0, "stable follows prerelease");
        Assert(UpdateService.CompareVersionsForTests("1.0.0+build.1", "1.0.0+build.2") == 0, "build metadata does not change precedence");
    }

    private static void ManifestAssetSelection()
    {
        string json = Manifest(
            Release("v0.2.0-beta.1", false, true, "UPSGuardian-0.2.0-beta.1-windows-x64.zip", "SHA256SUMS.txt"),
            Release("v0.1.9", false, false, "UPSGuardian-0.1.9-windows-x64.zip", "SHA256SUMS.txt"));
        UpdateCheckResult result = UpdateService.ParseManifestForTests(json, "0.1.0-beta.1");
        Assert(result.Status == UpdateStatus.Available, "new compatible release is available");
        Assert(result.Release != null && result.Release.Version == "0.2.0-beta.1", "highest compatible release selected");
        Assert(result.Release.PackageUrl.EndsWith("UPSGuardian-0.2.0-beta.1-windows-x64.zip", StringComparison.Ordinal), "exact package asset selected");
        Assert(result.Release.ChecksumsUrl.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal), "exact checksum asset selected");

        UpdateCheckResult current = UpdateService.ParseManifestForTests(json, "0.3.0-beta.1");
        Assert(current.Status == UpdateStatus.Current, "current prerelease is reported current");
    }

    private static void StableChannelExcludesPrerelease()
    {
        string json = Manifest(Release("v0.2.0-beta.1", false, true, "UPSGuardian-0.2.0-beta.1-windows-x64.zip", "SHA256SUMS.txt"));
        UpdateCheckResult result = UpdateService.ParseManifestForTests(json, "0.1.0");
        Assert(result.Status == UpdateStatus.NoReleases && result.Release == null, "stable channel does not treat a prerelease as current");
        UpdateCheckResult empty = UpdateService.ParseManifestForTests("[]", "0.1.0");
        Assert(empty.Status == UpdateStatus.NoReleases, "empty release feed is not reported up-to-date");
    }

    private static void ChecksumRejectsWrongHash()
    {
        string directory = NewDirectory();
        try
        {
            string package = CreatePackage(directory, "0.2.0", "new-main", "new-updater");
            string hash = Hash(package);
            Assert(UpdateService.VerifyPackageForTests(package, hash) == hash, "correct package hash verifies");
            string target = Path.Combine(directory, "install");
            Directory.CreateDirectory(target);
            bool rejected = false;
            try { WindowsUpdateInstaller.ValidateArchiveForTests(package, "0.2.0", new String('0', 64), target); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "wrong package hash is rejected before archive use");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void ArchiveAcceptsOnlyRuntimeFiles()
    {
        string directory = NewDirectory();
        try
        {
            string package = CreatePackage(directory, "0.2.0", "new-main", "new-updater", true);
            string target = Path.Combine(directory, "install");
            Directory.CreateDirectory(target);
            List<string> names = WindowsUpdateInstaller.ValidateArchiveForTests(package, "0.2.0", Hash(package), target);
            Assert(names.Contains("UPSGuardian.exe") && names.Contains("UPSGuardian.Updater.exe"), "required runtime files are accepted");
            Assert(names.Contains("README.md"), "optional documentation is accepted");
            Assert(names.Count == 3, "only packaged runtime and documentation files are staged");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void ArchiveRejectsUnsafeNames()
    {
        AssertArchiveRejected("../UPSGuardian.exe", "zip-slip entry rejected");
        AssertArchiveRejected("data/settings.xml", "data entry rejected and portable state is preserved");
        string directory = NewDirectory();
        try
        {
            string package = CreatePackage(directory, "0.2.0", "main", "updater", false, "UPSGuardian.exe", "upsguardian.exe");
            string target = Path.Combine(directory, "install");
            Directory.CreateDirectory(target);
            AssertThrows(delegate { WindowsUpdateInstaller.ValidateArchiveForTests(package, "0.2.0", Hash(package), target); }, "case-colliding archive entries rejected");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void TransactionRollsBack()
    {
        string directory = NewDirectory();
        try
        {
            string package = CreatePackage(directory, "0.2.0", "new-main", "new-updater");
            string target = Path.Combine(directory, "install");
            Directory.CreateDirectory(target);
            string main = Path.Combine(target, "UPSGuardian.exe");
            string updater = Path.Combine(target, "UPSGuardian.Updater.exe");
            string dataDirectory = Path.Combine(target, "data");
            string settings = Path.Combine(dataDirectory, "settings.xml");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(main, "old-main", Encoding.UTF8);
            File.WriteAllText(updater, "old-updater", Encoding.UTF8);
            File.WriteAllText(settings, "keep-settings", Encoding.UTF8);
            Exception failure = null;
            try
            {
                WindowsUpdateInstaller.ApplyPackageForTests(new StagedUpdate(package, Hash(package), "0.2.0"), target, false, true, true);
            }
            catch (Exception ex) { failure = ex; }
            UpdateRollbackException rollbackFailure = failure as UpdateRollbackException;
            Assert(rollbackFailure != null, "rollback failure is surfaced with a recovery exception");
            Assert(!String.IsNullOrEmpty(rollbackFailure.BackupDirectory) && Directory.Exists(rollbackFailure.BackupDirectory), "uncertain rollback preserves the backup directory");
            Assert(File.ReadAllText(Path.Combine(rollbackFailure.BackupDirectory, "UPSGuardian.exe"), Encoding.UTF8) == "old-main", "preserved backup contains the original executable");
            Assert(File.ReadAllText(settings, Encoding.UTF8) == "keep-settings", "portable data remains untouched");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static void VerifiedRollbackRemovesBackup()
    {
        string directory = NewDirectory();
        try
        {
            string package = CreatePackage(directory, "0.2.0", "new-main", "new-updater");
            string target = Path.Combine(directory, "install");
            string dataDirectory = Path.Combine(target, "data");
            Directory.CreateDirectory(dataDirectory);
            string main = Path.Combine(target, "UPSGuardian.exe");
            string updater = Path.Combine(target, "UPSGuardian.Updater.exe");
            string settings = Path.Combine(dataDirectory, "settings.xml");
            File.WriteAllText(main, "old-main", Encoding.UTF8);
            File.WriteAllText(updater, "old-updater", Encoding.UTF8);
            File.WriteAllText(settings, "keep-settings", Encoding.UTF8);
            Exception failure = null;
            try
            {
                WindowsUpdateInstaller.ApplyPackageForTests(new StagedUpdate(package, Hash(package), "0.2.0"), target, false, true, false);
            }
            catch (Exception ex) { failure = ex; }
            Assert(failure != null, "injected replacement failure is surfaced");
            Assert(File.ReadAllText(main, Encoding.UTF8) == "old-main", "verified rollback restores the original executable");
            Assert(File.ReadAllText(updater, Encoding.UTF8) == "old-updater", "verified rollback restores the original updater");
            Assert(File.ReadAllText(settings, Encoding.UTF8) == "keep-settings", "existing portable settings remain unchanged");
            Assert(Directory.GetDirectories(target, ".upsguardian-backup-*", SearchOption.TopDirectoryOnly).Length == 0, "verified rollback removes the backup");
        }
        finally { DeleteDirectory(directory); }
    }

    private static void ParentTimeoutAborts()
    {
        using (System.Diagnostics.Process current = System.Diagnostics.Process.GetCurrentProcess())
        {
            bool timedOut = false;
            try { WindowsUpdateInstaller.WaitForParentForTests(current.Id, 50); }
            catch (TimeoutException) { timedOut = true; }
            Assert(timedOut, "a live parent causes a timeout instead of installation");
            Assert(!current.HasExited, "timeout never kills the parent process");
        }
    }

    private static void ArgumentsAreQuoted()
    {
        string args = WindowsUpdateInstaller.BuildArgumentsForTests("C:\\work area\\UPSGuardian-0.2.0-windows-x64.zip", new String('a', 64), "C:\\Program Files\\UPS Guardian", 42, "0.2.0");
        Assert(args.IndexOf("\"C:\\work area\\UPSGuardian-0.2.0-windows-x64.zip\"", StringComparison.Ordinal) >= 0, "package path is quoted");
        Assert(args.IndexOf("\"C:\\Program Files\\UPS Guardian\"", StringComparison.Ordinal) >= 0, "target path is quoted");
        Assert(args.IndexOf("--wait-pid 42", StringComparison.Ordinal) >= 0, "parent pid is fixed numeric argument");
    }

    private static string Manifest(params string[] releases)
    {
        return "[" + String.Join(",", releases) + "]";
    }

    private static string Release(string tag, bool draft, bool prerelease, string package, string checksums)
    {
        string baseUrl = "https://github.com/kylefu8/ups-guardian/releases/download/" + tag + "/";
        return "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/kylefu8/ups-guardian/releases/tag/" + tag + "\",\"body\":\"notes\",\"draft\":" +
            draft.ToString().ToLowerInvariant() + ",\"prerelease\":" + prerelease.ToString().ToLowerInvariant() + ",\"assets\":[" +
            "{\"name\":\"" + package + "\",\"browser_download_url\":\"" + baseUrl + package + "\"}," +
            "{\"name\":\"" + checksums + "\",\"browser_download_url\":\"" + baseUrl + checksums + "\"}]}";
    }

    private static string CreatePackage(string directory, string version, string main, string updater, bool readme = false, params string[] extraNames)
    {
        string package = Path.Combine(directory, "UPSGuardian-" + version + "-windows-x64.zip");
        using (ZipArchive archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "UPSGuardian.exe", main);
            WriteEntry(archive, "UPSGuardian.Updater.exe", updater);
            if (readme) WriteEntry(archive, "README.md", "readme");
            for (int i = 0; i < extraNames.Length; i++)
                WriteEntry(archive, extraNames[i], "unsafe");
        }
        return package;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using (StreamWriter writer = new StreamWriter(entry.Open(), Encoding.UTF8))
            writer.Write(content);
    }

    private static void AssertArchiveRejected(string unsafeName, string message)
    {
        string directory = NewDirectory();
        try
        {
            string package = CreatePackage(directory, "0.2.0", "main", "updater", false, unsafeName);
            string target = Path.Combine(directory, "install");
            Directory.CreateDirectory(target);
            AssertThrows(delegate { WindowsUpdateInstaller.ValidateArchiveForTests(package, "0.2.0", Hash(package), target); }, message);
        }
        finally { DeleteDirectory(directory); }
    }

    private static string Hash(string path)
    {
        using (FileStream stream = File.OpenRead(path))
        using (SHA256 sha = SHA256.Create())
        {
            byte[] bytes = sha.ComputeHash(stream);
            StringBuilder result = new StringBuilder(64);
            for (int i = 0; i < bytes.Length; i++) result.Append(bytes[i].ToString("x2"));
            return result.ToString();
        }
    }

    private static string NewDirectory()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cases");
        Directory.CreateDirectory(root);
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            string allowed = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cases")) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(path);
            if (!resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("unsafe cleanup path");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
        catch { }
    }

    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); passed++; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex); Environment.Exit(1); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows(Action action, string message)
    {
        try { action(); }
        catch { return; }
        throw new InvalidOperationException(message);
    }
}

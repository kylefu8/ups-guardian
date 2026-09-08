using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace UpsGuardian
{
    /// <summary>
    /// Indicates that an update failed and the original files could not be
    /// verified after rollback. The backup directory is intentionally kept so
    /// recovery is still possible.
    /// </summary>
    public sealed class UpdateRollbackException : IOException
    {
        internal UpdateRollbackException(string message, Exception inner, string backupDirectory)
            : base(message, inner)
        {
            BackupDirectory = backupDirectory;
        }

        public string BackupDirectory { get; private set; }
    }

    /// <summary>
    /// Starts the trusted local updater and contains the updater's transactional
    /// file replacement logic. No shell is used and only the fixed runtime
    /// files from a verified package can be replaced.
    /// </summary>
    public static class WindowsUpdateInstaller
    {
        internal const long MaximumArchiveEntryBytes = 128L * 1024L * 1024L;
        internal const long MaximumArchiveBytes = 256L * 1024L * 1024L;
        internal const int ParentWaitMilliseconds = 30000;
        internal const string MainExecutable = "UPSGuardian.exe";
        internal const string UpdaterExecutable = "UPSGuardian.Updater.exe";

        private static readonly string[] AllowedRuntimeFiles = new string[]
        {
            MainExecutable,
            UpdaterExecutable,
            "LICENSE",
            "README.md",
            "CHANGELOG.md"
        };

        public static void Start(StagedUpdate update, string installDirectory, int parentPid)
        {
            if (update == null)
                throw new ArgumentNullException("update");
            if (parentPid <= 0)
                throw new ArgumentOutOfRangeException("parentPid", "parentPid must be positive.");

            string target = ValidateInstallDirectory(installDirectory);
            string package = ValidateStagedUpdate(update);
            string trustedUpdater = Path.Combine(target, UpdaterExecutable);
            RequireRegularFile(trustedUpdater, "installed updater");

            string packageDirectory = Path.GetDirectoryName(package);
            string helperDirectory = Path.Combine(packageDirectory, ".upsguardian-updater-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(helperDirectory);
            try
            {
                RejectReparsePoint(helperDirectory, "updater staging directory");
                string helperPath = Path.Combine(helperDirectory, UpdaterExecutable);
                File.Copy(trustedUpdater, helperPath, false);
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = helperPath;
                startInfo.Arguments = BuildArguments(package, update.Sha256, target, parentPid, update.Version);
                startInfo.WorkingDirectory = helperDirectory;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                        throw new InvalidOperationException("Could not start the UPS Guardian updater.");
                }
            }
            catch
            {
                TryDeleteDirectory(helperDirectory);
                throw;
            }
        }

        internal static void ApplyPackage(StagedUpdate update, string installDirectory, bool restartApplication)
        {
            ApplyPackageCore(update, installDirectory, restartApplication, false, false);
        }

        // Test-only seam. It injects failures after replacement and during
        // rollback so the recovery-preservation path can be verified without
        // racing a real file lock or launching the GUI.
        internal static void ApplyPackageForTests(StagedUpdate update, string installDirectory, bool restartApplication,
            bool failAfterReplacement, bool failRollback)
        {
            ApplyPackageCore(update, installDirectory, restartApplication, failAfterReplacement, failRollback);
        }

        private static void ApplyPackageCore(StagedUpdate update, string installDirectory, bool restartApplication,
            bool failAfterReplacement, bool failRollback)
        {
            if (update == null)
                throw new ArgumentNullException("update");
            string target = ValidateInstallDirectory(installDirectory);
            string package = ValidateStagedUpdate(update);
            string expectedPackageName = "UPSGuardian-" + update.Version + "-windows-x64.zip";
            if (!String.Equals(Path.GetFileName(package), expectedPackageName, StringComparison.Ordinal))
                throw new InvalidDataException("The staged package name does not match its version.");

            string actualHash = ComputeSha256(package);
            if (!String.Equals(actualHash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged package failed SHA-256 verification.");

            string staging = Path.Combine(target, ".upsguardian-install-" + Guid.NewGuid().ToString("N"));
            string backup = Path.Combine(target, ".upsguardian-backup-" + Guid.NewGuid().ToString("N"));
            bool preserveBackup = false;
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(backup);
            try
            {
                RejectReparsePoint(staging, "package staging directory");
                RejectReparsePoint(backup, "package backup directory");
                List<PackageFile> files = ValidateAndExtract(package, staging);
                List<BackupFile> backups = BackupFiles(files, target, backup);
                try
                {
                    ReplaceFiles(files, target);
                    if (failAfterReplacement)
                        throw new IOException("Injected test replacement failure.");
                    if (restartApplication)
                        StartUpdatedApplication(target);
                }
                catch (Exception installFailure)
                {
                    try
                    {
                        Rollback(files, backups, target, failRollback);
                        VerifyRollback(files, backups, target);
                    }
                    catch (Exception rollbackFailure)
                    {
                        preserveBackup = true;
                        throw new UpdateRollbackException(
                            "UPS Guardian update failed and rollback could not be verified. Original error: " +
                            installFailure.Message + ". Rollback error: " + rollbackFailure.Message +
                            ". Recovery backup preserved at " + backup + ".",
                            rollbackFailure,
                            backup);
                    }

                    // The old files are known to be intact. If the helper was
                    // asked to restart the app, make the recovered app visible
                    // with a diagnostic switch before propagating the original
                    // update failure to the helper's log.
                    if (restartApplication)
                    {
                        try { StartRecoveredApplication(target); }
                        catch (Exception recoveryStartFailure)
                        {
                            throw new IOException("The update was rolled back, but the recovered UPS Guardian application could not be started.", recoveryStartFailure);
                        }
                    }
                    throw;
                }
            }
            finally
            {
                TryDeleteDirectory(staging);
                if (!preserveBackup)
                    TryDeleteDirectory(backup);
            }
        }

        internal static List<string> ValidateArchiveForTests(string packagePath, string version, string expectedHash, string installDirectory)
        {
            if (String.IsNullOrEmpty(packagePath))
                throw new ArgumentException("packagePath is required.", "packagePath");
            UpdateService.SemVersion parsed = UpdateService.SemVersion.Parse(version);
            string package = Path.GetFullPath(packagePath);
            RequireRegularFile(package, "package");
            UpdateService.ValidateHashForTests(expectedHash);
            string actual = ComputeSha256(package);
            if (!String.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The package failed SHA-256 verification.");
            string target = ValidateInstallDirectory(installDirectory);
            string staging = Path.Combine(target, ".upsguardian-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                List<PackageFile> files = ValidateAndExtract(package, staging);
                List<string> result = new List<string>();
                for (int i = 0; i < files.Count; i++)
                    result.Add(files[i].Name);
                return result;
            }
            finally { TryDeleteDirectory(staging); }
        }

        internal static string BuildArgumentsForTests(string packagePath, string hash, string target, int parentPid, string version)
        {
            return BuildArguments(packagePath, hash, target, parentPid, version);
        }

        internal static void WaitForParentForTests(int parentPid)
        {
            WaitForParent(parentPid, ParentWaitMilliseconds);
        }

        internal static void WaitForParentForTests(int parentPid, int timeoutMilliseconds)
        {
            WaitForParent(parentPid, timeoutMilliseconds);
        }

        internal static void LogFailureForTests(string targetDirectory, Exception exception)
        {
            TryLogFailure(targetDirectory, exception);
        }

        internal static void WaitForParent(int parentPid)
        {
            WaitForParent(parentPid, ParentWaitMilliseconds);
        }

        private static void WaitForParent(int parentPid, int timeoutMilliseconds)
        {
            if (parentPid <= 0)
                return;
            if (timeoutMilliseconds < 0)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                try
                {
                    using (Process process = Process.GetProcessById(parentPid))
                    {
                        if (process.HasExited)
                            return;
                    }
                }
                catch (ArgumentException) { return; }
                catch (InvalidOperationException) { return; }
                long remaining = timeoutMilliseconds - timer.ElapsedMilliseconds;
                Thread.Sleep((int)Math.Max(1L, Math.Min(100L, remaining)));
            }
            throw new TimeoutException("The previous UPS Guardian process did not exit before the updater timeout.");
        }

        internal static void TryLogFailure(string targetDirectory, Exception exception)
        {
            try
            {
                string target = ValidateInstallDirectory(targetDirectory);
                string path = Path.Combine(target, "update-error.log");
                string message = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " UPS Guardian update failed: " +
                    (exception == null ? "unknown error" : exception.ToString());
                if (message.Length > 8000)
                    message = message.Substring(0, 8000);
                File.WriteAllText(path, message + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        private static string ValidateStagedUpdate(StagedUpdate update)
        {
            UpdateService.SemVersion.Parse(update.Version);
            UpdateService.ValidateHashForTests(update.Sha256);
            if (String.IsNullOrEmpty(update.PackagePath))
                throw new ArgumentException("The staged package path is required.", "update");
            string package = Path.GetFullPath(update.PackagePath);
            RequireRegularFile(package, "staged package");
            return package;
        }

        private static string ValidateInstallDirectory(string installDirectory)
        {
            if (String.IsNullOrWhiteSpace(installDirectory))
                throw new ArgumentException("installDirectory is required.", "installDirectory");
            string target = Path.GetFullPath(installDirectory);
            if (!Directory.Exists(target))
                throw new DirectoryNotFoundException("The UPS Guardian install directory was not found.");
            RejectReparsePoint(target, "install directory");
            return target;
        }

        private static string BuildArguments(string packagePath, string hash, string target, int parentPid, string version)
        {
            return "--package " + QuoteArgument(packagePath) +
                " --sha256 " + QuoteArgument(hash) +
                " --target " + QuoteArgument(target) +
                " --wait-pid " + parentPid.ToString(CultureInfo.InvariantCulture) +
                " --version " + QuoteArgument(version);
        }

        private static string QuoteArgument(string value)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            StringBuilder result = new StringBuilder(value.Length + 2);
            result.Append('"');
            int backslashes = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (ch == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }
                if (backslashes != 0)
                {
                    result.Append('\\', backslashes);
                    backslashes = 0;
                }
                result.Append(ch);
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private static List<PackageFile> ValidateAndExtract(string packagePath, string staging)
        {
            List<PackageFile> files = new List<PackageFile>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            bool hasMain = false;
            bool hasUpdater = false;
            using (ZipArchive archive = ZipFile.OpenRead(packagePath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = ValidateEntryName(entry, names);
                    if (!IsAllowedFile(name))
                        throw new InvalidDataException("The update package contains an unknown file: " + name);
                    if (entry.Length < 0 || entry.Length > MaximumArchiveEntryBytes)
                        throw new InvalidDataException("The update package contains an oversized file: " + name);
                    total += entry.Length;
                    if (total > MaximumArchiveBytes)
                        throw new InvalidDataException("The update package is larger than the safety limit.");
                    if (String.Equals(name, MainExecutable, StringComparison.Ordinal))
                        hasMain = true;
                    if (String.Equals(name, UpdaterExecutable, StringComparison.Ordinal))
                        hasUpdater = true;
                    string stagedPath = Path.Combine(staging, name);
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        CopyEntryBounded(input, output, entry.Length, name);
                    }
                    files.Add(new PackageFile(name, stagedPath));
                }
            }
            if (!hasMain || !hasUpdater)
                throw new InvalidDataException("The update package must contain both UPSGuardian.exe and UPSGuardian.Updater.exe.");
            return files;
        }

        private static string ValidateEntryName(ZipArchiveEntry entry, HashSet<string> names)
        {
            if (entry == null || String.IsNullOrEmpty(entry.FullName))
                throw new InvalidDataException("The update package contains an empty entry name.");
            string name = entry.FullName;
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || Path.IsPathRooted(name) || name.IndexOf(':') >= 0 ||
                name.IndexOf("..", StringComparison.Ordinal) >= 0 || name.IndexOf('\0') >= 0 ||
                name == "." || name == "..")
                throw new InvalidDataException("The update package contains a rooted or traversal entry: " + name);
            if (!names.Add(name))
                throw new InvalidDataException("The update package contains duplicate or case-colliding entries: " + name);
            int attributes = entry.ExternalAttributes;
            if ((attributes & (int)FileAttributes.ReparsePoint) != 0 || ((attributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The update package contains a link or reparse entry: " + name);
            return name;
        }

        private static bool IsAllowedFile(string name)
        {
            for (int i = 0; i < AllowedRuntimeFiles.Length; i++)
                if (String.Equals(AllowedRuntimeFiles[i], name, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static void CopyEntryBounded(Stream input, Stream output, long expectedLength, string name)
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaximumArchiveEntryBytes)
                    throw new InvalidDataException("The update package contains an oversized file: " + name);
                output.Write(buffer, 0, read);
            }
            if (total != expectedLength)
                throw new InvalidDataException("The update package entry length did not match its metadata: " + name);
        }

        private static List<BackupFile> BackupFiles(List<PackageFile> files, string target, string backup)
        {
            List<BackupFile> backups = new List<BackupFile>();
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    string destination = Path.Combine(target, files[i].Name);
                    if (Directory.Exists(destination))
                        throw new IOException("The update target is a directory: " + files[i].Name);
                    bool existed = File.Exists(destination);
                    if (existed)
                    {
                        RejectReparsePoint(destination, "existing runtime file");
                        string backupPath = Path.Combine(backup, files[i].Name);
                        File.Copy(destination, backupPath, false);
                        backups.Add(new BackupFile(files[i].Name, backupPath, true));
                    }
                    else
                    {
                        backups.Add(new BackupFile(files[i].Name, null, false));
                    }
                }
                return backups;
            }
            catch
            {
                throw;
            }
        }

        private static void ReplaceFiles(List<PackageFile> files, string target)
        {
            for (int i = 0; i < files.Count; i++)
            {
                string destination = Path.Combine(target, files[i].Name);
                if (File.Exists(destination))
                    RejectReparsePoint(destination, "runtime file");
                File.Copy(files[i].StagedPath, destination, true);
            }
        }

        private static void Rollback(List<PackageFile> files, List<BackupFile> backups, string target, bool failRollback)
        {
            Exception firstFailure = null;
            for (int i = backups.Count - 1; i >= 0; i--)
            {
                BackupFile backup = backups[i];
                try
                {
                    if (failRollback)
                        throw new IOException("Injected test rollback failure.");
                    string destination = Path.Combine(target, backup.Name);
                    if (backup.Existed)
                    {
                        RequireRegularFile(backup.BackupPath, "rollback backup");
                        if (File.Exists(destination))
                        {
                            RejectReparsePoint(destination, "rollback target");
                            File.SetAttributes(destination, FileAttributes.Normal);
                        }
                        File.Copy(backup.BackupPath, destination, true);
                    }
                    else if (File.Exists(destination))
                    {
                        RejectReparsePoint(destination, "rollback target");
                        File.SetAttributes(destination, FileAttributes.Normal);
                        File.Delete(destination);
                    }
                }
                catch (Exception ex)
                {
                    if (firstFailure == null)
                        firstFailure = ex;
                }
            }
            if (firstFailure != null)
                throw new IOException("One or more runtime files could not be restored during rollback.", firstFailure);
        }

        private static void VerifyRollback(List<PackageFile> files, List<BackupFile> backups, string target)
        {
            for (int i = 0; i < backups.Count; i++)
            {
                BackupFile backup = backups[i];
                string destination = Path.Combine(target, backup.Name);
                if (!backup.Existed)
                {
                    if (File.Exists(destination) || Directory.Exists(destination))
                        throw new IOException("Rollback left a newly created runtime path: " + backup.Name);
                    continue;
                }
                RequireRegularFile(destination, "restored runtime file");
                RequireRegularFile(backup.BackupPath, "rollback backup");
                if (!FilesEqual(destination, backup.BackupPath))
                    throw new IOException("Rollback verification failed for " + backup.Name + ".");
            }
        }

        private static bool FilesEqual(string left, string right)
        {
            FileInfo leftInfo = new FileInfo(left);
            FileInfo rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length)
                return false;
            using (FileStream leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] leftBuffer = new byte[81920];
                byte[] rightBuffer = new byte[81920];
                int leftRead;
                while ((leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length)) > 0)
                {
                    int offset = 0;
                    while (offset < leftRead)
                    {
                        int rightRead = rightStream.Read(rightBuffer, offset, leftRead - offset);
                        if (rightRead == 0)
                            return false;
                        for (int i = 0; i < rightRead; i++)
                            if (leftBuffer[offset + i] != rightBuffer[offset + i])
                                return false;
                        offset += rightRead;
                    }
                }
                return rightStream.ReadByte() < 0;
            }
        }

        private static void StartUpdatedApplication(string target)
        {
            string executable = Path.Combine(target, MainExecutable);
            RequireRegularFile(executable, "updated application");
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = executable;
            startInfo.Arguments = "--updated";
            startInfo.WorkingDirectory = target;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = false;
            startInfo.WindowStyle = ProcessWindowStyle.Normal;
            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                    throw new InvalidOperationException("The updated UPS Guardian application could not be restarted.");
            }
        }

        private static void StartRecoveredApplication(string target)
        {
            string executable = Path.Combine(target, MainExecutable);
            RequireRegularFile(executable, "recovered application");
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = executable;
            startInfo.Arguments = "--update-failed";
            startInfo.WorkingDirectory = target;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = false;
            startInfo.WindowStyle = ProcessWindowStyle.Normal;
            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                    throw new InvalidOperationException("The recovered UPS Guardian application could not be started.");
            }
        }

        private static string ComputeSha256(string path)
        {
            FileInfo info = new FileInfo(path);
            if (info.Length > UpdateService.MaximumPackageBytes)
                throw new InvalidDataException("The staged update exceeded its size limit.");
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                StringBuilder result = new StringBuilder(64);
                byte[] hash = sha.Hash;
                for (int i = 0; i < hash.Length; i++)
                    result.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static void RequireRegularFile(string path, string description)
        {
            if (!File.Exists(path) || Directory.Exists(path))
                throw new FileNotFoundException("The update " + description + " was not found.", path);
            RejectReparsePoint(path, description);
        }

        private static void RejectReparsePoint(string path, string description)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The update " + description + " is a reparse point.");
        }

        private static void TryDeleteDirectory(string path)
        {
            if (String.IsNullOrEmpty(path))
                return;
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { }
        }

        private sealed class PackageFile
        {
            internal PackageFile(string name, string stagedPath)
            {
                Name = name;
                StagedPath = stagedPath;
            }
            internal string Name;
            internal string StagedPath;
        }

        private sealed class BackupFile
        {
            internal BackupFile(string name, string backupPath, bool existed)
            {
                Name = name;
                BackupPath = backupPath;
                Existed = existed;
            }
            internal string Name;
            internal string BackupPath;
            internal bool Existed;
        }
    }
}

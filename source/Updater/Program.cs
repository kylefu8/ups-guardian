using System;
using System.Globalization;
using System.IO;

namespace UpsGuardian.Updater
{
    /// <summary>Small, argument-validated helper launched by the installed app.</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string target = null;
            try
            {
                ParsedArguments parsed = Parse(args);
                target = parsed.Target;
                WindowsUpdateInstaller.WaitForParent(parsed.ParentPid);
                StagedUpdate update = new StagedUpdate(parsed.Package, parsed.Sha256, parsed.Version);
                WindowsUpdateInstaller.ApplyPackage(update, parsed.Target, true);
                return 0;
            }
            catch (Exception ex)
            {
                WindowsUpdateInstaller.TryLogFailure(target, ex);
                return 1;
            }
        }

        private static ParsedArguments Parse(string[] args)
        {
            if (args == null)
                throw new ArgumentException("Updater arguments are required.");
            string package = null;
            string hash = null;
            string target = null;
            string version = null;
            int parentPid = 0;
            bool seenPackage = false;
            bool seenHash = false;
            bool seenTarget = false;
            bool seenVersion = false;
            bool seenParent = false;
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                if (String.Equals(option, "--package", StringComparison.Ordinal))
                {
                    if (seenPackage || ++i >= args.Length) throw new ArgumentException("Invalid --package argument.");
                    package = args[i]; seenPackage = true;
                }
                else if (String.Equals(option, "--sha256", StringComparison.Ordinal))
                {
                    if (seenHash || ++i >= args.Length) throw new ArgumentException("Invalid --sha256 argument.");
                    hash = args[i]; seenHash = true;
                }
                else if (String.Equals(option, "--target", StringComparison.Ordinal))
                {
                    if (seenTarget || ++i >= args.Length) throw new ArgumentException("Invalid --target argument.");
                    target = args[i]; seenTarget = true;
                }
                else if (String.Equals(option, "--wait-pid", StringComparison.Ordinal))
                {
                    if (seenParent || ++i >= args.Length || !Int32.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid) || parentPid <= 0)
                        throw new ArgumentException("Invalid --wait-pid argument.");
                    seenParent = true;
                }
                else if (String.Equals(option, "--version", StringComparison.Ordinal))
                {
                    if (seenVersion || ++i >= args.Length) throw new ArgumentException("Invalid --version argument.");
                    version = UpdateService.SemVersion.Parse(args[i]).ToString(); seenVersion = true;
                }
                else
                {
                    throw new ArgumentException("Unknown updater argument.");
                }
            }
            if (!seenPackage || !seenHash || !seenTarget || !seenParent || !seenVersion)
                throw new ArgumentException("Updater requires package, sha256, target, wait-pid, and version arguments.");
            return new ParsedArguments(Path.GetFullPath(package), hash, Path.GetFullPath(target), parentPid, version);
        }

        private sealed class ParsedArguments
        {
            internal ParsedArguments(string package, string sha256, string target, int parentPid, string version)
            {
                Package = package;
                Sha256 = sha256;
                Target = target;
                ParentPid = parentPid;
                Version = version;
            }
            internal string Package;
            internal string Sha256;
            internal string Target;
            internal int ParentPid;
            internal string Version;
        }
    }
}

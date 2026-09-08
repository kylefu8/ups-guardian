using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace UpsGuardian
{
    public enum UpdateStatus
    {
        Available,
        Current,
        NoReleases
    }

    public sealed class UpdateCheckResult
    {
        internal UpdateCheckResult(UpdateStatus status, UpdateRelease release)
        {
            Status = status;
            Release = release;
        }

        public UpdateStatus Status { get; private set; }
        public UpdateRelease Release { get; private set; }
    }

    public sealed class UpdateRelease
    {
        public UpdateRelease(string version, string notes, string releaseUrl, string packageUrl, string checksumsUrl)
        {
            Version = version;
            Notes = notes ?? String.Empty;
            ReleaseUrl = releaseUrl;
            PackageUrl = packageUrl;
            ChecksumsUrl = checksumsUrl;
        }

        public string Version { get; private set; }
        public string Notes { get; private set; }
        public string ReleaseUrl { get; private set; }
        public string PackageUrl { get; private set; }
        public string ChecksumsUrl { get; private set; }
    }

    public sealed class StagedUpdate
    {
        public StagedUpdate(string packagePath, string sha256, string version)
        {
            PackagePath = packagePath;
            Sha256 = sha256;
            Version = version;
        }

        public string PackagePath { get; private set; }
        public string Sha256 { get; private set; }
        public string Version { get; private set; }
    }

    /// <summary>
    /// Reads the fixed UPS Guardian GitHub release feed and stages a verified
    /// Windows package. This class deliberately has no UI or Windows API
    /// dependencies so it can also be used by the small update helper.
    /// </summary>
    public static class UpdateService
    {
        internal const string Repository = "kylefu8/ups-guardian";
        internal const string ApiUrl = "https://api.github.com/repos/kylefu8/ups-guardian/releases?per_page=20";
        internal const string PackagePrefix = "UPSGuardian-";
        internal const string PackageSuffix = "-windows-x64.zip";
        internal const string ChecksumsAssetName = "SHA256SUMS.txt";
        internal const int RequestTimeoutMilliseconds = 15000;
        internal const int MaximumApiResponseBytes = 1024 * 1024;
        internal const int MaximumChecksumBytes = 64 * 1024;
        internal const long MaximumPackageBytes = 256L * 1024L * 1024L;
        internal const int MaximumRedirects = 4;

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static UpdateCheckResult Check(string currentVersion)
        {
            SemVersion current = SemVersion.Parse(currentVersion);
            byte[] json;
            using (HttpWebResponse response = OpenResponse(new Uri(ApiUrl), false, CancellationToken.None))
                json = ReadBounded(response, MaximumApiResponseBytes, CancellationToken.None);
            return ParseManifest(json, current);
        }

        public static StagedUpdate Download(UpdateRelease release, string stageRoot, Action<int> progress, CancellationToken cancellation)
        {
            if (release == null)
                throw new ArgumentNullException("release");
            if (cancellation == null)
                throw new ArgumentNullException("cancellation");

            SemVersion version = SemVersion.Parse(release.Version);
            string normalizedVersion = version.ToString();
            string assetName = PackagePrefix + normalizedVersion + PackageSuffix;
            Uri packageUri = ValidateReleaseDownloadUri(release.PackageUrl, assetName);
            Uri checksumUri = ValidateReleaseDownloadUri(release.ChecksumsUrl, ChecksumsAssetName);
            string root = PrepareStageRoot(stageRoot);
            string finalPath = Path.Combine(root, assetName);
            string packageTemp = Path.Combine(root, "." + assetName + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                cancellation.ThrowIfCancellationRequested();
                byte[] checksumBytes;
                using (HttpWebResponse checksumResponse = OpenResponse(checksumUri, true, cancellation))
                    checksumBytes = ReadBounded(checksumResponse, MaximumChecksumBytes, cancellation);
                string expectedHash = ParseChecksum(checksumBytes, assetName);

                using (HttpWebResponse packageResponse = OpenResponse(packageUri, true, cancellation))
                using (Stream input = packageResponse.GetResponseStream())
                using (FileStream output = new FileStream(packageTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CopyBounded(input, output, MaximumPackageBytes, progress, cancellation,
                        packageResponse.ContentLength);
                }

                string actualHash = ComputeSha256(packageTemp, cancellation);
                if (!String.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded UPS Guardian package failed SHA-256 verification.");

                PromoteVerifiedPackage(packageTemp, finalPath, actualHash, cancellation);
                packageTemp = null;
                return new StagedUpdate(finalPath, actualHash, normalizedVersion);
            }
            finally
            {
                DeleteQuiet(packageTemp);
            }
        }

        internal static UpdateCheckResult ParseManifestForTests(string json, string currentVersion)
        {
            if (json == null)
                throw new ArgumentNullException("json");
            return ParseManifest(StrictUtf8.GetBytes(json), SemVersion.Parse(currentVersion));
        }

        internal static int CompareVersionsForTests(string left, string right)
        {
            return SemVersion.Parse(left).CompareTo(SemVersion.Parse(right));
        }

        internal static string VerifyPackageForTests(string packagePath, string expectedHash)
        {
            ValidateHash(expectedHash);
            if (String.IsNullOrEmpty(packagePath))
                throw new ArgumentException("packagePath is required.", "packagePath");
            return ComputeSha256(Path.GetFullPath(packagePath), CancellationToken.None);
        }

        internal static string ParseChecksumForTests(string text, string assetName)
        {
            if (text == null)
                throw new ArgumentNullException("text");
            return ParseChecksum(StrictUtf8.GetBytes(text), assetName);
        }

        private static UpdateCheckResult ParseManifest(byte[] json, SemVersion current)
        {
            GitHubReleaseDto[] releases;
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(GitHubReleaseDto[]));
                using (MemoryStream stream = new MemoryStream(json, false))
                    releases = (GitHubReleaseDto[])serializer.ReadObject(stream);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("GitHub returned an invalid release manifest.", ex);
            }

            if (releases == null || releases.Length == 0)
                return new UpdateCheckResult(UpdateStatus.NoReleases, null);

            UpdateRelease best = null;
            SemVersion bestVersion = null;
            for (int i = 0; i < releases.Length; i++)
            {
                GitHubReleaseDto dto = releases[i];
                if (dto == null || dto.Draft)
                    continue;
                UpdateRelease candidate;
                SemVersion candidateVersion;
                if (!TryBuildRelease(dto, out candidate, out candidateVersion))
                    continue;
                if (!current.IsPrerelease && candidateVersion.IsPrerelease)
                    continue;
                if (bestVersion == null || candidateVersion.CompareTo(bestVersion) > 0)
                {
                    best = candidate;
                    bestVersion = candidateVersion;
                }
            }

            if (bestVersion == null)
                return new UpdateCheckResult(UpdateStatus.NoReleases, null);
            if (bestVersion.CompareTo(current) > 0)
                return new UpdateCheckResult(UpdateStatus.Available, best);
            return new UpdateCheckResult(UpdateStatus.Current, best);
        }

        private static bool TryBuildRelease(GitHubReleaseDto dto, out UpdateRelease release, out SemVersion version)
        {
            release = null;
            version = null;
            try
            {
                version = SemVersion.Parse(dto.TagName);
                string assetName = PackagePrefix + version.ToString() + PackageSuffix;
                string package = null;
                string checksums = null;
                if (dto.Assets == null)
                    return false;
                for (int i = 0; i < dto.Assets.Length; i++)
                {
                    GitHubAssetDto asset = dto.Assets[i];
                    if (asset == null || String.IsNullOrEmpty(asset.Name))
                        continue;
                    if (String.Equals(asset.Name, assetName, StringComparison.Ordinal))
                    {
                        if (package != null)
                            return false;
                        package = ValidateReleaseDownloadUri(asset.BrowserDownloadUrl, assetName).AbsoluteUri;
                    }
                    else if (String.Equals(asset.Name, ChecksumsAssetName, StringComparison.Ordinal))
                    {
                        if (checksums != null)
                            return false;
                        checksums = ValidateReleaseDownloadUri(asset.BrowserDownloadUrl, ChecksumsAssetName).AbsoluteUri;
                    }
                }
                if (package == null || checksums == null)
                    return false;
                string releaseUrl = ValidateReleasePageUri(dto.HtmlUrl, dto.TagName).AbsoluteUri;
                release = new UpdateRelease(version.ToString(), dto.Body ?? String.Empty, releaseUrl, package, checksums);
                return true;
            }
            catch (Exception)
            {
                release = null;
                version = null;
                return false;
            }
        }

        private static string PrepareStageRoot(string stageRoot)
        {
            if (String.IsNullOrWhiteSpace(stageRoot))
                throw new ArgumentException("stageRoot is required.", "stageRoot");
            string root = Path.GetFullPath(stageRoot);
            if (File.Exists(root))
                throw new IOException("The update staging path is a file.");
            Directory.CreateDirectory(root);
            RejectReparsePoint(root, "update staging directory");
            return root;
        }

        private static void PromoteVerifiedPackage(string tempPath, string finalPath, string hash, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(finalPath))
            {
                RejectReparsePoint(finalPath, "staged package");
                string existing = ComputeSha256(finalPath, cancellation);
                if (String.Equals(existing, hash, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteQuiet(tempPath);
                    return;
                }
                File.Replace(tempPath, finalPath, null);
                return;
            }
            File.Move(tempPath, finalPath);
        }

        private static Uri ValidateReleaseDownloadUri(string value, string assetName)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("GitHub release asset URL must use HTTPS without a query or fragment.");
            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            string prefix = "/kylefu8/ups-guardian/releases/download/";
            string middle = path.Length > prefix.Length + assetName.Length
                ? path.Substring(prefix.Length, path.Length - prefix.Length - assetName.Length)
                : String.Empty;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.Length <= prefix.Length + assetName.Length ||
                !String.Equals(path.Substring(path.Length - assetName.Length), assetName, StringComparison.Ordinal) ||
                middle.Length < 2 || !middle.EndsWith("/", StringComparison.Ordinal) ||
                middle.IndexOf("..", StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("GitHub release asset URL is outside the fixed UPS Guardian repository.");
            return uri;
        }

        private static Uri ValidateReleasePageUri(string value, string tag)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("GitHub release page URL is invalid.");
            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (!path.StartsWith("/kylefu8/ups-guardian/releases/tag/", StringComparison.OrdinalIgnoreCase) ||
                path.IndexOf("..", StringComparison.Ordinal) >= 0 || String.IsNullOrEmpty(tag))
                throw new InvalidDataException("GitHub release page URL is outside the fixed repository.");
            return uri;
        }

        private static HttpWebResponse OpenResponse(Uri initial, bool releaseAsset, CancellationToken cancellation)
        {
            // A legacy .NET Framework host may otherwise default to SSL3/TLS1.0.
            // Preserve OS defaults or newer explicit choices; upgrade only legacy selections.
            SecurityProtocolType protocol = ServicePointManager.SecurityProtocol;
            if (protocol != SecurityProtocolType.SystemDefault && (int)protocol < (int)SecurityProtocolType.Tls12)
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Uri current = initial;
            for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                cancellation.ThrowIfCancellationRequested();
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(current);
                request.Method = "GET";
                request.AllowAutoRedirect = false;
                request.Timeout = RequestTimeoutMilliseconds;
                request.ReadWriteTimeout = RequestTimeoutMilliseconds;
                request.UserAgent = "UPSGuardian-Updater/0.1";
                request.Accept = "application/vnd.github+json";
                HttpWebResponse response = null;
                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                }
                catch (WebException ex)
                {
                    response = ex.Response as HttpWebResponse;
                    if (response == null)
                        throw new IOException("Could not contact the GitHub update service.", ex);
                }

                int status = (int)response.StatusCode;
                if (status >= 300 && status <= 399)
                {
                    string location = response.Headers[HttpResponseHeader.Location];
                    response.Close();
                    if (String.IsNullOrEmpty(location) || redirect == MaximumRedirects)
                        throw new InvalidDataException("GitHub returned an invalid redirect for an update download.");
                    Uri next = new Uri(current, location);
                    if (!IsAllowedRedirect(next, releaseAsset))
                        throw new InvalidDataException("GitHub update redirect left the allowed HTTPS hosts.");
                    current = next;
                    continue;
                }
                if (status < 200 || status >= 300)
                {
                    response.Close();
                    throw new InvalidDataException("GitHub update request returned HTTP " + status.ToString(CultureInfo.InvariantCulture) + ".");
                }
                return response;
            }
            throw new InvalidDataException("Too many redirects while contacting GitHub.");
        }

        private static bool IsAllowedRedirect(Uri uri, bool releaseAsset)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps)
                return false;
            if (!releaseAsset)
                return String.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
            return String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(uri.Host, "github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static byte[] ReadBounded(HttpWebResponse response, int maximumBytes, CancellationToken cancellation)
        {
            if (response.ContentLength > maximumBytes)
                throw new InvalidDataException("The GitHub update response exceeded its safety limit.");
            using (Stream input = response.GetResponseStream())
            using (MemoryStream output = new MemoryStream())
            {
                CopyBounded(input, output, maximumBytes, null, cancellation, response.ContentLength);
                return output.ToArray();
            }
        }

        private static void CopyBounded(Stream input, Stream output, long maximumBytes, Action<int> progress,
            CancellationToken cancellation, long contentLength)
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            int lastProgress = -1;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                total += read;
                if (total > maximumBytes)
                    throw new InvalidDataException("The GitHub update response exceeded its safety limit.");
                output.Write(buffer, 0, read);
                if (progress != null && contentLength > 0)
                {
                    int value = (int)Math.Min(100L, total * 100L / contentLength);
                    if (value != lastProgress)
                    {
                        lastProgress = value;
                        progress(value);
                    }
                }
            }
            if (progress != null)
                progress(100);
        }

        private static string ParseChecksum(byte[] bytes, string assetName)
        {
            if (bytes == null || String.IsNullOrEmpty(assetName))
                throw new ArgumentException("Checksum input is incomplete.");
            string text;
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("SHA256SUMS.txt is not valid UTF-8.", ex); }
            string found = null;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                int whitespace = 0;
                while (whitespace < line.Length && !Char.IsWhiteSpace(line[whitespace]))
                    whitespace++;
                if (whitespace == line.Length)
                    continue;
                string hash = line.Substring(0, whitespace);
                int nameStart = whitespace;
                while (nameStart < line.Length && Char.IsWhiteSpace(line[nameStart]))
                    nameStart++;
                if (nameStart < line.Length && line[nameStart] == '*')
                    nameStart++;
                string name = nameStart < line.Length ? line.Substring(nameStart).Trim() : String.Empty;
                if (!String.Equals(name, assetName, StringComparison.Ordinal))
                    continue;
                ValidateHash(hash);
                if (found != null)
                    throw new InvalidDataException("SHA256SUMS.txt contains duplicate entries for the update package.");
                found = hash.ToLowerInvariant();
            }
            if (found == null)
                throw new InvalidDataException("SHA256SUMS.txt did not contain the exact update package name.");
            return found;
        }

        private static string ComputeSha256(string path, CancellationToken cancellation)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("The update package was not found.", path);
            FileInfo info = new FileInfo(path);
            if (info.Length > MaximumPackageBytes)
                throw new InvalidDataException("The update package exceeded its safety limit.");
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                StringBuilder result = new StringBuilder(64);
                byte[] hash = sha.Hash;
                for (int i = 0; i < hash.Length; i++)
                    result.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        internal static void ValidateHashForTests(string hash)
        {
            ValidateHash(hash);
        }

        private static void ValidateHash(string hash)
        {
            if (String.IsNullOrEmpty(hash) || hash.Length != 64)
                throw new InvalidDataException("SHA-256 values must contain exactly 64 hexadecimal characters.");
            for (int i = 0; i < hash.Length; i++)
            {
                char ch = hash[i];
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
                    throw new InvalidDataException("SHA-256 value contained a non-hexadecimal character.");
            }
        }

        internal static void RejectReparsePointForTests(string path)
        {
            RejectReparsePoint(path, "path");
        }

        private static void RejectReparsePoint(string path, string description)
        {
            if (String.IsNullOrEmpty(path))
                throw new ArgumentException(description + " is required.", "path");
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The update " + description + " is a reparse point.");
        }

        private static void DeleteQuiet(string path)
        {
            if (String.IsNullOrEmpty(path))
                return;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        [DataContract]
        private sealed class GitHubReleaseDto
        {
            [DataMember(Name = "tag_name")]
            public string TagName { get; set; }
            [DataMember(Name = "html_url")]
            public string HtmlUrl { get; set; }
            [DataMember(Name = "body")]
            public string Body { get; set; }
            [DataMember(Name = "draft")]
            public bool Draft { get; set; }
            [DataMember(Name = "prerelease")]
            public bool Prerelease { get; set; }
            [DataMember(Name = "assets")]
            public GitHubAssetDto[] Assets { get; set; }
        }

        [DataContract]
        private sealed class GitHubAssetDto
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }
            [DataMember(Name = "browser_download_url")]
            public string BrowserDownloadUrl { get; set; }
        }

        internal sealed class SemVersion : IComparable<SemVersion>
        {
            private readonly int[] numbers;
            private readonly string[] prerelease;
            private readonly string build;

            private SemVersion(int major, int minor, int patch, string[] prereleaseParts, string buildPart)
            {
                numbers = new int[] { major, minor, patch };
                prerelease = prereleaseParts;
                build = buildPart;
            }

            internal bool IsPrerelease { get { return prerelease.Length != 0; } }

            internal static SemVersion Parse(string value)
            {
                if (String.IsNullOrEmpty(value))
                    throw new ArgumentException("A semantic version is required.", "value");
                string text = value.Trim();
                if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V'))
                    text = text.Substring(1);
                if (text.Length == 0 || text.IndexOf(' ') >= 0 || text.IndexOf('\t') >= 0)
                    throw new ArgumentException("Invalid semantic version: " + value, "value");
                string buildPart = String.Empty;
                int plus = text.IndexOf('+');
                if (plus >= 0)
                {
                    buildPart = text.Substring(plus + 1);
                    text = text.Substring(0, plus);
                    ValidateIdentifiers(buildPart, false, value);
                }
                string prereleasePart = String.Empty;
                int dash = text.IndexOf('-');
                if (dash >= 0)
                {
                    prereleasePart = text.Substring(dash + 1);
                    text = text.Substring(0, dash);
                    ValidateIdentifiers(prereleasePart, true, value);
                }
                string[] core = text.Split('.');
                if (core.Length != 3)
                    throw new ArgumentException("Semantic versions must contain major, minor, and patch numbers: " + value, "value");
                int major = ParseCoreNumber(core[0], value);
                int minor = ParseCoreNumber(core[1], value);
                int patch = ParseCoreNumber(core[2], value);
                string[] pre = prereleasePart.Length == 0 ? new string[0] : prereleasePart.Split('.');
                return new SemVersion(major, minor, patch, pre, buildPart);
            }

            private static int ParseCoreNumber(string text, string original)
            {
                if (String.IsNullOrEmpty(text) || (text.Length > 1 && text[0] == '0'))
                    throw new ArgumentException("Invalid semantic version: " + original, "value");
                int number;
                if (!Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number < 0)
                    throw new ArgumentException("Invalid semantic version: " + original, "value");
                return number;
            }

            private static void ValidateIdentifiers(string text, bool numericRules, string original)
            {
                if (String.IsNullOrEmpty(text))
                    throw new ArgumentException("Invalid semantic version: " + original, "value");
                string[] pieces = text.Split('.');
                for (int i = 0; i < pieces.Length; i++)
                {
                    string piece = pieces[i];
                    if (String.IsNullOrEmpty(piece))
                        throw new ArgumentException("Invalid semantic version: " + original, "value");
                    bool numeric = true;
                    for (int j = 0; j < piece.Length; j++)
                    {
                        char ch = piece[j];
                        if (!(ch >= '0' && ch <= '9'))
                        {
                            numeric = false;
                            if (!((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '-'))
                                throw new ArgumentException("Invalid semantic version: " + original, "value");
                        }
                    }
                    if (numericRules && numeric && piece.Length > 1 && piece[0] == '0')
                        throw new ArgumentException("Numeric prerelease identifiers may not contain leading zeroes: " + original, "value");
                }
            }

            public int CompareTo(SemVersion other)
            {
                if (other == null)
                    return 1;
                for (int i = 0; i < numbers.Length; i++)
                {
                    if (numbers[i] != other.numbers[i])
                        return numbers[i] < other.numbers[i] ? -1 : 1;
                }
                if (!IsPrerelease && !other.IsPrerelease)
                    return 0;
                if (!IsPrerelease)
                    return 1;
                if (!other.IsPrerelease)
                    return -1;
                int count = Math.Min(prerelease.Length, other.prerelease.Length);
                for (int i = 0; i < count; i++)
                {
                    string left = prerelease[i];
                    string right = other.prerelease[i];
                    bool leftNumeric = IsNumeric(left);
                    bool rightNumeric = IsNumeric(right);
                    if (leftNumeric && rightNumeric)
                    {
                        int comparison = CompareNumericIdentifier(left, right);
                        if (comparison != 0)
                            return comparison;
                    }
                    else if (leftNumeric != rightNumeric)
                    {
                        return leftNumeric ? -1 : 1;
                    }
                    else
                    {
                        int comparison = StringComparer.Ordinal.Compare(left, right);
                        if (comparison != 0)
                            return comparison < 0 ? -1 : 1;
                    }
                }
                if (prerelease.Length == other.prerelease.Length)
                    return 0;
                return prerelease.Length < other.prerelease.Length ? -1 : 1;
            }

            private static bool IsNumeric(string text)
            {
                for (int i = 0; i < text.Length; i++)
                    if (text[i] < '0' || text[i] > '9')
                        return false;
                return true;
            }

            private static int CompareNumericIdentifier(string left, string right)
            {
                left = left.TrimStart('0');
                right = right.TrimStart('0');
                if (left.Length != right.Length)
                    return left.Length < right.Length ? -1 : 1;
                return StringComparer.Ordinal.Compare(left, right);
            }

            public override string ToString()
            {
                StringBuilder result = new StringBuilder();
                result.Append(numbers[0].ToString(CultureInfo.InvariantCulture));
                result.Append('.');
                result.Append(numbers[1].ToString(CultureInfo.InvariantCulture));
                result.Append('.');
                result.Append(numbers[2].ToString(CultureInfo.InvariantCulture));
                if (prerelease.Length != 0)
                {
                    result.Append('-');
                    result.Append(String.Join(".", prerelease));
                }
                if (!String.IsNullOrEmpty(build))
                {
                    result.Append('+');
                    result.Append(build);
                }
                return result.ToString();
            }
        }
    }
}

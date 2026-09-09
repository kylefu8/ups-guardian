using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UpsGuardian
{
    /// <summary>A readable UPS returned by a bounded LAN discovery scan.</summary>
    public sealed class NutDiscoveryCandidate
    {
        public NutDiscoveryCandidate(string host, int port, string upsName, string description)
        {
            if (String.IsNullOrEmpty(host))
                throw new ArgumentException("host must be non-empty.", "host");
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException("port");
            if (String.IsNullOrEmpty(upsName))
                throw new ArgumentException("upsName must be non-empty.", "upsName");
            Host = host;
            Port = port;
            UpsName = upsName;
            Description = description ?? "";
        }

        public string Host { get; private set; }
        public int Port { get; private set; }
        public string UpsName { get; private set; }
        public string Description { get; private set; }
    }

    /// <summary>An explicit host/port endpoint included in a scan plan.</summary>
    public sealed class NutDiscoveryTarget
    {
        public NutDiscoveryTarget(string host, int port)
        {
            if (!NutClient.IsValidHost(host))
                throw new ArgumentException("host must be a non-empty ASCII hostname or address.", "host");
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException("port");
            Host = host;
            Port = port;
        }

        public string Host { get; private set; }
        public int Port { get; private set; }
    }

    /// <summary>
    /// Immutable description of the endpoints and local-network scope selected
    /// for discovery. The plan never selects a candidate on the user's behalf.
    /// </summary>
    public sealed class NutDiscoveryPlan
    {
        public const int MaxTargetCount = 1024;

        public NutDiscoveryPlan(IEnumerable<NutDiscoveryTarget> targets, string scopeDescription, bool isTruncated)
            : this(targets, new string[0], isTruncated, scopeDescription)
        {
        }

        public NutDiscoveryPlan(IEnumerable<NutDiscoveryTarget> targets, IEnumerable<string> ranges,
            bool isTruncated, string scopeDescription)
        {
            List<NutDiscoveryTarget> uniqueTargets = new List<NutDiscoveryTarget>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (targets != null)
            {
                foreach (NutDiscoveryTarget target in targets)
                {
                    if (target == null)
                        continue;
                    string key = target.Host + "\0" + target.Port.ToString();
                    if (!seen.Add(key))
                        continue;
                    if (uniqueTargets.Count >= MaxTargetCount)
                    {
                        isTruncated = true;
                        break;
                    }
                    uniqueTargets.Add(target);
                }
            }

            List<string> rangeList = new List<string>();
            if (ranges != null)
            {
                foreach (string range in ranges)
                {
                    if (!String.IsNullOrWhiteSpace(range) && !rangeList.Contains(range))
                        rangeList.Add(range);
                }
            }

            Targets = new ReadOnlyCollection<NutDiscoveryTarget>(uniqueTargets);
            Ranges = new ReadOnlyCollection<string>(rangeList);
            IsTruncated = isTruncated;
            ScopeDescription = scopeDescription ?? "";
        }

        public IReadOnlyList<NutDiscoveryTarget> Targets { get; private set; }
        public IReadOnlyList<string> Ranges { get; private set; }
        public bool IsTruncated { get; private set; }
        public string ScopeDescription { get; private set; }
    }

    /// <summary>Injectable read-only probe seam used by discovery tests and adapters.</summary>
    public interface INutDiscoveryProbe
    {
        IList<NutUpsInfo> ListUps(string host, int port, int timeoutMs, CancellationToken cancellationToken);
        NutSnapshot Read(string host, int port, string upsName, int timeoutMs, CancellationToken cancellationToken);
    }

    /// <summary>Bounded, read-only discovery of NUT servers on local IPv4 scopes.</summary>
    public static class NutDiscovery
    {
        public const int MaxConcurrentProbes = 16;
        public const int MaxTotalBudgetMilliseconds = 30000;
        public const int DefaultProbeTimeoutMilliseconds = 800;

        private sealed class DefaultProbe : INutDiscoveryProbe
        {
            public IList<NutUpsInfo> ListUps(string host, int port, int timeoutMs, CancellationToken cancellationToken)
            {
                return NutClient.ListUps(host, port, timeoutMs, cancellationToken);
            }

            public NutSnapshot Read(string host, int port, string upsName, int timeoutMs, CancellationToken cancellationToken)
            {
                return NutClient.Read(host, port, upsName, timeoutMs, cancellationToken);
            }
        }

        private sealed class LocalScope
        {
            public string Name;
            public IPAddress Address;
            public IPAddress Mask;
            public int PrefixLength;
            public bool HasGateway;
        }

        private static readonly INutDiscoveryProbe DefaultProbeInstance = new DefaultProbe();

        public static NutDiscoveryPlan CreateLocalPlan(int port)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException("port");

            List<LocalScope> scopes = ReadLocalScopes();
            if (scopes.Count == 0)
                return new NutDiscoveryPlan(new NutDiscoveryTarget[0], new string[0], false,
                    "No Up Ethernet or Wi-Fi IPv4 interface was found.");

            bool hasGatewayScope = false;
            for (int i = 0; i < scopes.Count; i++)
                hasGatewayScope |= scopes[i].HasGateway;

            List<LocalScope> selected = new List<LocalScope>();
            for (int i = 0; i < scopes.Count; i++)
            {
                if (!hasGatewayScope || scopes[i].HasGateway)
                    selected.Add(scopes[i]);
            }

            List<NutDiscoveryTarget> targets = new List<NutDiscoveryTarget>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> ranges = new List<string>();
            List<string> restrictedScopes = new List<string>();
            bool truncated = false;
            bool addressCapReached = false;
            for (int i = 0; i < selected.Count; i++)
            {
                LocalScope scope = selected[i];
                uint address = ToUInt32(scope.Address);
                uint originalNetwork = address & ToUInt32(scope.Mask);
                int scanPrefix = scope.PrefixLength < 24 ? 24 : scope.PrefixLength;
                uint scanMask = PrefixMask(scanPrefix);
                uint scanNetwork = address & scanMask;
                uint first;
                uint last;
                if (scanPrefix <= 30)
                {
                    first = scanNetwork + 1;
                    last = scanNetwork + ((uint)1 << (32 - scanPrefix)) - 2;
                }
                else
                {
                    first = scanNetwork;
                    last = scanNetwork + ((uint)1 << (32 - scanPrefix)) - 1;
                }

                string scanRange = FormatAddress(scanNetwork) + "/" + scanPrefix;
                ranges.Add(scope.Name + ": " + scanRange);
                if (scanPrefix != scope.PrefixLength)
                {
                    truncated = true;
                    restrictedScopes.Add(scope.Name + ": " + FormatAddress(originalNetwork) + "/" + scope.PrefixLength);
                }

                for (uint value = first; value <= last; value++)
                {
                    if (targets.Count >= NutDiscoveryPlan.MaxTargetCount)
                    {
                        addressCapReached = true;
                        break;
                    }
                    string host = FormatAddress(value);
                    string key = host + "\0" + port.ToString();
                    if (seen.Add(key))
                        targets.Add(new NutDiscoveryTarget(host, port));
                }
                if (addressCapReached)
                    break;
            }

            string description = hasGatewayScope
                ? "Up Ethernet/Wi-Fi IPv4 interfaces with a gateway were selected; other interfaces were excluded."
                : "No gateway-bearing interface was found; all eligible Up Ethernet/Wi-Fi IPv4 interfaces are included as a fallback."
                ;
            if (addressCapReached)
                description += " The address scan is limited to 1024 targets.";
            if (restrictedScopes.Count > 0)
                description += " Large networks are restricted to the local /24 from " +
                    String.Join(", ", restrictedScopes.ToArray()) + ".";
            return new NutDiscoveryPlan(targets, ranges, truncated || addressCapReached, description);
        }

        public static List<NutDiscoveryCandidate> Scan(NutDiscoveryPlan plan, CancellationToken cancellationToken)
        {
            return Scan(plan, DefaultProbeInstance, MaxTotalBudgetMilliseconds, cancellationToken);
        }

        /// <summary>
        /// Scans a supplied plan using at most 16 workers and one global budget.
        /// Endpoint errors are isolated; only LIST UPS entries followed by a
        /// successful LIST VAR response with a non-empty status are returned.
        /// </summary>
        public static List<NutDiscoveryCandidate> Scan(NutDiscoveryPlan plan, INutDiscoveryProbe probe,
            int totalBudgetMilliseconds, CancellationToken cancellationToken)
        {
            if (plan == null)
                throw new ArgumentNullException("plan");
            if (probe == null)
                throw new ArgumentNullException("probe");
            if (totalBudgetMilliseconds < 0)
                throw new ArgumentOutOfRangeException("totalBudgetMilliseconds");

            List<NutDiscoveryCandidate> result = new List<NutDiscoveryCandidate>();
            if (totalBudgetMilliseconds == 0 || plan.Targets.Count == 0)
                return result;

            int budget = Math.Min(totalBudgetMilliseconds, MaxTotalBudgetMilliseconds);
            long deadline = Stopwatch.GetTimestamp() +
                (long)Math.Ceiling(budget * (double)Stopwatch.Frequency / 1000.0);
            CancellationTokenSource budgetCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCancellation.CancelAfter(budget);
            CancellationToken scanToken = budgetCancellation.Token;
            object resultLock = new object();
            HashSet<string> candidateKeys = new HashSet<string>(StringComparer.Ordinal);
            int nextTarget = -1;
            int workerCount = Math.Min(MaxConcurrentProbes, plan.Targets.Count);
            List<Task> workers = new List<Task>();

            Action worker = delegate
            {
                while (!scanToken.IsCancellationRequested)
                {
                    int remaining = RemainingMilliseconds(deadline);
                    if (remaining <= 0)
                        break;
                    int index = Interlocked.Increment(ref nextTarget);
                    if (index >= plan.Targets.Count)
                        break;
                    NutDiscoveryTarget target = plan.Targets[index];
                    try
                    {
                        int timeout = Math.Max(1, Math.Min(DefaultProbeTimeoutMilliseconds,
                            RemainingMilliseconds(deadline)));
                        IList<NutUpsInfo> upsList = probe.ListUps(target.Host, target.Port, timeout, scanToken);
                        if (upsList == null)
                            continue;
                        for (int i = 0; i < upsList.Count; i++)
                        {
                            if (scanToken.IsCancellationRequested || RemainingMilliseconds(deadline) <= 0)
                                break;
                            NutUpsInfo ups = upsList[i];
                            if (ups == null || String.IsNullOrEmpty(ups.UpsName))
                                continue;
                            try
                            {
                                timeout = Math.Max(1, Math.Min(DefaultProbeTimeoutMilliseconds,
                                    RemainingMilliseconds(deadline)));
                                NutSnapshot snapshot = probe.Read(target.Host, target.Port, ups.UpsName, timeout, scanToken);
                                if (scanToken.IsCancellationRequested || RemainingMilliseconds(deadline) <= 0)
                                    break;
                                if (snapshot == null || String.IsNullOrWhiteSpace(snapshot.Status))
                                    continue;
                                string description = ups.Description;
                                if ((String.IsNullOrWhiteSpace(description) ||
                                     String.Equals(description.Trim(), "Description unavailable", StringComparison.OrdinalIgnoreCase)) &&
                                    !String.IsNullOrWhiteSpace(snapshot.Model))
                                    description = snapshot.Model;
                                NutDiscoveryCandidate candidate = new NutDiscoveryCandidate(target.Host, target.Port,
                                    ups.UpsName, description);
                                string key = candidate.Host.ToUpperInvariant() + "\0" + candidate.Port.ToString() + "\0" + candidate.UpsName;
                                lock (resultLock)
                                {
                                    if (candidateKeys.Add(key))
                                        result.Add(candidate);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception)
                            {
                                // One UPS on a server can be unreadable while
                                // another UPS on the same server remains valid.
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        // A dead, non-NUT, malformed, or slow endpoint must
                        // not abort the remaining bounded scan.
                    }
                }
            };

            for (int i = 0; i < workerCount; i++)
                workers.Add(Task.Factory.StartNew(worker, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default));
            try
            {
                Task.WaitAll(workers.ToArray(), budget);
            }
            catch (AggregateException)
            {
                // Worker endpoint failures are isolated above. Keep partial results
                // if a task was interrupted by an external cancellation.
            }
            catch (Exception)
            {
                // The scan is best-effort and always returns collected candidates.
            }

            budgetCancellation.Cancel();
            bool allComplete = true;
            for (int i = 0; i < workers.Count; i++)
                allComplete &= workers[i].IsCompleted;
            if (allComplete)
                budgetCancellation.Dispose();
            else
            {
                Task.Factory.ContinueWhenAll(workers.ToArray(), delegate(Task[] completed)
                {
                    budgetCancellation.Dispose();
                });
            }

            lock (resultLock)
            {
                result.Sort(delegate(NutDiscoveryCandidate left, NutDiscoveryCandidate right)
                {
                    int host = StringComparer.OrdinalIgnoreCase.Compare(left.Host, right.Host);
                    if (host != 0) return host;
                    int port = left.Port.CompareTo(right.Port);
                    if (port != 0) return port;
                    return StringComparer.Ordinal.Compare(left.UpsName, right.UpsName);
                });
                return new List<NutDiscoveryCandidate>(result);
            }
        }

        private static List<LocalScope> ReadLocalScopes()
        {
            List<LocalScope> result = new List<LocalScope>();
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch
            {
                return result;
            }

            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface networkInterface = interfaces[i];
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    (networkInterface.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                     networkInterface.NetworkInterfaceType != NetworkInterfaceType.Wireless80211))
                    continue;
                try
                {
                    IPInterfaceProperties properties = networkInterface.GetIPProperties();
                    bool hasGateway = false;
                    for (int g = 0; g < properties.GatewayAddresses.Count; g++)
                    {
                        IPAddress gateway = properties.GatewayAddresses[g].Address;
                        if (gateway.AddressFamily == AddressFamily.InterNetwork && !gateway.Equals(IPAddress.Any))
                        {
                            hasGateway = true;
                            break;
                        }
                    }
                    for (int u = 0; u < properties.UnicastAddresses.Count; u++)
                    {
                        UnicastIPAddressInformation address = properties.UnicastAddresses[u];
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask == null)
                            continue;
                        int prefix = PrefixLength(address.IPv4Mask);
                        if (prefix < 0)
                            continue;
                        result.Add(new LocalScope
                        {
                            Name = networkInterface.Name,
                            Address = address.Address,
                            Mask = address.IPv4Mask,
                            PrefixLength = prefix,
                            HasGateway = hasGateway
                        });
                    }
                }
                catch
                {
                    // An interface can disappear while its properties are read.
                }
            }
            return result;
        }

        private static int PrefixLength(IPAddress mask)
        {
            uint value = ToUInt32(mask);
            int prefix = 0;
            while ((value & 0x80000000U) != 0)
            {
                prefix++;
                value <<= 1;
            }
            return value == 0 ? prefix : -1;
        }

        private static uint PrefixMask(int prefix)
        {
            return prefix == 0 ? 0U : UInt32.MaxValue << (32 - prefix);
        }

        private static uint ToUInt32(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                ((uint)bytes[2] << 8) | bytes[3];
        }

        private static string FormatAddress(uint value)
        {
            return new IPAddress(new byte[]
            {
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            }).ToString();
        }

        private static int RemainingMilliseconds(long deadline)
        {
            long remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                return 0;
            double milliseconds = remaining * 1000.0 / Stopwatch.Frequency;
            return milliseconds >= Int32.MaxValue ? Int32.MaxValue : Math.Max(1, (int)Math.Ceiling(milliseconds));
        }
    }
}

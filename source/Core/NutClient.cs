using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace UpsGuardian
{
    /// <summary>Read-only values returned by a NUT LIST VAR request.</summary>
    public sealed class NutSnapshot
    {
        /// <summary>Builds the same derived snapshot shape for policy/unit tests.</summary>
        public static NutSnapshot FromVariables(Dictionary<string, string> variables, DateTime receivedUtc)
        {
            if (variables == null)
                throw new ArgumentNullException("variables");
            return new NutSnapshot(new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase), receivedUtc);
        }

        internal NutSnapshot(Dictionary<string, string> variables, DateTime receivedUtc)
        {
            Variables = variables;
            ReceivedUtc = receivedUtc;
            Status = Get(variables, "ups.status");
            Model = Get(variables, "device.model");
            if (Model == null)
                Model = Get(variables, "ups.model");
            // NUT exposes the present UPS load as ups.load.
            LoadPercent = NutClient.GetNumber(variables, "ups.load", 0.0, 1000.0);
            NominalWatts = NutClient.GetNumber(variables, "ups.realpower.nominal", 0.0, 1000000.0);

            // ups.realpower is the only accepted measured-watts source.  In
            // particular, ups.power is apparent VA and must not be used here.
            MeasuredWatts = NutClient.GetNumber(variables, "ups.realpower", 0.0, 1000000.0);
            ChargePercent = NutClient.GetNumber(variables, "battery.charge", 0.0, 100.0);
            RuntimeSeconds = NutClient.GetNumber(variables, "battery.runtime", 0.0, 604800.0);

            if (LoadPercent.HasValue && NominalWatts.HasValue)
            {
                double estimate = LoadPercent.Value * NominalWatts.Value / 100.0;
                EstimatedWatts = NutClient.IsFinite(estimate) && estimate >= 0.0 && estimate <= 10000000.0
                    ? (double?)estimate
                    : null;
            }

            OnBattery = NutClient.HasStatusToken(Status, "OB");
            OnLine = NutClient.HasStatusToken(Status, "OL");
        }

        public Dictionary<string, string> Variables { get; private set; }
        public DateTime ReceivedUtc { get; private set; }
        public string Status { get; private set; }
        public string Model { get; private set; }
        public double? LoadPercent { get; private set; }
        public double? NominalWatts { get; private set; }
        public double? MeasuredWatts { get; private set; }
        public double? EstimatedWatts { get; private set; }
        public double? ChargePercent { get; private set; }
        public double? RuntimeSeconds { get; private set; }
        public bool OnBattery { get; private set; }
        public bool OnLine { get; private set; }

        private static string Get(Dictionary<string, string> variables, string key)
        {
            string value;
            return variables.TryGetValue(key, out value) ? value : null;
        }
    }

    /// <summary>Indicates a malformed or rejected NUT protocol response.</summary>
    public sealed class NutException : IOException
    {
        public NutException(string message) : base(message) { }
        internal NutException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Small, bounded, read-only NUT protocol client.</summary>
    public static class NutClient
    {
        private const int MaxResponseBytes = 64 * 1024;
        private const int MaxLines = 512;
        private const int MaxLineBytes = 4096;
        private const int MaxUpsNameLength = 64;
        private const int MaxHostLength = 253;

        private static readonly Regex HostPattern = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9._:%-]{0,252}\z",
            RegexOptions.CultureInvariant);
        private static readonly Regex UpsNamePattern = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Connects once, sends LIST VAR, and reads one complete response. No
        /// credentials, retries, background threads, or UPS mutations are used.
        /// </summary>
        public static NutSnapshot Read(string host, int port, string upsName, int timeoutMs)
        {
            ValidateArguments(host, port, upsName, timeoutMs);
            long deadline = Deadline(timeoutMs);
            TcpClient client = new TcpClient();
            try
            {
                Connect(client, host, port, deadline);
                Socket socket = client.Client;
                NetworkStream stream = client.GetStream();
                string command = "LIST VAR " + upsName + "\n";
                byte[] commandBytes = Encoding.ASCII.GetBytes(command);
                WriteCommand(stream, socket, commandBytes, deadline);
                Dictionary<string, string> variables = ReadResponse(stream, socket, upsName, deadline);
                return new NutSnapshot(variables, DateTime.UtcNow);
            }
            finally
            {
                client.Close();
            }
        }

        private static void ValidateArguments(string host, int port, string upsName, int timeoutMs)
        {
            if (String.IsNullOrEmpty(host) || host.Length > MaxHostLength || !HostPattern.IsMatch(host))
                throw new ArgumentException("host must be a non-empty ASCII hostname or address (1-253 safe characters).", "host");
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException("port", "port must be between 1 and 65535.");
            if (String.IsNullOrEmpty(upsName) || upsName.Length > MaxUpsNameLength || !UpsNamePattern.IsMatch(upsName))
                throw new ArgumentException("upsName must be a non-empty ASCII NUT name (1-64 safe characters).", "upsName");
            if (timeoutMs < 1)
                throw new ArgumentOutOfRangeException("timeoutMs", "timeoutMs must be positive.");
        }

        private static long Deadline(int timeoutMs)
        {
            double budget = timeoutMs * (double)Stopwatch.Frequency / 1000.0;
            return Stopwatch.GetTimestamp() + (long)Math.Ceiling(budget);
        }

        private static int RemainingMilliseconds(long deadline)
        {
            long remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                return 0;
            double milliseconds = remaining * 1000.0 / Stopwatch.Frequency;
            if (milliseconds >= Int32.MaxValue)
                return Int32.MaxValue;
            return Math.Max(1, (int)Math.Ceiling(milliseconds));
        }

        private static void Connect(TcpClient client, string host, int port, long deadline)
        {
            IAsyncResult result = null;
            try
            {
                result = client.BeginConnect(host, port, null, null);
                int remaining = RemainingMilliseconds(deadline);
                if (remaining == 0 || !result.AsyncWaitHandle.WaitOne(remaining))
                    throw new TimeoutException("Timed out connecting to NUT server " + host + ":" + port + ".");
                client.EndConnect(result);
                client.NoDelay = true;
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new NutException("Could not connect to NUT server " + host + ":" + port + ".", ex);
            }
            finally
            {
                if (result != null && result.AsyncWaitHandle != null)
                    result.AsyncWaitHandle.Close();
            }
        }

        private static void WriteCommand(NetworkStream stream, Socket socket, byte[] bytes, long deadline)
        {
            int remaining = RemainingMilliseconds(deadline);
            if (remaining == 0)
                throw new TimeoutException("Timed out before sending NUT request.");
            socket.SendTimeout = remaining;
            if (!socket.Poll(PollMicroseconds(remaining), SelectMode.SelectWrite))
                throw new TimeoutException("Timed out sending NUT request.");
            try
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            catch (IOException ex)
            {
                if (RemainingMilliseconds(deadline) == 0)
                    throw new TimeoutException("Timed out sending NUT request.", ex);
                throw new NutException("Could not send NUT request.", ex);
            }
        }

        private static int PollMicroseconds(int milliseconds)
        {
            long microseconds = milliseconds * 1000L;
            return microseconds >= Int32.MaxValue ? Int32.MaxValue : (int)microseconds;
        }

        private static Dictionary<string, string> ReadResponse(NetworkStream stream, Socket socket, string upsName, long deadline)
        {
            Dictionary<string, string> variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            byte[] buffer = new byte[4096];
            List<byte> lineBytes = new List<byte>();
            int totalBytes = 0;
            int lineCount = 0;
            bool began = false;

            while (true)
            {
                int remaining = RemainingMilliseconds(deadline);
                if (remaining == 0)
                    throw new TimeoutException("Timed out reading NUT response.");
                socket.ReceiveTimeout = remaining;
                int read;
                try
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                catch (IOException ex)
                {
                    if (RemainingMilliseconds(deadline) == 0 || IsSocketTimeout(ex))
                        throw new TimeoutException("Timed out reading NUT response.", ex);
                    throw new NutException("Could not read NUT response.", ex);
                }
                if (read == 0)
                    throw new NutException("NUT connection closed before a complete BEGIN/END response was received.");
                totalBytes += read;
                if (totalBytes > MaxResponseBytes)
                    throw new NutException("NUT response exceeded the 64 KiB safety limit.");

                for (int i = 0; i < read; i++)
                {
                    byte value = buffer[i];
                    if (value == (byte)'\n')
                    {
                        lineCount++;
                        if (lineCount > MaxLines)
                            throw new NutException("NUT response exceeded the 512-line safety limit.");
                        string line = DecodeLine(lineBytes);
                        lineBytes.Clear();
                        bool end = ParseLine(line, upsName, variables, ref began);
                        if (end)
                            return variables;
                    }
                    else
                    {
                        if (lineBytes.Count >= MaxLineBytes)
                            throw new NutException("A NUT response line exceeded the 4096-byte safety limit.");
                        lineBytes.Add(value);
                    }
                }
            }
        }

        private static bool IsSocketTimeout(IOException exception)
        {
            SocketException socketException = exception.InnerException as SocketException;
            return socketException != null &&
                (socketException.SocketErrorCode == SocketError.TimedOut ||
                 socketException.SocketErrorCode == SocketError.WouldBlock);
        }

        private static string DecodeLine(List<byte> bytes)
        {
            if (bytes.Count > 0 && bytes[bytes.Count - 1] == (byte)'\r')
                bytes.RemoveAt(bytes.Count - 1);
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes.ToArray());
            }
            catch (DecoderFallbackException ex)
            {
                throw new NutException("NUT response contained invalid UTF-8 data.", ex);
            }
        }

        private static bool ParseLine(string line, string upsName, Dictionary<string, string> variables, ref bool began)
        {
            List<Token> tokens = Tokenize(line);
            if (tokens.Count == 0)
                throw new NutException("NUT response contained an unexpected blank line.");
            if (String.Equals(tokens[0].Text, "ERR", StringComparison.Ordinal))
            {
                throw new NutException("NUT server returned ERR " + line.Trim() + ". Check the UPS name and NUT access whitelist.");
            }

            if (!began)
            {
                RequireCommand(tokens, "BEGIN", "LIST", "VAR", upsName, "BEGIN LIST VAR " + upsName);
                began = true;
                return false;
            }

            if (String.Equals(tokens[0].Text, "END", StringComparison.Ordinal))
            {
                RequireCommand(tokens, "END", "LIST", "VAR", upsName, "END LIST VAR " + upsName);
                return true;
            }

            if (tokens.Count != 4 || !String.Equals(tokens[0].Text, "VAR", StringComparison.Ordinal) ||
                !String.Equals(tokens[1].Text, upsName, StringComparison.Ordinal) || tokens[2].Quoted || !tokens[3].Quoted)
                throw new NutException("Malformed NUT VAR line; expected VAR " + upsName + " <name> \"<value>\".");
            if (String.IsNullOrEmpty(tokens[2].Text) || tokens[2].Text.Length > 256 ||
                !Regex.IsMatch(tokens[2].Text, @"^[A-Za-z0-9._:-]+\z", RegexOptions.CultureInvariant))
                throw new NutException("Malformed NUT variable name.");
            variables[tokens[2].Text] = tokens[3].Text;
            return false;
        }

        private static void RequireCommand(List<Token> tokens, string first, string second, string third, string fourth, string expected)
        {
            if (tokens.Count != 4 || !String.Equals(tokens[0].Text, first, StringComparison.Ordinal) ||
                !String.Equals(tokens[1].Text, second, StringComparison.Ordinal) ||
                !String.Equals(tokens[2].Text, third, StringComparison.Ordinal) ||
                !String.Equals(tokens[3].Text, fourth, StringComparison.Ordinal))
                throw new NutException("Malformed NUT response; expected " + expected + ".");
        }

        private sealed class Token
        {
            internal Token(string text, bool quoted)
            {
                Text = text;
                Quoted = quoted;
            }
            internal string Text;
            internal bool Quoted;
        }

        private static List<Token> Tokenize(string line)
        {
            List<Token> result = new List<Token>();
            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
                    i++;
                if (i >= line.Length)
                    break;
                if (line[i] == '"')
                {
                    i++;
                    StringBuilder value = new StringBuilder();
                    bool closed = false;
                    while (i < line.Length)
                    {
                        char ch = line[i++];
                        if (ch == '"')
                        {
                            closed = true;
                            break;
                        }
                        if (ch == '\\')
                        {
                            if (i >= line.Length)
                                throw new NutException("Malformed NUT quoted value: dangling escape.");
                            char escaped = line[i++];
                            switch (escaped)
                            {
                                case 'n': value.Append('\n'); break;
                                case 'r': value.Append('\r'); break;
                                case 't': value.Append('\t'); break;
                                case '"': value.Append('"'); break;
                                case '\\': value.Append('\\'); break;
                                default: value.Append(escaped); break;
                            }
                        }
                        else
                        {
                            value.Append(ch);
                        }
                    }
                    if (!closed)
                        throw new NutException("Malformed NUT quoted value: missing closing quote.");
                    if (i < line.Length && line[i] != ' ' && line[i] != '\t')
                        throw new NutException("Malformed NUT quoted value: unexpected trailing characters.");
                    result.Add(new Token(value.ToString(), true));
                }
                else
                {
                    int start = i;
                    while (i < line.Length && line[i] != ' ' && line[i] != '\t')
                        i++;
                    result.Add(new Token(line.Substring(start, i - start), false));
                }
            }
            return result;
        }

        internal static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        internal static double? GetNumber(Dictionary<string, string> variables, string key, double minimum, double maximum)
        {
            string raw;
            double value;
            if (!variables.TryGetValue(key, out raw) || String.IsNullOrWhiteSpace(raw) ||
                !Double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                !IsFinite(value) || value < minimum || value > maximum)
                return null;
            return value;
        }

        internal static bool HasStatusToken(string status, string token)
        {
            if (String.IsNullOrEmpty(status))
                return false;
            string[] values = status.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < values.Length; i++)
            {
                if (String.Equals(values[i], token, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}

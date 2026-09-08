using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UpsGuardian;

internal static class NutClientTests
{
    private static int passed;
    private static int failed;

    private static int Main()
    {
        Run("nominal estimate", TestNominalEstimate);
        Run("real watts precedence", TestMeasuredRealPowerPrecedence);
        Run("missing values", TestMissingValues);
        Run("overload estimate", TestOverloadEstimate);
        Run("status tokens and quoted escapes", TestStatusTokensAndEscapes);
        Run("exact status token", TestExactStatusToken);
        Run("access denied", TestAccessDenied);
        Run("incomplete response", TestIncompleteResponse);
        Run("malformed response", TestMalformedResponse);
        Run("bounded timeout", TestTimeout);
        Run("argument validation", TestArgumentValidation);
        Console.WriteLine("Passed {0}; failed {1}.", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine("PASS: " + name);
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine("FAIL: " + name + " - " + ex.Message);
        }
    }

    private static void TestNominalEstimate()
    {
        string response =
            "BEGIN LIST VAR ups\n" +
            "VAR ups ups.status \"OL\"\n" +
            "VAR ups device.model \"APC BX1200CI-CN\"\n" +
            "VAR ups ups.load \"40\"\n" +
            "VAR ups ups.realpower.nominal \"650\"\n" +
            "VAR ups battery.charge \"100\"\n" +
            "VAR ups battery.runtime \"628\"\n" +
            "END LIST VAR ups\n";
        using (FakeNutServer server = new FakeNutServer(response))
        {
            NutSnapshot snapshot = NutClient.Read("127.0.0.1", server.Port, "ups", 2000);
            AssertNear(snapshot.EstimatedWatts, 260.0, "ups.load * nominal should estimate 260 W");
            Assert(!snapshot.MeasuredWatts.HasValue, "measured watts should be null when ups.realpower is absent");
            Assert(snapshot.OnLine && !snapshot.OnBattery, "OL should indicate online only");
            AssertEqual(snapshot.Model, "APC BX1200CI-CN", "device.model");
            AssertNear(snapshot.ChargePercent, 100.0, "battery charge");
            AssertNear(snapshot.RuntimeSeconds, 628.0, "battery runtime");
        }
    }

    private static void TestMeasuredRealPowerPrecedence()
    {
        string response =
            "BEGIN LIST VAR ups\n" +
            "VAR ups ups.status \"OL\"\n" +
            "VAR ups ups.load \"40\"\n" +
            "VAR ups ups.realpower.nominal \"650\"\n" +
            "VAR ups ups.realpower \"123\"\n" +
            "VAR ups ups.power \"999\"\n" +
            "END LIST VAR ups\n";
        using (FakeNutServer server = new FakeNutServer(response))
        {
            NutSnapshot snapshot = NutClient.Read("127.0.0.1", server.Port, "ups", 2000);
            AssertNear(snapshot.MeasuredWatts, 123.0, "ups.realpower must be measured watts");
            AssertNear(snapshot.EstimatedWatts, 260.0, "estimate remains ups.load * nominal");
            Assert(snapshot.MeasuredWatts.Value != 999.0, "apparent VA ups.power must not be treated as watts");
        }
    }

    private static void TestMissingValues()
    {
        const string response = "BEGIN LIST VAR ups\nVAR ups ups.status \"OL\"\nVAR ups ups.model \"Fallback Model\"\nEND LIST VAR ups\n";
        using (FakeNutServer server = new FakeNutServer(response))
        {
            NutSnapshot snapshot = NutClient.Read("127.0.0.1", server.Port, "ups", 2000);
            Assert(!snapshot.LoadPercent.HasValue, "missing ups.load should be null");
            Assert(!snapshot.NominalWatts.HasValue, "missing nominal watts should be null");
            Assert(!snapshot.MeasuredWatts.HasValue, "missing realpower should be null");
            Assert(!snapshot.EstimatedWatts.HasValue, "estimate needs both inputs");
            Assert(!snapshot.ChargePercent.HasValue, "missing battery charge should be null");
            Assert(!snapshot.RuntimeSeconds.HasValue, "missing battery runtime should be null");
            AssertEqual(snapshot.Model, "Fallback Model", "ups.model fallback");
        }
    }

    private static void TestOverloadEstimate()
    {
        const string response =
            "BEGIN LIST VAR ups\n" +
            "VAR ups ups.status \"OL\"\n" +
            "VAR ups ups.load \"115\"\n" +
            "VAR ups ups.realpower.nominal \"650\"\n" +
            "END LIST VAR ups\n";
        using (FakeNutServer server = new FakeNutServer(response))
        {
            NutSnapshot snapshot = NutClient.Read("127.0.0.1", server.Port, "ups", 2000);
            AssertNear(snapshot.LoadPercent, 115.0, "overload load percent");
            AssertNear(snapshot.EstimatedWatts, 747.5, "115 percent of 650 W");
        }
    }

    private static void TestStatusTokensAndEscapes()
    {
        string response =
            "BEGIN LIST VAR ups\n" +
            "VAR ups ups.status \"OB LB\"\n" +
            "VAR ups device.model \"APC \\\"BX\\\\1200\\\"\"\n" +
            "VAR ups note \"line\\nnext\"\n" +
            "END LIST VAR ups\n";
        using (FakeNutServer server = new FakeNutServer(response))
        {
            NutSnapshot snapshot = NutClient.Read("127.0.0.1", server.Port, "ups", 2000);
            Assert(snapshot.OnBattery, "OB must be an exact on-battery token");
            Assert(!snapshot.OnLine, "OB LB must not imply OL");
            AssertEqual(snapshot.Model, "APC \"BX\\1200\"", "escaped model");
            AssertEqual(snapshot.Variables["note"], "line\nnext", "escaped newline value");
        }
    }

    private static void TestExactStatusToken()
    {
        const string response = "BEGIN LIST VAR ups\nVAR ups ups.status \"OLB\"\nEND LIST VAR ups\n";
        using (FakeNutServer server = new FakeNutServer(response))
        {
            NutSnapshot snapshot = NutClient.Read("127.0.0.1", server.Port, "ups", 2000);
            Assert(!snapshot.OnLine && !snapshot.OnBattery, "OLB must not match OL or OB by substring");
        }
    }

    private static void TestAccessDenied()
    {
        using (FakeNutServer server = new FakeNutServer("ERR ACCESS-DENIED\n"))
        {
            AssertThrows<NutException>(delegate { NutClient.Read("127.0.0.1", server.Port, "ups", 2000); }, "ACCESS-DENIED");
        }
    }

    private static void TestIncompleteResponse()
    {
        const string response = "BEGIN LIST VAR ups\nVAR ups ups.status \"OL\"\n";
        using (FakeNutServer server = new FakeNutServer(response))
        {
            AssertThrows<NutException>(delegate { NutClient.Read("127.0.0.1", server.Port, "ups", 2000); }, "complete");
        }
    }

    private static void TestMalformedResponse()
    {
        const string response = "BEGIN LIST VAR other\nEND LIST VAR other\n";
        using (FakeNutServer server = new FakeNutServer(response))
        {
            AssertThrows<NutException>(delegate { NutClient.Read("127.0.0.1", server.Port, "ups", 2000); }, "expected BEGIN");
        }
    }

    private static void TestTimeout()
    {
        Stopwatch watch = Stopwatch.StartNew();
        using (FakeNutServer server = new FakeNutServer(
            "BEGIN LIST VAR ups\nEND LIST VAR ups\n", 1200))
        {
            AssertThrows<TimeoutException>(delegate { NutClient.Read("127.0.0.1", server.Port, "ups", 500); }, "Timed out");
        }
        watch.Stop();
        Assert(watch.ElapsedMilliseconds < 2500, "500 ms timeout should remain bounded");
    }

    private static void TestArgumentValidation()
    {
        AssertThrows<ArgumentException>(delegate { NutClient.Read("127.0.0.1\n", 3493, "ups", 1000); }, "host");
        AssertThrows<ArgumentOutOfRangeException>(delegate { NutClient.Read("127.0.0.1", 0, "ups", 1000); }, "port");
        AssertThrows<ArgumentException>(delegate { NutClient.Read("127.0.0.1", 3493, "ups bad", 1000); }, "upsName");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertEqual(string actual, string expected, string name)
    {
        Assert(String.Equals(actual, expected, StringComparison.Ordinal), name + " expected '" + expected + "' but got '" + actual + "'.");
    }

    private static void AssertNear(double? actual, double expected, string name)
    {
        Assert(actual.HasValue && Math.Abs(actual.Value - expected) < 0.0001, name + " expected " + expected + ".");
    }

    private static void AssertThrows<T>(Action action, string messagePart) where T : Exception
    {
        try
        {
            action();
        }
        catch (T ex)
        {
            if (!String.IsNullOrEmpty(messagePart) && ex.Message.IndexOf(messagePart, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Exception did not contain '" + messagePart + "': " + ex.Message);
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Expected " + typeof(T).Name + " but got " + ex.GetType().Name + ".", ex);
        }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    private sealed class FakeNutServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread thread;
        private readonly string response;
        private readonly int delayMs;
        private Exception serverException;

        internal FakeNutServer(string response) : this(response, 0) { }

        internal FakeNutServer(string response, int delayMs)
        {
            this.response = response;
            this.delayMs = delayMs;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            thread = new Thread(Serve);
            thread.IsBackground = true;
            thread.Start();
        }

        internal int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

        private void Serve()
        {
            try
            {
                using (TcpClient client = listener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    int value;
                    do
                    {
                        value = stream.ReadByte();
                    }
                    while (value >= 0 && value != '\n');
                    if (delayMs > 0)
                        Thread.Sleep(delayMs);
                    byte[] bytes = Encoding.UTF8.GetBytes(response);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                serverException = ex;
            }
        }

        public void Dispose()
        {
            listener.Stop();
            if (thread.IsAlive)
                thread.Join(3000);
            if (serverException != null && !(serverException is IOException) && !(serverException is SocketException))
                throw new InvalidOperationException("Fake server failed.", serverException);
        }
    }
}

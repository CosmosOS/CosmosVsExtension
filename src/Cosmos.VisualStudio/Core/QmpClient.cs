using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>
    /// Minimal QEMU Machine Protocol client: completes the handshake and sends
    /// <c>human-monitor-command</c> memory reads. QMP is independent of the
    /// gdbstub, so these reads don't pause the guest — which is what lets the
    /// kernel views stay live while the kernel runs.
    /// </summary>
    public sealed class QmpClient : IDisposable
    {
        private readonly string host;
        private readonly int port;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private TcpClient client;
        private StreamReader reader;
        private StreamWriter writer;

        public QmpClient(string host, int port)
        {
            this.host = host;
            this.port = port;
        }

        public bool IsConnected => client != null && client.Connected;

        public async Task ConnectAsync(TimeSpan timeout)
        {
            client = new TcpClient();
            Task connect = client.ConnectAsync(host, port);
            if (await Task.WhenAny(connect, Task.Delay(timeout)).ConfigureAwait(false) != connect)
            {
                Dispose();
                throw new TimeoutException($"QMP connect timeout ({host}:{port})");
            }
            await connect.ConfigureAwait(false);

            NetworkStream stream = client.GetStream();
            reader = new StreamReader(stream, new UTF8Encoding(false));
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            // The server greets first, then expects capabilities negotiation.
            JObject greeting = await ReadMessageAsync(timeout).ConfigureAwait(false);
            if (greeting["QMP"] == null)
            {
                throw new IOException("Unexpected QMP greeting: " + greeting.ToString(Newtonsoft.Json.Formatting.None));
            }
            await ExecuteAsync(new JObject { ["execute"] = "qmp_capabilities" }, timeout).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads <paramref name="length"/> bytes of guest VIRTUAL memory at
        /// <paramref name="address"/> via HMP <c>x</c> (uses the current vCPU's MMU).
        /// </summary>
        public async Task<byte[]> ReadVirtualAsync(ulong address, int length)
        {
            string command = "x /" + length.ToString(CultureInfo.InvariantCulture) + "bx 0x" + address.ToString("x", CultureInfo.InvariantCulture);
            JObject response = await ExecuteAsync(new JObject
            {
                ["execute"] = "human-monitor-command",
                ["arguments"] = new JObject { ["command-line"] = command }
            }, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (response["return"]?.Type != JTokenType.String)
            {
                throw new IOException("x returned non-string: " + response.ToString(Newtonsoft.Json.Formatting.None));
            }
            return ParseMonitorHexDump((string)response["return"], length);
        }

        private async Task<JObject> ExecuteAsync(JObject command, TimeSpan timeout)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (writer == null)
                {
                    throw new IOException("QMP not connected");
                }
                await writer.WriteLineAsync(command.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
                while (true)
                {
                    JObject message = await ReadMessageAsync(timeout).ConfigureAwait(false);
                    // Asynchronous events can arrive between a command and its reply.
                    if (message["event"] != null)
                    {
                        continue;
                    }
                    if (message["error"] is JObject error)
                    {
                        throw new IOException("QMP error: " + ((string)error["desc"] ?? error.ToString(Newtonsoft.Json.Formatting.None)));
                    }
                    return message;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<JObject> ReadMessageAsync(TimeSpan timeout)
        {
            while (true)
            {
                Task<string> read = reader.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(timeout)).ConfigureAwait(false) != read)
                {
                    // A reply that never came leaves the stream out of step; drop the connection.
                    _ = read.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    Dispose();
                    throw new TimeoutException("QMP read timeout");
                }
                string line = await read.ConfigureAwait(false);
                if (line == null)
                {
                    throw new IOException("QMP socket closed");
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    return JObject.Parse(line);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    // Not a message — skip it.
                }
            }
        }

        public void Dispose()
        {
            try { client?.Close(); } catch (SocketException) { } catch (ObjectDisposedException) { }
            client = null;
            reader = null;
            writer = null;
        }

        /// <summary>
        /// HMP <c>x</c> output looks like
        /// <c>ffff800000040020: 0x01 0x00 0x5d 0xc0 …</c>; each line carries the
        /// address then bytes. Takes bytes in order until <paramref name="expected"/>.
        /// </summary>
        public static byte[] ParseMonitorHexDump(string text, int expected)
        {
            var output = new byte[expected];
            int written = 0;
            foreach (string line in text.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }
                foreach (string token in line.Substring(colon + 1).Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                        !byte.TryParse(token.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                    {
                        continue;
                    }
                    if (written >= expected)
                    {
                        return output;
                    }
                    output[written++] = value;
                }
            }
            if (written < expected)
            {
                Array.Resize(ref output, written);
            }
            return output;
        }
    }
}

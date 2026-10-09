using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Cosmos.VisualStudio.Core
{
    public static class PortCheck
    {
        /// <summary>
        /// True if something listens on <paramref name="port"/> on loopback: a
        /// successful connect means busy, a refused or timed-out one means free.
        /// </summary>
        public static async Task<bool> IsPortInUseAsync(int port, string host = "127.0.0.1")
        {
            using (var client = new TcpClient())
            {
                try
                {
                    Task connect = client.ConnectAsync(host, port);
                    Task winner = await Task.WhenAny(connect, Task.Delay(500)).ConfigureAwait(false);
                    if (winner != connect)
                    {
                        // Observe the abandoned connect so its failure isn't unobserved.
                        _ = connect.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        return false;
                    }
                    await connect.ConfigureAwait(false);
                    return true;
                }
                catch (SocketException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Polls until the port accepts connections instead of a fixed sleep.
        /// Gives up when <paramref name="stillWaiting"/> turns false (the process
        /// that should open it exited) or the timeout passes.
        /// </summary>
        public static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, Func<bool> stillWaiting, CancellationToken cancellationToken = default)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                if (await IsPortInUseAsync(port).ConfigureAwait(false))
                {
                    return true;
                }
                if (stillWaiting != null && !stillWaiting())
                {
                    return false;
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            return false;
        }
    }
}

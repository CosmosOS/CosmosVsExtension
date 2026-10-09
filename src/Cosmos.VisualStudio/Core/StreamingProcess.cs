using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>
    /// A child process whose stdout and stderr are streamed as they arrive, in
    /// raw chunks rather than lines, so wrapped build output can be rejoined.
    /// </summary>
    public sealed class StreamingProcess : IDisposable
    {
        private readonly Process process;
        private readonly TaskCompletionSource<int> completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        private StreamingProcess(Process process)
        {
            this.process = process;
        }

        public int Id { get; private set; }

        /// <summary>Exit code, once the process exited and both pipes are drained.</summary>
        public Task<int> Completion => completion.Task;

        public bool HasExited => completion.Task.IsCompleted;

        /// <summary>
        /// Starts <paramref name="psi"/> (built by <see cref="CosmosTools.CreateStartInfo"/>).
        /// stdin is deliberately not redirected: the child inherits Visual Studio's
        /// null stdin, as QEMU dies under a piped stdin when spawned from a GUI process.
        /// </summary>
        public static StreamingProcess Start(ProcessStartInfo psi, Action<string> onOutput)
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var runner = new StreamingProcess(process);
            if (!process.Start())
            {
                throw new CosmosException("Failed to start " + psi.FileName);
            }
            runner.Id = process.Id;

            Task stdout = PumpAsync(process.StandardOutput.BaseStream, onOutput);
            Task stderr = PumpAsync(process.StandardError.BaseStream, onOutput);
            _ = Task.Run(async () =>
            {
#pragma warning disable VSTHRD003 // Stream pumps started just above; they never need the UI thread.
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
#pragma warning restore VSTHRD003
                process.WaitForExit();
                int code;
                try
                {
                    code = process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    code = -1;
                }
                runner.completion.TrySetResult(code);
            });
            return runner;
        }

        private static async Task PumpAsync(Stream stream, Action<string> onOutput)
        {
            Decoder decoder = new UTF8Encoding(false).GetDecoder();
            var bytes = new byte[8192];
            var chars = new char[8192 + 16];
            try
            {
                while (true)
                {
                    int n = await stream.ReadAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }
                    int count = decoder.GetChars(bytes, 0, n, chars, 0);
                    if (count > 0)
                    {
                        onOutput(new string(chars, 0, count));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>Kills the process and everything it started.</summary>
        public void KillTree()
        {
            if (HasExited)
            {
                return;
            }
            try
            {
                if (CosmosTools.IsWindows)
                {
                    // taskkill /T takes the children (QEMU) down with the parent.
                    var psi = new ProcessStartInfo("taskkill", "/T /F /PID " + Id)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (Process kill = Process.Start(psi))
                    {
                        kill?.WaitForExit(5000);
                    }
                }
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            catch (Win32Exception)
            {
                // Access denied or already exiting.
            }
        }

        public void Dispose()
        {
            process.Dispose();
        }
    }
}

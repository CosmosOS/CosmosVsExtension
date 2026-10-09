using System;
using System.Text.RegularExpressions;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>
    /// Cleans raw command output before it reaches an output pane: strips ANSI
    /// escapes and, when joining is on, undoes the hard-wrapping at 80 columns
    /// that build tools apply while keeping legitimate line breaks.
    /// </summary>
    public sealed class LogProcessor
    {
        private static readonly Regex Ansi = new Regex("\\x1b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
        private static readonly Regex Newline = new Regex("[\\r\\n]+", RegexOptions.Compiled);

        private readonly Action<string> sink;
        private readonly bool join;
        private readonly object gate = new object();
        private string buffer = "";

        public LogProcessor(Action<string> sink, bool join = false)
        {
            this.sink = sink;
            this.join = join;
        }

        public static string StripAnsi(string text) => Ansi.Replace(text, "");

        public void Append(string data)
        {
            lock (gate)
            {
                buffer += StripAnsi(data);
                Process();
            }
        }

        public void Flush()
        {
            lock (gate)
            {
                if (buffer.Length > 0)
                {
                    sink(buffer);
                    buffer = "";
                }
            }
        }

        private void Process()
        {
            while (true)
            {
                Match match = Newline.Match(buffer);
                if (!match.Success)
                {
                    return;
                }
                int index = match.Index;
                int next = index + match.Length;

                if (!join)
                {
                    // A trailing \r may be half of a \r\n split across chunks.
                    if (next >= buffer.Length && buffer[buffer.Length - 1] == '\r')
                    {
                        return;
                    }
                    sink(Normalize(buffer.Substring(0, next)));
                    buffer = buffer.Substring(next);
                    continue;
                }

                // A newline at the very end might be a wrap: wait for the next
                // chunk to see how the following line starts.
                if (next >= buffer.Length)
                {
                    return;
                }

                // Cosmos build logs: real log lines are indented with spaces, while
                // hard-wrapped continuations (paths, commands) start at column 0.
                char nextChar = buffer[next];
                bool wrapped = nextChar != ' ' && nextChar != '\r' && nextChar != '\n';
                if (wrapped)
                {
                    sink(buffer.Substring(0, index));
                }
                else
                {
                    sink(Normalize(buffer.Substring(0, next)));
                }
                buffer = buffer.Substring(next);
            }
        }

        // Output panes want one newline per line break: collapse \r\n (and the
        // stray \r progress redraws) so lines aren't doubled.
        private static string Normalize(string chunk) => Regex.Replace(chunk, "\\r\\n?|\\n", "\n");
    }
}

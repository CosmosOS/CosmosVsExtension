using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>
    /// Builds a Windows command line from an argv list, quoting each argument
    /// the way CommandLineToArgvW (and the .NET runtime) parses it back, so the
    /// child sees exactly the entries we pass — embedded quotes included.
    /// </summary>
    public static class CommandLine
    {
        public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return arg;
            }

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    // Backslashes before a quote are escaped, then the quote itself.
                    sb.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }
                backslashes = 0;
                sb.Append(c);
            }
            // Backslashes before the closing quote are escaped too.
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}

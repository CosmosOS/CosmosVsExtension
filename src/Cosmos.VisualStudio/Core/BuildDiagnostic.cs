using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>An error or warning picked out of build output for the Error List.</summary>
    public sealed class BuildDiagnostic : IEquatable<BuildDiagnostic>
    {
        // MSBuild canonical form: path(line[,col[,endLine,endCol]]): error CODE: message [project]
        private static readonly Regex MsBuildWithLocation = new Regex(
            @"^\s*(?<file>[^\s(][^(]*?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // MSBuild form without a location: [origin :] error CODE: message [project]
        private static readonly Regex MsBuildNoLocation = new Regex(
            @"^\s*(?:(?<origin>[^:\[\]]+?)\s*:\s*)?(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // GCC / Clang: path:line:col: error: message (the kernel's C sources)
        private static readonly Regex Gcc = new Regex(
            @"^\s*(?<file>(?:[A-Za-z]:)?[^:]+):(?<line>\d+):(?<col>\d+):\s*(?<sev>error|warning|fatal error):\s*(?<msg>.*?)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public bool IsError { get; private set; }
        public string File { get; private set; }
        /// <summary>1-based, 0 when unknown.</summary>
        public int Line { get; private set; }
        /// <summary>1-based, 0 when unknown.</summary>
        public int Column { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public string Project { get; private set; }

        public static BuildDiagnostic TryParse(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.Length > 4000)
            {
                return null;
            }

            Match m = MsBuildWithLocation.Match(line);
            if (m.Success)
            {
                return new BuildDiagnostic
                {
                    IsError = IsErrorSeverity(m.Groups["sev"].Value),
                    File = m.Groups["file"].Value.Trim(),
                    Line = Int(m.Groups["line"].Value),
                    Column = Int(m.Groups["col"].Value),
                    Code = m.Groups["code"].Value,
                    Message = m.Groups["msg"].Value,
                    Project = NullIfEmpty(m.Groups["proj"].Value)
                };
            }

            m = Gcc.Match(line);
            if (m.Success)
            {
                return new BuildDiagnostic
                {
                    IsError = IsErrorSeverity(m.Groups["sev"].Value),
                    File = m.Groups["file"].Value.Trim(),
                    Line = Int(m.Groups["line"].Value),
                    Column = Int(m.Groups["col"].Value),
                    Code = "",
                    Message = m.Groups["msg"].Value
                };
            }

            m = MsBuildNoLocation.Match(line);
            if (m.Success)
            {
                return new BuildDiagnostic
                {
                    IsError = IsErrorSeverity(m.Groups["sev"].Value),
                    File = null,
                    Code = m.Groups["code"].Value,
                    Message = m.Groups["msg"].Value,
                    Project = NullIfEmpty(m.Groups["proj"].Value)
                };
            }
            return null;
        }

        private static bool IsErrorSeverity(string severity) => !severity.Equals("warning", StringComparison.OrdinalIgnoreCase);

        private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        // MSBuild repeats every diagnostic in its summary; equality lets the
        // caller drop the repeats.
        public bool Equals(BuildDiagnostic other) =>
            other != null && IsError == other.IsError && Line == other.Line && Column == other.Column &&
            string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase) &&
            Code == other.Code && Message == other.Message;

        public override bool Equals(object obj) => Equals(obj as BuildDiagnostic);

        public override int GetHashCode() =>
            ((File ?? "").ToUpperInvariant().GetHashCode() * 31 + Line) * 31 + (Message ?? "").GetHashCode();
    }

    /// <summary>Splits streamed output into whole lines.</summary>
    public sealed class LineSplitter
    {
        private readonly Action<string> onLine;
        private string pending = "";

        public LineSplitter(Action<string> onLine)
        {
            this.onLine = onLine;
        }

        public void Append(string text)
        {
            pending += text;
            int newline;
            while ((newline = pending.IndexOf('\n')) >= 0)
            {
                onLine(pending.Substring(0, newline).TrimEnd('\r'));
                pending = pending.Substring(newline + 1);
            }
        }

        public void Flush()
        {
            if (pending.Length > 0)
            {
                onLine(pending.TrimEnd('\r'));
                pending = "";
            }
        }
    }
}

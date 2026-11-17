using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>A language a kernel can be written in, and the <c>dotnet new</c> template that creates one.</summary>
    public sealed class KernelLanguage
    {
        public static readonly KernelLanguage CSharp = new KernelLanguage("C#", ".csproj", "cosmos-kernel-csharp", "cosmos-kernel");
        public static readonly KernelLanguage VisualBasic = new KernelLanguage("Visual Basic", ".vbproj", "cosmos-kernel-vb", null);

        /// <summary>C# first: the default of a new project, and the project <c>cosmos build</c> takes from a directory that holds both.</summary>
        public static readonly IReadOnlyList<KernelLanguage> All = new[] { CSharp, VisualBasic };

        private KernelLanguage(string name, string projectExtension, string template, string fallbackTemplate)
        {
            Name = name;
            ProjectExtension = projectExtension;
            Template = template;
            FallbackTemplate = fallbackTemplate;
        }

        public string Name { get; }
        public string ProjectExtension { get; }
        public string Template { get; }

        /// <summary>
        /// The template to use when <see cref="Template"/> is not installed, or
        /// null. The templates from before the split by language are a single
        /// C# one, <c>cosmos-kernel</c>.
        /// </summary>
        public string FallbackTemplate { get; }

        /// <summary>The installed template that creates a kernel in this language, or null when none does.</summary>
        public string FindTemplate(ICollection<string> installed) =>
            new[] { Template, FallbackTemplate }.FirstOrDefault(template => template != null && installed.Contains(template));
    }

    /// <summary>The kernel templates of the Cosmos.Build.Templates package.</summary>
    public static class KernelTemplates
    {
        public const string Package = "Cosmos.Build.Templates";

        /// <summary>What <c>dotnet new list</c> is asked for: every kernel template's short name starts with it.</summary>
        public const string ListFilter = "cosmos-kernel";

        // The row of dashes between the table's header and its templates.
        private static readonly Regex HeaderRule = new Regex(@"^-+(\s+-+)+\s*$");

        private static readonly Regex ShortName = new Regex(@"(?<=^|\s)cosmos-kernel(-[a-z]+)?(?=\s|$)");

        /// <summary>
        /// The short names of the kernel templates in the output of
        /// <c>dotnet new list cosmos-kernel</c>. Only the table is read: the
        /// lines above it, and the whole output when nothing is installed,
        /// name the filter itself.
        /// </summary>
        public static ISet<string> Installed(string listOutput)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            bool inTable = false;
            foreach (string line in (listOutput ?? "").Split('\n'))
            {
                if (!inTable)
                {
                    inTable = HeaderRule.IsMatch(line.TrimEnd('\r'));
                    continue;
                }
                foreach (Match match in ShortName.Matches(line))
                {
                    names.Add(match.Value);
                }
            }
            return names;
        }
    }
}

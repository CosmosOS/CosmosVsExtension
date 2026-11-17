using System.Collections.Generic;
using Cosmos.VisualStudio.Core;
using Xunit;

namespace Cosmos.VisualStudio.Tests
{
    public class KernelLanguageTests
    {
        // dotnet new list cosmos-kernel, with the templates of CosmosOS/Cosmos#3311 installed.
        private const string SplitTemplates = @"These templates matched your input: 'cosmos-kernel'

Template Name                         Short Name            Language  Tags                                   
------------------------------------  --------------------  --------  ---------------------------------------
Cosmos Kernel Project (C#)            cosmos-kernel-csharp  [C#]      Cosmos/Kernel/OS Development/Bare Metal
Cosmos Kernel Project (Visual Basic)  cosmos-kernel-vb      VB        Cosmos/Kernel/OS Development/Bare Metal

";

        // The same with Cosmos.Build.Templates 3.0.97, from before the split by language.
        private const string SingleTemplate = @"These templates matched your input: 'cosmos-kernel'

Template Name          Short Name     Language  Tags                                   
---------------------  -------------  --------  ---------------------------------------
Cosmos Kernel Project  cosmos-kernel  [C#]      Cosmos/Kernel/OS Development/Bare Metal

";

        // The same with no Cosmos template installed (exit code 103).
        private const string NoTemplate = @"No templates found matching: 'cosmos-kernel'.

To search for the templates on NuGet.org, run:
   dotnet new search cosmos-kernel


For details on the exit code, refer to https://aka.ms/templating-exit-codes#103
";

        [Fact]
        public void InstalledReadsTheShortNamesOfTheTemplateTable()
        {
            Assert.Equal(new[] { "cosmos-kernel-csharp", "cosmos-kernel-vb" }, Sorted(KernelTemplates.Installed(SplitTemplates)));
            Assert.Equal(new[] { "cosmos-kernel" }, Sorted(KernelTemplates.Installed(SingleTemplate)));
            // Windows line endings, as a console on Windows writes them.
            Assert.Equal(new[] { "cosmos-kernel-csharp", "cosmos-kernel-vb" }, Sorted(KernelTemplates.Installed(SplitTemplates.Replace("\n", "\r\n"))));
        }

        [Fact]
        public void InstalledIsEmptyWhenNothingIsInstalled()
        {
            // The output names the filter twice, but has no table.
            Assert.Empty(KernelTemplates.Installed(NoTemplate));
            Assert.Empty(KernelTemplates.Installed(""));
            Assert.Empty(KernelTemplates.Installed(null));
        }

        [Fact]
        public void EachLanguageFindsItsTemplate()
        {
            ISet<string> split = KernelTemplates.Installed(SplitTemplates);
            Assert.Equal("cosmos-kernel-csharp", KernelLanguage.CSharp.FindTemplate(split));
            Assert.Equal("cosmos-kernel-vb", KernelLanguage.VisualBasic.FindTemplate(split));
        }

        [Fact]
        public void OnlyCSharpFallsBackToTheTemplateFromBeforeTheSplit()
        {
            ISet<string> single = KernelTemplates.Installed(SingleTemplate);
            Assert.Equal("cosmos-kernel", KernelLanguage.CSharp.FindTemplate(single));
            Assert.Null(KernelLanguage.VisualBasic.FindTemplate(single));

            Assert.Null(KernelLanguage.CSharp.FindTemplate(new HashSet<string>()));
        }

        [Fact]
        public void ProjectExtensionsFollowTheLanguages()
        {
            Assert.Equal(new[] { ".csproj", ".vbproj" }, CosmosProject.ProjectExtensions);
            Assert.True(CosmosProject.IsProjectFile(@"C:\src\VbKernel\VbKernel.VBPROJ"));
            Assert.False(CosmosProject.IsProjectFile(@"C:\src\Native\Native.vcxproj"));
        }

        private static List<string> Sorted(IEnumerable<string> names)
        {
            var list = new List<string>(names);
            list.Sort(string.CompareOrdinal);
            return list;
        }
    }
}

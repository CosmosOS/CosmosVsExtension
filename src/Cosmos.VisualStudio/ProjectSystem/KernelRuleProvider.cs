using System;
using System.Collections.Generic;
using System.Linq;
using Cosmos.VisualStudio.Core;
using Microsoft.Build.Framework.XamlTypes;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;

namespace Cosmos.VisualStudio.ProjectSystem
{
    /// <summary>Adds the Cosmos and QEMU pages to the Project Properties of a Cosmos kernel.</summary>
    [ExportRuleObjectProvider("CosmosKernelRuleObjectProvider", PropertyPageContexts.Project)]
    [AppliesTo(KernelPropertyPages.Capability)]
    [Order(0)]
    internal sealed class KernelRuleProvider : IRuleObjectProvider
    {
        // Built once, on first use: the project system expects the same Rule instances every time.
        private readonly Lazy<IReadOnlyCollection<Rule>> rules =
            new Lazy<IReadOnlyCollection<Rule>>(() => KernelPropertyPages.CreateRules().ToList());

        public IReadOnlyCollection<Rule> GetRules() => rules.Value;
    }
}

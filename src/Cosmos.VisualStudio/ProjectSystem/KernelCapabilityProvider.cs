using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Cosmos.VisualStudio.Core;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;

namespace Cosmos.VisualStudio.ProjectSystem
{
    /// <summary>
    /// Gives a project that uses Cosmos.Sdk the <see cref="KernelPropertyPages.Capability"/>
    /// capability, which brings in the Cosmos pages of Project Properties. The
    /// SDK sets <c>UsingCosmosSdk</c> in its props, in every version, so no SDK
    /// change is needed. Re-read on each evaluation.
    /// </summary>
    [Export(ExportContractNames.Scopes.ConfiguredProject, typeof(IProjectCapabilitiesProvider))]
    [SupportsFileExtension(".csproj")]
    [AppliesTo(ProjectCapabilities.AlwaysApplicable)]
    internal sealed class KernelCapabilityProvider : ConfiguredProjectCapabilitiesProviderBase
    {
        private static readonly ImmutableHashSet<string> Kernel = Empty.CapabilitiesSet.Add(KernelPropertyPages.Capability);

        [ImportingConstructor]
        public KernelCapabilityProvider(ConfiguredProject configuredProject)
            : base(nameof(KernelCapabilityProvider), configuredProject)
        {
        }

        protected override async Task<ImmutableHashSet<string>> GetCapabilitiesAsync(CancellationToken cancellationToken)
        {
            try
            {
                IProjectProperties properties = ConfiguredProject.Services.ProjectPropertiesProvider?.GetCommonProperties();
                string usingCosmosSdk = properties == null ? null : await properties.GetEvaluatedPropertyValueAsync("UsingCosmosSdk");
                return string.Equals(usingCosmosSdk, "true", StringComparison.OrdinalIgnoreCase) ? Kernel : Empty.CapabilitiesSet;
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                // A faulted provider would fault the project load: without the
                // capability the project just doesn't get the Cosmos pages.
                return Empty.CapabilitiesSet;
            }
        }
    }
}

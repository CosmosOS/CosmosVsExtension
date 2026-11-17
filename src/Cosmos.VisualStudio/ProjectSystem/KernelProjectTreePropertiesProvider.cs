using System.ComponentModel.Composition;
using Cosmos.VisualStudio.Core;
using Microsoft.VisualStudio.ProjectSystem;

namespace Cosmos.VisualStudio.ProjectSystem
{
    /// <summary>Shows a Cosmos kernel in Solution Explorer with the Cosmos logo rather than its language's project icon.</summary>
    [Export(typeof(IProjectTreePropertiesProvider))]
    [AppliesTo(KernelPropertyPages.Capability)]
    // The higher order runs later: after the managed project system's (10), which sets the language's icon.
    [Order(1000)]
    internal sealed class KernelProjectTreePropertiesProvider : IProjectTreePropertiesProvider
    {
        private static readonly ProjectImageMoniker Icon = new ProjectImageMoniker(PackageGuids.Images, ImageIds.KernelProject);

        public void CalculatePropertyValues(IProjectTreeCustomizablePropertyContext propertyContext, IProjectTreeCustomizablePropertyValues propertyValues)
        {
            if (propertyValues.Flags.Contains(ProjectTreeFlags.Common.ProjectRoot))
            {
                propertyValues.Icon = Icon;
                propertyValues.ExpandedIcon = Icon;
            }
        }
    }
}

using System.ComponentModel.Composition;
using Cosmos.VisualStudio.Core;
using Microsoft.VisualStudio.ProjectSystem;

namespace Cosmos.VisualStudio.ProjectSystem
{
    /// <summary>Shows a Cosmos kernel in Solution Explorer with the Cosmos logo rather than the C# project icon.</summary>
    [Export(typeof(IProjectTreePropertiesProvider))]
    [AppliesTo(KernelPropertyPages.Capability)]
    // The higher order runs later: after the managed project system's (10), which sets the C# icon.
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

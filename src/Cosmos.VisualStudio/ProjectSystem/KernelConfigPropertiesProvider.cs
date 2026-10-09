using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cosmos.VisualStudio.Core;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.Threading;

namespace Cosmos.VisualStudio.ProjectSystem
{
    /// <summary>
    /// The <see cref="KernelPropertyPages.ConfigPersistence"/> data source: the
    /// properties of the Cosmos pages that live in <c>.cosmos/config.json</c>
    /// rather than in the csproj, shared with the VS Code extension. Exported
    /// the way the project system exports its own launchSettings.json source.
    /// </summary>
    [Export(KernelPropertyPages.ConfigPersistence, typeof(IProjectPropertiesProvider))]
    [Export(typeof(IProjectPropertiesProvider))]
    [ExportMetadata("Name", KernelPropertyPages.ConfigPersistence)]
    [AppliesTo(KernelPropertyPages.Capability)]
    internal sealed class KernelConfigPropertiesProvider : IProjectPropertiesProvider
    {
        private readonly UnconfiguredProject project;

        [ImportingConstructor]
        public KernelConfigPropertiesProvider(UnconfiguredProject project)
        {
            this.project = project;
        }

        public string DefaultProjectPath => project.FullPath;

#pragma warning disable CS0067 // Nothing in the project system listens to these for a custom source.
        public event AsyncEventHandler<ProjectPropertyChangedEventArgs> ProjectPropertyChanging;
        public event AsyncEventHandler<ProjectPropertyChangedEventArgs> ProjectPropertyChangedOnWriter;
        public event AsyncEventHandler<ProjectPropertyChangedEventArgs> ProjectPropertyChanged;
#pragma warning restore CS0067

        public IProjectProperties GetCommonProperties() => new KernelConfigProperties(project.FullPath);

        // The settings belong to the project as a whole: there are none per item.
        public IProjectProperties GetItemTypeProperties(string itemType) => GetCommonProperties();

        public IProjectProperties GetItemProperties(string itemType, string item) => item == null ? GetCommonProperties() : null;

        public IProjectProperties GetProperties(string file, string itemType, string item) =>
            string.IsNullOrEmpty(file) || string.Equals(file, project.FullPath, StringComparison.OrdinalIgnoreCase)
                ? GetItemProperties(itemType, item)
                : null;
    }

    internal sealed class KernelConfigProperties : IProjectProperties, IProjectPropertiesContext
    {
        private readonly string projectDir;

        public KernelConfigProperties(string projectPath)
        {
            File = projectPath;
            projectDir = Path.GetDirectoryName(projectPath);
        }

        public IProjectPropertiesContext Context => this;
        public string FileFullPath => File;
        public PropertyKind PropertyKind => PropertyKind.PropertyGroup;

        // IProjectPropertiesContext: the logical home of the settings is the project.
        public bool IsProjectFile => true;
        public string File { get; }
        public string ItemType => null;
        public string ItemName => null;

        public Task<IEnumerable<string>> GetPropertyNamesAsync() =>
            Task.FromResult(KernelConfigSettings.All.Select(s => s.Name));

        public Task<IEnumerable<string>> GetDirectPropertyNamesAsync() =>
            Task.FromResult<IEnumerable<string>>(KernelConfigSettings.All.Where(s => KernelConfigSettings.IsSet(projectDir, s.Name)).Select(s => s.Name).ToList());

        public Task<string> GetEvaluatedPropertyValueAsync(string propertyName) =>
            Task.FromResult(KernelConfigSettings.Get(projectDir, propertyName) ?? "");

        // config.json holds no MSBuild expressions: the unevaluated value is the value.
        public Task<string> GetUnevaluatedPropertyValueAsync(string propertyName) =>
            Task.FromResult(KernelConfigSettings.Get(projectDir, propertyName));

        public Task<bool> IsValueInheritedAsync(string propertyName) =>
            Task.FromResult(!KernelConfigSettings.IsSet(projectDir, propertyName));

        public Task SetPropertyValueAsync(string propertyName, string unevaluatedPropertyValue, IReadOnlyDictionary<string, string> dimensionalConditions = null)
        {
            KernelConfigSettings.Set(projectDir, propertyName, unevaluatedPropertyValue);
            return Task.CompletedTask;
        }

        public Task DeletePropertyAsync(string propertyName, IReadOnlyDictionary<string, string> dimensionalConditions = null)
        {
            KernelConfigSettings.Reset(projectDir, propertyName);
            return Task.CompletedTask;
        }

        public Task DeleteDirectPropertiesAsync()
        {
            foreach (KernelConfigSetting setting in KernelConfigSettings.All)
            {
                KernelConfigSettings.Reset(projectDir, setting.Name);
            }
            return Task.CompletedTask;
        }
    }
}

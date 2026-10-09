using System;
using System.Collections;
using System.ComponentModel.Design;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Cosmos.VisualStudio.Core;
using Cosmos.VisualStudio.Services;
using Cosmos.VisualStudio.UI;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace Cosmos.VisualStudio.ToolWindows
{
    /// <summary>A label / value row of the GC and Memory windows.</summary>
    public sealed class MetricRow
    {
        public MetricRow(string metric, string value, string tooltip = null)
        {
            Metric = metric;
            Value = value;
            Tooltip = tooltip;
        }

        public string Metric { get; }
        public string Value { get; }
        public string Tooltip { get; }
    }

    /// <summary>
    /// A live list fed by one <see cref="LiveModel"/>: a status line on top and
    /// the rows below, repainted whenever the model changes.
    /// </summary>
    internal sealed class LiveListControl : UserControl
    {
        private readonly LiveModel model;
        private readonly Func<IEnumerable> rows;
        private readonly TextBlock status;
        private readonly ListView list;
        private bool repaintQueued;

        public LiveListControl(LiveModel model, Func<IEnumerable> rows, params GridViewColumn[] columns)
        {
            this.model = model;
            this.rows = rows;
            Theme.Surface(this);

            status = Theme.Text("", gray: true);
            status.Margin = new Thickness(8, 6, 8, 6);
            DockPanel.SetDock(status, Dock.Top);

            list = Theme.ListView();
            var view = new GridView();
            foreach (GridViewColumn column in columns)
            {
                view.Columns.Add(column);
            }
            list.View = view;
            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(ToolTipService.ToolTipProperty, new System.Windows.Data.Binding("Tooltip")));
            list.ItemContainerStyle = style;

            var dock = new DockPanel();
            dock.Children.Add(status);
            dock.Children.Add(list);
            Content = dock;

            Loaded += (s, e) =>
            {
                model.Changed += OnChanged;
                Repaint();
            };
            Unloaded += (s, e) => model.Changed -= OnChanged;
        }

        // The model changes on the polling thread; coalesce onto the UI thread.
        private void OnChanged(object sender, EventArgs e)
        {
            if (repaintQueued)
            {
                return;
            }
            repaintQueued = true;
            CosmosPackage.Instance?.JoinableTaskFactory.RunAsync(async () =>
            {
                await CosmosPackage.Instance.JoinableTaskFactory.SwitchToMainThreadAsync();
                repaintQueued = false;
                Repaint();
            }).FileAndForget("Cosmos/LiveView");
        }

        private void Repaint()
        {
            status.Text = model.Message ?? "";
            status.Visibility = string.IsNullOrEmpty(model.Message) ? Visibility.Collapsed : Visibility.Visible;
            object selected = list.SelectedIndex;
            list.ItemsSource = rows()?.Cast<object>().ToList();
            list.SelectedIndex = (int)selected < list.Items.Count ? (int)selected : -1;
        }
    }

    /// <summary>Kernel scheduler threads, read live from the kernel's snapshot buffer.</summary>
    [Guid(PackageGuids.ThreadsWindowString)]
    public sealed class KernelThreadsWindow : ToolWindowPane
    {
        public KernelThreadsWindow() : base(null)
        {
            Caption = "Cosmos Kernel Threads";
            BitmapImageMoniker = KnownMonikers.Thread;
            ToolBar = new CommandID(PackageGuids.CommandSet, PackageIds.ThreadsToolbar);
            KernelThreadsModel model = KernelLiveService.Instance.Threads;
            Content = new LiveListControl(model, () => model.Threads,
                Theme.Column("Slot", nameof(KernelThreadInfo.Slot), 50),
                Theme.Column("ID", nameof(KernelThreadInfo.Id), 60),
                Theme.Column("State", nameof(KernelThreadInfo.State), 90),
                Theme.Column("CPU", nameof(KernelThreadInfo.CpuText), 50),
                Theme.Column("Flags", nameof(KernelThreadInfo.FlagNames), 180));
        }
    }

    /// <summary>Garbage collector statistics.</summary>
    [Guid(PackageGuids.GCWindowString)]
    public sealed class KernelGCWindow : ToolWindowPane
    {
        public KernelGCWindow() : base(null)
        {
            Caption = "Cosmos Kernel GC";
            BitmapImageMoniker = KnownMonikers.Statistics;
            ToolBar = new CommandID(PackageGuids.CommandSet, PackageIds.GCToolbar);
            KernelGCModel model = KernelLiveService.Instance.GC;
            Content = new LiveListControl(model,
                () => model.Stats?.Rows().Select(r => new MetricRow(r.Key, r.Value)),
                Theme.Column("Metric", nameof(MetricRow.Metric), 200),
                Theme.Column("Value", nameof(MetricRow.Value), 160));
        }
    }

    /// <summary>Page allocator state: heap bounds and page usage.</summary>
    [Guid(PackageGuids.MemoryWindowString)]
    public sealed class KernelMemoryWindow : ToolWindowPane
    {
        public KernelMemoryWindow() : base(null)
        {
            Caption = "Cosmos Kernel Memory";
            BitmapImageMoniker = KnownMonikers.Memory;
            ToolBar = new CommandID(PackageGuids.CommandSet, PackageIds.MemoryToolbar);
            KernelMemoryModel model = KernelLiveService.Instance.Memory;
            Content = new LiveListControl(model,
                () => model.Stats?.Rows().Select(r => new MetricRow(r[0], r[1], r[2])),
                Theme.Column("Metric", nameof(MetricRow.Metric), 140),
                Theme.Column("Value", nameof(MetricRow.Value), 220));
        }
    }
}

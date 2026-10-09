using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cosmos.VisualStudio.Core;
using Cosmos.VisualStudio.Services;
using Cosmos.VisualStudio.UI;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace Cosmos.VisualStudio.ToolWindows
{
    /// <summary>
    /// The kernel heap as a colored grid — one cell per page, or per bucket of
    /// pages on large heaps — so fragmentation and per-type layout show at a glance.
    /// </summary>
    [Guid(PackageGuids.MemoryMapWindowString)]
    public sealed class KernelMemoryMapWindow : ToolWindowPane
    {
        public KernelMemoryMapWindow() : base(null)
        {
            Caption = "Cosmos Kernel Memory Map";
            BitmapImageMoniker = KnownMonikers.MemoryWindow;
            Content = new MemoryMapControl(KernelLiveService.Instance.Memory);
        }
    }

    internal sealed class MemoryMapControl : UserControl
    {
        private const int MaxCells = 16000;
        private const int MinCellPx = 6;
        private const uint EmptyCellColor = 0x1a1d20;

        private readonly KernelMemoryModel model;
        private readonly TextBlock message;
        private readonly TextBlock stats;
        private readonly Image image;
        private readonly Border imageBorder;
        private readonly StackPanel legend;
        private readonly StackPanel snapshotPanel;
        private readonly Popup tooltip;
        private readonly TextBlock tooltipText;
        private bool repaintQueued;

        // Layout of the last paint, for hit-testing the tooltip.
        private Dictionary<byte, int>[] cellCounts;
        private int cols, rows, cellPx, pagesPerCell, totalPages;

        public MemoryMapControl(KernelMemoryModel model)
        {
            this.model = model;
            Theme.Surface(this);

            message = Theme.Text("", gray: true);
            message.FontStyle = FontStyles.Italic;
            message.Margin = new Thickness(4, 12, 4, 12);

            stats = Theme.Text("", gray: true, size: 11);
            stats.Margin = new Thickness(0, 0, 0, 6);

            image = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Cross };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            image.MouseMove += OnMouseMove;
            image.MouseLeave += (s, e) => tooltip.IsOpen = false;
            imageBorder = new Border { Child = image, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
            imageBorder.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);

            legend = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            snapshotPanel = new StackPanel();
            snapshotPanel.Children.Add(stats);
            snapshotPanel.Children.Add(imageBorder);
            snapshotPanel.Children.Add(legend);

            var root = new StackPanel { Margin = new Thickness(8) };
            root.Children.Add(message);
            root.Children.Add(snapshotPanel);
            Content = new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            tooltipText = new TextBlock { FontSize = 11, Margin = new Thickness(6, 4, 6, 4) };
            tooltipText.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolTipTextBrushKey);
            var tooltipBorder = new Border { Child = tooltipText, BorderThickness = new Thickness(1) };
            tooltipBorder.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolTipBrushKey);
            tooltipBorder.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolTipBorderBrushKey);
            tooltip = new Popup
            {
                Child = tooltipBorder,
                PlacementTarget = image,
                Placement = PlacementMode.Relative,
                AllowsTransparency = true,
                IsHitTestVisible = false
            };

            Loaded += (s, e) =>
            {
                model.Changed += OnChanged;
                Repaint();
            };
            Unloaded += (s, e) =>
            {
                model.Changed -= OnChanged;
                tooltip.IsOpen = false;
            };
            SizeChanged += (s, e) =>
            {
                if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 1)
                {
                    Repaint();
                }
            };
        }

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
            }).FileAndForget("Cosmos/MemoryMap");
        }

        private void Repaint()
        {
            MemoryStats s = model.Stats;
            if (s == null || s.TotalPageCount == 0)
            {
                ShowMessage(s == null ? model.Message ?? "Waiting for memory snapshot…" : "Memory snapshot not initialized yet.");
                return;
            }
            message.Visibility = Visibility.Collapsed;
            snapshotPanel.Visibility = Visibility.Visible;

            totalPages = (int)Math.Min(s.TotalPageCount, int.MaxValue);
            BuildGrid(model.Extents);
            Paint();
            RenderStats(s);
            RenderLegend(s);
        }

        private void ShowMessage(string text)
        {
            message.Text = text;
            message.Visibility = Visibility.Visible;
            snapshotPanel.Visibility = Visibility.Collapsed;
            tooltip.IsOpen = false;
            cellCounts = null;
        }

        // Picks pagesPerCell as the smallest power of two that keeps the grid
        // under MaxCells, then sizes the cells to fill the width.
        private void BuildGrid(IReadOnlyList<PageExtent> extents)
        {
            double width = Math.Max(64, ActualWidth - 16 - 2);
            pagesPerCell = 1;
            while ((totalPages + pagesPerCell - 1) / pagesPerCell > MaxCells)
            {
                pagesPerCell *= 2;
            }
            int cells = (totalPages + pagesPerCell - 1) / pagesPerCell;
            cols = Math.Max(8, (int)(width / MinCellPx));
            if (cols > cells)
            {
                cols = cells;
            }
            rows = (cells + cols - 1) / cols;
            cellPx = Math.Max(2, (int)(width / cols));

            cellCounts = new Dictionary<byte, int>[cols * rows];
            if (extents == null)
            {
                return;
            }
            foreach (PageExtent extent in extents)
            {
                int end = extent.Start + extent.Length;
                int p = extent.Start;
                while (p < end)
                {
                    int cell = p / pagesPerCell;
                    int cellEnd = Math.Min(end, (cell + 1) * pagesPerCell);
                    if (cell >= cellCounts.Length)
                    {
                        break;
                    }
                    Dictionary<byte, int> counts = cellCounts[cell] ?? (cellCounts[cell] = new Dictionary<byte, int>());
                    counts.TryGetValue(extent.Type, out int existing);
                    counts[extent.Type] = existing + (cellEnd - p);
                    p = cellEnd;
                }
            }
        }

        private void Paint()
        {
            int width = cols * cellPx;
            int height = rows * cellPx;
            if (width <= 0 || height <= 0)
            {
                return;
            }
            var pixels = new int[width * height];
            for (int cell = 0; cell < cellCounts.Length; cell++)
            {
                Dictionary<byte, int> counts = cellCounts[cell];
                uint color = EmptyCellColor;
                if (counts != null)
                {
                    // The dominant type colors the cell.
                    byte type = counts.OrderByDescending(kv => kv.Value).First().Key;
                    color = PageTypes.Find(type)?.Color ?? PageTypes.UnknownColor;
                }
                int argb = unchecked((int)(0xff000000 | color));
                int x0 = (cell % cols) * cellPx;
                int y0 = (cell / cols) * cellPx;
                for (int y = y0; y < y0 + cellPx; y++)
                {
                    int rowStart = y * width;
                    for (int x = x0; x < x0 + cellPx; x++)
                    {
                        pixels[rowStart + x] = argb;
                    }
                }
            }

            if (!(image.Source is WriteableBitmap bitmap) || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
            {
                bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                image.Source = bitmap;
            }
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        }

        private void RenderStats(MemoryStats s)
        {
            ulong used = s.UsedPageCount;
            string text =
                $"{s.TotalPageCount} pages × {s.PageSize} B = {Format.Bytes(s.PagesAsBytes(s.TotalPageCount))}  |  " +
                $"used {used} ({Format.Bytes(s.PagesAsBytes(used))})  |  free {s.FreePageCount} ({Format.Bytes(s.PagesAsBytes(s.FreePageCount))})\n" +
                $"cell = {pagesPerCell} page{(pagesPerCell == 1 ? "" : "s")}, grid {cols} × {rows}";
            if (model.ExtentsError != null)
            {
                text += "\n" + model.ExtentsError;
            }
            stats.Text = text;
        }

        private void RenderLegend(MemoryStats s)
        {
            legend.Children.Clear();
            IReadOnlyDictionary<byte, ulong> counts = s.TypeCounts;
            double total = Math.Max(1, s.TotalPageCount);
            // Largest first; types with no pages are left out.
            var entries = PageTypes.All
                .Select((info, index) => new { info, index, count = counts.TryGetValue(info.Id, out ulong c) ? c : 0 })
                .Where(e => e.count > 0)
                .OrderByDescending(e => e.count)
                .ThenBy(e => e.index)
                .ToList();
            if (entries.Count == 0)
            {
                legend.Children.Add(Theme.Text("(no pages yet)", gray: true, size: 11));
                return;
            }
            foreach (var entry in entries)
            {
                double pct = entry.count * 100 / total;
                string pctText = pct >= 10 ? pct.ToString("0", CultureInfo.InvariantCulture) + "%" : pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), ToolTip = $"{entry.info.Name}: {entry.count} pages ({pct.ToString("0.00", CultureInfo.InvariantCulture)}%)" };
                var swatch = new Border
                {
                    Width = 10,
                    Height = 10,
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Theme.ColorFromRgb(entry.info.Color)),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                swatch.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
                DockPanel.SetDock(swatch, Dock.Left);
                row.Children.Add(swatch);
                TextBlock count = Theme.Text(entry.count.ToString("N0", CultureInfo.CurrentCulture) + "  " + pctText, gray: true, size: 11, wrap: false);
                DockPanel.SetDock(count, Dock.Right);
                row.Children.Add(count);
                row.Children.Add(Theme.Text(entry.info.Name, size: 11, wrap: false));
                legend.Children.Add(row);
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (cellCounts == null || cellPx == 0)
            {
                tooltip.IsOpen = false;
                return;
            }
            Point p = e.GetPosition(image);
            int c = (int)(p.X / cellPx);
            int r = (int)(p.Y / cellPx);
            if (c < 0 || c >= cols || r < 0 || r >= rows)
            {
                tooltip.IsOpen = false;
                return;
            }
            int index = r * cols + c;
            int pageStart = index * pagesPerCell;
            int pageEnd = Math.Min(pageStart + pagesPerCell, totalPages);
            var lines = new List<string> { $"Pages [{pageStart}..{pageEnd})" };
            Dictionary<byte, int> counts = cellCounts[index];
            if (counts == null)
            {
                lines.Add("(no data)");
            }
            else
            {
                lines.AddRange(counts.OrderByDescending(kv => kv.Value).Select(kv => $"{PageTypes.NameOf(kv.Key)}: {kv.Value}"));
            }
            tooltipText.Text = string.Join("\n", lines);
            tooltip.HorizontalOffset = p.X + 14;
            tooltip.VerticalOffset = p.Y + 14;
            tooltip.IsOpen = true;
        }
    }
}

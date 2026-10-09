using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace Cosmos.VisualStudio.UI
{
    /// <summary>
    /// Builds tool-window content that follows the Visual Studio theme: colors
    /// come from the environment's resource keys and the standard controls pick
    /// up the themed dialog styles.
    /// </summary>
    internal static class Theme
    {
        /// <summary>Makes <paramref name="root"/> a themed tool-window surface.</summary>
        public static T Surface<T>(T root) where T : FrameworkElement
        {
            ThemedDialogStyleLoader.SetUseDefaultThemedDialogStyles(root, true);
            root.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            root.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            root.SetResourceReference(TextElement.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            // Lets CrispImage icons invert for dark backgrounds.
            root.SetResourceReference(ImageThemingUtilities.ImageBackgroundColorProperty, EnvironmentColors.ToolWindowBackgroundColorKey);
            return root;
        }

        public static TextBlock Text(string text, bool gray = false, double? size = null, bool bold = false, bool wrap = true)
        {
            var block = new TextBlock
            {
                Text = text,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
            };
            block.SetResourceReference(TextBlock.ForegroundProperty,
                gray ? EnvironmentColors.SystemGrayTextBrushKey : EnvironmentColors.ToolWindowTextBrushKey);
            if (size != null)
            {
                block.FontSize = size.Value;
            }
            if (bold)
            {
                block.FontWeight = FontWeights.SemiBold;
            }
            return block;
        }

        /// <summary>Uppercase gray caption with a separator line, like a VS Code side-bar section.</summary>
        public static FrameworkElement SectionHeader(string text, UIElement trailing = null)
        {
            var grid = new Grid { Margin = new Thickness(0, 14, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock caption = Text(text.ToUpperInvariant(), gray: true, size: 11, bold: true, wrap: false);
            caption.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(caption);
            if (trailing != null)
            {
                Grid.SetColumn(trailing, 1);
                grid.Children.Add(trailing);
            }
            var border = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 4), Child = grid };
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return border;
        }

        public static CrispImage Icon(ImageMoniker moniker, double size = 16)
        {
            return new CrispImage { Moniker = moniker, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        }

        public static Button Button(string text, Action onClick, ImageMoniker? icon = null)
        {
            object content = text;
            if (icon != null)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                CrispImage image = Icon(icon.Value);
                image.Margin = new Thickness(0, 0, 6, 0);
                panel.Children.Add(image);
                panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
                content = panel;
            }
            var button = new Button
            {
                Content = content,
                Padding = new Thickness(10, 3, 10, 3),
                MinWidth = 75,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            button.Click += (s, e) => onClick();
            return button;
        }

        private static ControlTemplate flatTemplate;

        /// <summary>
        /// A borderless, full-width clickable row (icon, label, gray detail) that
        /// highlights on hover the way tree rows do.
        /// </summary>
        public static Button ActionRow(ImageMoniker icon, string label, string detail, Action onClick)
        {
            var panel = new DockPanel { LastChildFill = true };
            CrispImage image = Icon(icon);
            image.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(image, Dock.Left);
            panel.Children.Add(image);
            TextBlock text = Text(label, wrap: false);
            text.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(text, Dock.Left);
            panel.Children.Add(text);
            if (!string.IsNullOrEmpty(detail))
            {
                TextBlock description = Text(detail, gray: true, wrap: false);
                description.Margin = new Thickness(8, 0, 0, 0);
                description.VerticalAlignment = VerticalAlignment.Center;
                panel.Children.Add(description);
            }

            var button = new Button
            {
                Content = panel,
                Template = FlatTemplate(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                ToolTip = string.IsNullOrEmpty(detail) ? label : detail
            };
            button.Click += (s, e) => onClick();
            return button;
        }

        private static ControlTemplate FlatTemplate()
        {
            if (flatTemplate != null)
            {
                return flatTemplate;
            }
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "Bd");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.PaddingProperty, new Thickness(6, 3, 6, 3));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            border.AppendChild(presenter);
            template.VisualTree = border;

            // Setters take the DynamicResourceExtension itself; WPF resolves it per instance.
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.CommandBarMouseOverBackgroundBeginBrushKey), "Bd"));
            template.Triggers.Add(hover);
            var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(TreeViewColors.SelectedItemInactiveBrushKey), "Bd"));
            template.Triggers.Add(focus);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);

            template.Seal();
            flatTemplate = template;
            return template;
        }

        /// <summary>Label above a control, with an optional gray hint below.</summary>
        public static FrameworkElement Field(string label, UIElement control, string hint = null)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            TextBlock caption = Text(label, bold: true);
            caption.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(caption);
            panel.Children.Add(control);
            if (!string.IsNullOrEmpty(hint))
            {
                TextBlock hintText = Text(hint, gray: true, size: 11.5);
                hintText.Margin = new Thickness(0, 4, 0, 0);
                panel.Children.Add(hintText);
            }
            return panel;
        }

        /// <summary>A ListView styled like the debugger's tool windows.</summary>
        public static ListView ListView()
        {
            var list = new ListView
            {
                BorderThickness = new Thickness(0),
                SelectionMode = SelectionMode.Extended
            };
            list.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            list.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            return list;
        }

        public static GridViewColumn Column(string header, string bindingPath, double width)
        {
            return new GridViewColumn
            {
                Header = header,
                Width = width,
                DisplayMemberBinding = new System.Windows.Data.Binding(bindingPath)
            };
        }

        public static Color ColorFromRgb(uint rgb) =>
            Color.FromRgb((byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));
    }
}

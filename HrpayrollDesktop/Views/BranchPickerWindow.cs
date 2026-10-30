using HrpayrollDesktop.Sync;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HrpayrollDesktop;

/// <summary>
/// Shown at Back Office sign-in when the account is not tied to one branch and there is more than one:
/// the officer says which branch's employees this terminal is serving.
/// </summary>
public sealed class BranchPickerWindow : Window
{
    private readonly ListBox _list;

    public BranchOption? Selected { get; private set; }

    public BranchPickerWindow(IReadOnlyList<BranchOption> branches)
    {
        Title = "Choose branch";
        Width = 380;
        Height = 440;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        _list = new ListBox
        {
            ItemsSource = branches,
            DisplayMemberPath = nameof(BranchOption.Name),
            FontSize = 14,
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#E6EDEF")!,
            Margin = new Thickness(0, 12, 0, 16),
            SelectedIndex = branches.Count > 0 ? 0 : -1,
        };
        _list.MouseDoubleClick += (_, _) => Confirm();

        var ok = new Button { Content = "Continue", Padding = new Thickness(22, 8, 22, 8), IsDefault = true };
        ok.Click += (_, _) => Confirm();

        var cancel = new Button { Content = "Cancel", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Which branch is this terminal serving?", FontSize = 16, FontWeight = FontWeights.Bold });
        header.Children.Add(new TextBlock
        {
            Text = "You will see that branch's employees and damages.",
            FontSize = 12,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#7C8B8A")!,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(buttons);
        root.Children.Add(_list);

        Content = root;
    }

    private void Confirm()
    {
        if (_list.SelectedItem is not BranchOption chosen) return;
        Selected = chosen;
        DialogResult = true;
    }
}

using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CoverageInsight.Models;
using CoverageInsight.ViewModels;

namespace CoverageInsight;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (!string.IsNullOrEmpty(App.StartupFile) && File.Exists(App.StartupFile))
                Vm.Load(App.StartupFile!);
        };
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var file = TryGetDroppedFile(e);
        if (file is not null)
            Vm.Load(file);
    }

    private static string? TryGetDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;

        return (e.Data.GetData(DataFormats.FileDrop) as string[])?
            .FirstOrDefault(f => f.EndsWith(".xml", System.StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".coveragexml", System.StringComparison.OrdinalIgnoreCase));
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is CoverageNode node)
            Vm.Selected = node;
    }

    private void OnHotspotSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Hotspots.SelectedItem is CoverageNode node)
            Vm.Selected = node;
    }

    private void OnRowActivated(object sender, MouseButtonEventArgs e)
    {
        if (Vm.OpenInIdeCommand.CanExecute(null))
            Vm.OpenInIdeCommand.Execute(null);
    }

    private void OnRowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (Vm.OpenInIdeCommand.CanExecute(null))
        {
            Vm.OpenInIdeCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            Vm.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>Print &amp; PDF (see <see cref="PrintViewModel"/>).</summary>
public partial class PrintWindow : Window
{
    private readonly PrintViewModel _vm;

    public PrintWindow(PrintViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        Closing += (_, e) =>
        {
            if (_vm.IsBusy && !Dialogs.Confirm("Stop making PDFs?", "PDFs being made now are cancelled.", "Stop"))
                e.Cancel = true;
            else if (_vm.IsBusy)
                _vm.CancelCommand.Execute(null);
        };
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for the PDFs", InitialDirectory = System.IO.Directory.Exists(_vm.Folder) ? _vm.Folder : "" };
        if (dialog.ShowDialog(this) == true) _vm.Folder = dialog.FolderName;
    }

    private void OnOpenPreview(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Selected?.PdfPath is { } pdf && System.IO.File.Exists(pdf))
            Process.Start(new ProcessStartInfo(pdf) { UseShellExecute = true });
    }

    /// <summary>The window is not a dialog (it stays open beside the hub), so IsCancel alone does not close it.</summary>
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

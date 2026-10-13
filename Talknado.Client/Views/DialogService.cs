using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Talknado.Client.ViewModels;

namespace Talknado.Client.Views;

public sealed class DialogService : IDialogService
{
    private static Window? Owner =>
        Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);

    public string? PickFileToAttach()
    {
        var dialog = new OpenFileDialog { CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    // Скачанное помечено «из интернета», поэтому исполняемый файл Windows
    // запустит только после предупреждения SmartScreen.
    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* нет программы для этого типа или запуск отменён */ }
    }

    public void ShowInFolder(string path)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { /* ignore */ }
    }
}

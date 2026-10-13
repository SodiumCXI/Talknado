namespace Talknado.Client.ViewModels;

public interface IDialogService
{
    string? PickFileToAttach();
    void OpenFile(string path);
    void ShowInFolder(string path);
}

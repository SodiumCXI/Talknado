using CommunityToolkit.Mvvm.ComponentModel;

namespace Talknado.Client.Models;

public interface IMediaViewerManager
{
    Attachment? Current { get; }
    bool IsWindowVisible { get; set; }
    void Open(Attachment attachment);
}

public partial class MediaViewerManager(IFileTransferManager fileTransferManager) : ObservableObject, IMediaViewerManager
{
    private readonly IFileTransferManager _fileTransferManager = fileTransferManager;

    [ObservableProperty]
    private Attachment? _current;

    [ObservableProperty]
    private bool _isWindowVisible;

    public void Open(Attachment attachment)
    {
        // Видео не качается целиком: плеер читает его по мере просмотра.
        if (attachment.IsImage && attachment.State == AttachmentState.Remote)
            _fileTransferManager.Download(attachment);

        // Окно уже открыто с другим вложением — переоткрываем, иначе
        // IsWindowVisible не изменится и окно не узнает о новом вложении.
        if (IsWindowVisible)
            IsWindowVisible = false;

        Current = attachment;
        IsWindowVisible = true;
    }
}

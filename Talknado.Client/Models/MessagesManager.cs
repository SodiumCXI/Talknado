using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;

namespace Talknado.Client.Models;

public interface IMessagesManager
{
    void AddMessage(ushort userId, string message, Attachment? attachment = null);
    ObservableCollection<MessagesManager.Message> Messages { get; }
    /// <summary>Сообщение от другого участника; из потока UI.</summary>
    event Action? MessageReceived;
}
public partial class MessagesManager(IUsersInfo usersInfo, IConnectionInfo connectionInfo) : ObservableObject, IMessagesManager
{
    private readonly IUsersInfo _usersInfo = usersInfo;
    private readonly IConnectionInfo _connectionInfo = connectionInfo;

    public event Action? MessageReceived;

    [ObservableProperty]
    private ObservableCollection<Message> _messages = [];

    public void AddMessage(ushort userId, string message, Attachment? attachment = null)
    {
        if (string.IsNullOrWhiteSpace(message) && attachment == null)
        {
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            string timestamp = DateTime.Now.ToString("HH:mm");

            // Вложение всегда начинает свою карточку, и текст к карточке
            // с вложением не дописывается: иначе он читался бы как подпись.
            if (attachment == null && Messages.Count > 0 && Messages[^1].UserId == userId
                && Messages[^1].Timestamp == timestamp && Messages[^1].Attachment == null)
            {
                Messages[^1].Text += "\n" + message;
            }
            else
            {
                var username = _usersInfo.GetUsernameByUserId(userId);
                Messages.Add(new(userId, username, message, timestamp, attachment));
            }

            if (userId != _connectionInfo.LocalUserId)
                MessageReceived?.Invoke();
        });
    }

    public partial class Message : ObservableObject
    {
        public ushort UserId { get; }
        public string Username { get; }
        public string Timestamp { get; }
        public Attachment? Attachment { get; }
        public bool HasAttachment => Attachment != null;

        [ObservableProperty]
        private string _text;

        public Message(ushort userId, string username, string text, string timestamp, Attachment? attachment = null)
        {
            UserId = userId;
            Username = username;
            Timestamp = timestamp;
            Text = text;
            Attachment = attachment;
        }
    }
}

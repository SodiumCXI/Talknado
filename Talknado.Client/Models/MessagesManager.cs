using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace Talknado.Client.Models
{
    public interface IMessagesManager
    {
        void AddMessage(ushort userId, string message);
        ObservableCollection<MessagesManager.Message> Messages { get; }
    }
    public partial class MessagesManager(IUsersInfo usersInfo) : ObservableObject, IMessagesManager
    {
        private readonly IUsersInfo _usersInfo = usersInfo;

        [ObservableProperty]
        private ObservableCollection<Message> _messages = [];

        public void AddMessage(ushort userId, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm");

            if (Messages.Count > 0 && Messages[^1].UserId == userId && Messages[^1].Timestamp == timestamp)
            {
                Messages[^1].Text += "\n" + message;
            }
            else
            {
                var username = _usersInfo.GetUsernameByUserId(userId);
                Messages.Add(new(userId, username, message, DateTime.Now.ToString("HH:mm")));
            }
        }

        public partial class Message : ObservableObject
        {
            public ushort UserId { get; }
            public string Username { get; }
            public string Timestamp { get; }

            [ObservableProperty]
            private string _text;

            public Message(ushort userId, string username, string text, string timestamp)
            {
                UserId = userId;
                Username = username;
                Timestamp = timestamp;
                Text = text;
            }
        }
    }
}

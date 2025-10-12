using System.Windows;
using Talknado.Client.Views;

namespace Talknado.Client
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var window = new StartWindow();
            window.Show();
        }
    }
}
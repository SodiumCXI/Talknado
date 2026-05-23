using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Talknado.Client.Models.Helpers.Audio;
using Talknado.Client.Properties;
using Talknado.Client.Views;

namespace Talknado.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var lang = Settings.Default.Language;
        if (string.IsNullOrEmpty(lang))
        {
            var systemLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (systemLang == "ru")
                lang = "ru";
            else if (systemLang == "zh")
                lang = "zh";
            else
                lang = "en";
            Settings.Default.Language = lang;
            Settings.Default.Save();
        }

        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var culture = new CultureInfo(lang);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        var thread = new Thread(() =>
        {
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            var window = new StartWindow();
            window.Show();
            try
            {
                Dispatcher.Run();
            }
            catch { /* ignore */ }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LoopbackAudioCapture.Dispose();

        base.OnExit(e);
    }
}
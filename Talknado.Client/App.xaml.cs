using System.Windows;
using Talknado.Client.Views;
using Talknado.Client.Properties;

namespace Talknado.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var lang = Settings.Default.Language;

        if (string.IsNullOrEmpty(lang))
        {
            var systemLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

            if (systemLang == "ru")
                lang = "ru";
            else if (systemLang == "zh")
                lang = "zh";
            else
                lang = "en";

            Settings.Default.Language = lang;
            Settings.Default.Save();
        }

        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(lang);
        Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(lang);

        base.OnStartup(e);

        var window = new StartWindow();
        window.Show();
    }
}
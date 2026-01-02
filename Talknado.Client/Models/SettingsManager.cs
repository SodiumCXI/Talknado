using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.Wave;
using System.Collections.ObjectModel;
using Talknado.Client.Properties;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.Models;

public interface ISettingsManager
{
    bool ShareScreenWithAudio { get; set; }
    bool AutoOpenScreenShareWindow { get; set; }
    string? SelectedInputDevice { get; set; }
    string? SelectedOutputDevice { get; set; }
    ObservableCollection<string> InputDevices { get; }
    ObservableCollection<string> OutputDevices { get; }
    bool IsWindowVisible { get; set; }

    event Action? InputDeviceChanged;
    event Action? OutputDeviceChanged;
}

public partial class SettingsManager : ObservableObject, ISettingsManager
{
    [ObservableProperty]
    private bool _shareScreenWithAudio;

    [ObservableProperty]
    private bool _autoOpenScreenShareWindow;

    [ObservableProperty]
    private string? _selectedInputDevice;

    [ObservableProperty]
    private string? _selectedOutputDevice;

    public ObservableCollection<string> InputDevices { get; set; } = [];
    public ObservableCollection<string> OutputDevices { get; set; } = [];

    [ObservableProperty]
    private bool _isWindowVisible = false;

    public event Action? InputDeviceChanged;
    public event Action? OutputDeviceChanged;

    public SettingsManager()
    {
        LoadSettings();
        LoadAudioDevices();
    }

    private void LoadSettings()
    {
        ShareScreenWithAudio = Settings.Default.ShareScreenWithAudio;
        AutoOpenScreenShareWindow = Settings.Default.AutoOpenScreenShareWindow;
    }

    private void LoadAudioDevices()
    {
        OutputDevices.Add(Strings.DefaultDeviceText);
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            OutputDevices.Add(caps.ProductName);
        }

        var savedSpeaker = Settings.Default.SelectedOutputDevice;
        SelectedOutputDevice = !string.IsNullOrEmpty(savedSpeaker) && OutputDevices.Contains(savedSpeaker)
            ? savedSpeaker
            : Strings.DefaultDeviceText;

        InputDevices.Add(Strings.DefaultDeviceText);
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            InputDevices.Add(caps.ProductName);
        }

        var savedMicrophone = Settings.Default.SelectedInputDevice;
        SelectedInputDevice = !string.IsNullOrEmpty(savedMicrophone) && InputDevices.Contains(savedMicrophone)
            ? savedMicrophone
            : Strings.DefaultDeviceText;
    }

    partial void OnShareScreenWithAudioChanged(bool value)
    {
        Settings.Default.ShareScreenWithAudio = value;
        Settings.Default.Save();
    }

    partial void OnAutoOpenScreenShareWindowChanged(bool value)
    {
        Settings.Default.AutoOpenScreenShareWindow = value;
        Settings.Default.Save();
    }

    partial void OnSelectedInputDeviceChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Settings.Default.SelectedInputDevice = value;
            Settings.Default.Save();

            InputDeviceChanged?.Invoke();
        }
    }

    partial void OnSelectedOutputDeviceChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Settings.Default.SelectedOutputDevice = value;
            Settings.Default.Save();

            OutputDeviceChanged?.Invoke();
        }
    }
}
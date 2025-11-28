using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.Wave;
using System.Collections.ObjectModel;

namespace Talknado.Client.Models;

public interface ISettingsManager
{
    bool ScreenShareWithAudio { get; set; }
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
    private bool _screenShareWithAudio;

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
        ScreenShareWithAudio = Properties.Settings.Default.ScreenShareWithAudio;
        AutoOpenScreenShareWindow = Properties.Settings.Default.AutoOpenScreenShareWindow;
    }

    private void LoadAudioDevices()
    {
        OutputDevices.Add("Устройство по умолчанию");
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            OutputDevices.Add(caps.ProductName);
        }

        var savedSpeaker = Properties.Settings.Default.SelectedOutputDevice;
        SelectedOutputDevice = !string.IsNullOrEmpty(savedSpeaker) && OutputDevices.Contains(savedSpeaker)
            ? savedSpeaker
            : "Устройство по умолчанию";

        InputDevices.Add("Устройство по умолчанию");
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            InputDevices.Add(caps.ProductName);
        }

        var savedMicrophone = Properties.Settings.Default.SelectedInputDevice;
        SelectedInputDevice = !string.IsNullOrEmpty(savedMicrophone) && InputDevices.Contains(savedMicrophone)
            ? savedMicrophone
            : "Устройство по умолчанию";
    }

    partial void OnScreenShareWithAudioChanged(bool value)
    {
        Properties.Settings.Default.ScreenShareWithAudio = value;
        Properties.Settings.Default.Save();
    }

    partial void OnAutoOpenScreenShareWindowChanged(bool value)
    {
        Properties.Settings.Default.AutoOpenScreenShareWindow = value;
        Properties.Settings.Default.Save();
    }

    partial void OnSelectedInputDeviceChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Properties.Settings.Default.SelectedInputDevice = value;
            Properties.Settings.Default.Save();

            InputDeviceChanged?.Invoke();
        }
    }

    partial void OnSelectedOutputDeviceChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Properties.Settings.Default.SelectedOutputDevice = value;
            Properties.Settings.Default.Save();

            OutputDeviceChanged?.Invoke();
        }
    }
}
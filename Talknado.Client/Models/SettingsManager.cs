using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
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
    void LoadAudioDevices();
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

    private bool _isLoadingDevices = false;

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

    public void LoadAudioDevices()
    {
        _isLoadingDevices = true;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            OutputDevices.Clear();
            OutputDevices.Add(Strings.DefaultDeviceText);

            var outputDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int i = 0; i < outputDevices.Count; i++)
            {
                try
                {
                    OutputDevices.Add(outputDevices[i].FriendlyName);
                }
                catch { }
            }

            var savedOutput = Settings.Default.SelectedOutputDevice;
            SelectedOutputDevice = !string.IsNullOrEmpty(savedOutput) && OutputDevices.Contains(savedOutput)
                ? savedOutput
                : Strings.DefaultDeviceText;

            InputDevices.Clear();
            InputDevices.Add(Strings.DefaultDeviceText);

            var inputDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            for (int i = 0; i < inputDevices.Count; i++)
            {
                try
                {
                    InputDevices.Add(inputDevices[i].FriendlyName);
                }
                catch { }
            }

            var savedInput = Settings.Default.SelectedInputDevice;
            SelectedInputDevice = !string.IsNullOrEmpty(savedInput) && InputDevices.Contains(savedInput)
                ? savedInput
                : Strings.DefaultDeviceText;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading devices: {ex.Message}");

            if (OutputDevices.Count == 0) OutputDevices.Add(Strings.DefaultDeviceText);
            if (InputDevices.Count == 0) InputDevices.Add(Strings.DefaultDeviceText);
            if (string.IsNullOrEmpty(SelectedOutputDevice)) SelectedOutputDevice = Strings.DefaultDeviceText;
            if (string.IsNullOrEmpty(SelectedInputDevice)) SelectedInputDevice = Strings.DefaultDeviceText;
        }
        finally
        {
            _isLoadingDevices = false;
        }
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
        if (!string.IsNullOrEmpty(value) && !_isLoadingDevices)
        {
            Settings.Default.SelectedInputDevice = value;
            Settings.Default.Save();
            InputDeviceChanged?.Invoke();
        }
    }

    partial void OnSelectedOutputDeviceChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value) && !_isLoadingDevices)
        {
            Settings.Default.SelectedOutputDevice = value;
            Settings.Default.Save();
            OutputDeviceChanged?.Invoke();
        }
    }
}
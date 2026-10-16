using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Talknado.Client.Models;
using Talknado.Client.Properties.Localization;

namespace Talknado.Client.ViewModels;

public partial class SettingsWindowViewModel(ISettingsManager settingsManager) : ObservableObject
{
    private const int MiddleMouseButton = 0x04;
    private const int MouseButton4 = 0x05;
    private const int MouseButton5 = 0x06;

    private readonly ISettingsManager _settingsManager = settingsManager;
    public ISettingsManager SettingsManager => _settingsManager;

    /// <summary>Ждём нажатия, которое станет клавишей рации.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowKeyCap), nameof(PushToTalkHint))]
    private bool _isCapturingKey;

    public string PushToTalkKeyText => KeyName(_settingsManager.PushToTalkKey);

    /// <summary>Плашка с клавишей; вместо неё — подсказка, пока клавиши нет или ждём нажатия.</summary>
    public bool ShowKeyCap => !IsCapturingKey && _settingsManager.PushToTalkKey != 0;

    public string PushToTalkHint => IsCapturingKey ? Strings.PressKeyText : Strings.PushToTalkKeyNotSet;

    public bool IsMouseKey => _settingsManager.PushToTalkKey is MiddleMouseButton or MouseButton4 or MouseButton5;

    [RelayCommand]
    private void CaptureKey() => IsCapturingKey = true;

    public void AssignKey(Key key) => Assign(KeyInterop.VirtualKeyFromKey(key));

    /// <summary>Левой и правой кнопкой нажимают саму кнопку назначения, они не назначаются.</summary>
    public void AssignMouseButton(MouseButton button)
    {
        int code = button switch
        {
            MouseButton.Middle => MiddleMouseButton,
            MouseButton.XButton1 => MouseButton4,
            MouseButton.XButton2 => MouseButton5,
            _ => 0
        };
        if (code != 0)
            Assign(code);
    }

    public void CancelCapture() => IsCapturingKey = false;

    private void Assign(int virtualKey)
    {
        _settingsManager.PushToTalkKey = virtualKey;
        IsCapturingKey = false;
        OnPropertyChanged(nameof(PushToTalkKeyText));
        OnPropertyChanged(nameof(ShowKeyCap));
        OnPropertyChanged(nameof(IsMouseKey));
    }

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static partial uint MapVirtualKey(uint code, uint mapType);

    private const uint VirtualKeyToChar = 2;

    // Печатные клавиши — их символом в текущей раскладке, остальные — по имени.
    private static string KeyName(int virtualKey)
    {
        switch (virtualKey)
        {
            case 0: return Strings.PushToTalkKeyNotSet;
            case MiddleMouseButton: return string.Format(Strings.MouseButtonText, 3);
            case MouseButton4: return string.Format(Strings.MouseButtonText, 4);
            case MouseButton5: return string.Format(Strings.MouseButtonText, 5);
        }

        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return $"Num {key - Key.NumPad0}";

        char symbol = (char)(MapVirtualKey((uint)virtualKey, VirtualKeyToChar) & 0xFFFF);
        if (symbol > ' ' && !char.IsControl(symbol))
            return char.ToUpperInvariant(symbol).ToString();

        // У этих клавиш в Key два имени на одно значение, ToString выбирает старое (Capital, Return, Prior, Next)
        return key switch
        {
            Key.CapsLock => "CapsLock",
            Key.Enter => "Enter",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            _ => key.ToString()
        };
    }
}

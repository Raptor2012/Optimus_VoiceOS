namespace Optimus.Shell.Views;

using System;
using System.Windows;
using System.Windows.Controls;
using Optimus.Shell.ViewModels;

public partial class SettingsPanel : UserControl
{
    private PersonalSettings? _preferences;
    private AgentCapacityViewModel? _capacity;
    private bool _loading;

    public event EventHandler? Closed;
    public event EventHandler? SettingsChanged;
    public event EventHandler? ResetTimersRequested;

    public SettingsPanel() => InitializeComponent();

    public void LoadSettings(PersonalSettings preferences, AgentCapacityViewModel capacity)
    {
        _loading = true;
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _capacity = capacity ?? throw new ArgumentNullException(nameof(capacity));
        VoiceCombo.SelectedIndex = Math.Clamp(VoiceCombo.Items.IndexOf(VoiceCombo.Items[0]), 0, VoiceCombo.Items.Count - 1);
        for (int i = 0; i < VoiceCombo.Items.Count; i++)
            if ((VoiceCombo.Items[i] as ComboBoxItem)?.Content?.ToString() == preferences.TtsVoice) VoiceCombo.SelectedIndex = i;
        SpeedSlider.Value = preferences.TtsSpeed;
        NarrationCheck.IsChecked = preferences.NarrationEnabled;
        ListeningHotkeyBox.Text = preferences.ToggleListeningHotkey;
        CompanionHotkeyBox.Text = preferences.ToggleCompanionHotkey;
        ModeCombo.SelectedIndex = preferences.StartExpanded ? 1 : 0;
        AgentsList.ItemsSource = capacity.Agents;
        _loading = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SpeedLabel.Text = $"{e.NewValue:0.0}×";
        OnPreferenceChanged(sender, e);
    }

    private void OnPreferenceChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || _preferences == null) return;
        _preferences.TtsVoice = (VoiceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Default local voice";
        _preferences.TtsSpeed = SpeedSlider.Value;
        _preferences.NarrationEnabled = NarrationCheck.IsChecked == true;
        _preferences.ToggleListeningHotkey = ListeningHotkeyBox.Text.Trim();
        _preferences.ToggleCompanionHotkey = CompanionHotkeyBox.Text.Trim();
        _preferences.StartExpanded = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "expanded";
        try { _preferences.Save(PersonalSettings.DefaultPath); } catch { /* preferences are best effort */ }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnResetTimers(object sender, RoutedEventArgs e)
    {
        _capacity?.ResetTimers();
        ResetTimersRequested?.Invoke(this, EventArgs.Empty);
    }
}

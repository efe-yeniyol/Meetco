using System.Windows;
using MeetcoApp.ViewModels;

namespace MeetcoApp.Views;

/// <summary>First-launch onboarding: what Meetco does, the API key and the microphone permission.</summary>
public partial class WelcomeWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public WelcomeWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Closing += (_, _) => BindingFlush.UpdateSources(this);
        Closed += (_, _) => _viewModel.Detach();
    }

    private async void OnDownloadWhisper(object sender, RoutedEventArgs e) => await _viewModel.DownloadWhisperModelAsync();

    private void OnCancelDownload(object sender, RoutedEventArgs e) => _viewModel.CancelDownload();

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SaveKey(KeyBox.Password)) KeyBox.Clear();
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        await _viewModel.TestConnectionAsync(KeyBox.Password);
    }

    private void OnGetStarted(object sender, RoutedEventArgs e)
    {
        // A key typed but not saved yet is saved now.
        if (KeyBox.Password.Trim().Length > 0 && !_viewModel.SaveKey(KeyBox.Password)) return;
        KeyBox.Clear();
        if (!_viewModel.CanGetStarted)
        {
            MessageBox.Show(this, "Add an OpenAI API key, or choose on-device transcription, to get started.", "Meetco",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _viewModel.Settings.HasCompletedOnboarding = true;
        Close();
    }
}

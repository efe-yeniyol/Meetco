using System.Windows;
using MeetcoApp.ViewModels;

namespace MeetcoApp.Views;

/// <summary>Settings. A normal activating window, because it needs keyboard input.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Activated += (_, _) => _viewModel.RefreshSessionCount();
    }

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SaveKey(KeyBox.Password)) KeyBox.Clear();
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        await _viewModel.TestConnectionAsync(KeyBox.Password);
    }

    private void OnDeleteKey(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Delete the saved OpenAI API key from this PC?", "Meetco",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK) _viewModel.DeleteKey();
    }

    private void OnRefreshMicrophones(object sender, RoutedEventArgs e) => _viewModel.RefreshMicrophones();

    private void OnOpenHistory(object sender, RoutedEventArgs e) => _viewModel.OpenHistoryFolder();

    private void OnOpenLogs(object sender, RoutedEventArgs e) => _viewModel.OpenLogFolder();

    private void OnDeleteHistory(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SessionCount == 0) return;
        var answer = MessageBox.Show(this, $"Delete all {_viewModel.SessionCount} saved transcripts? This cannot be undone.", "Meetco",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        if (_viewModel.DeleteHistory() is { } error) MessageBox.Show(this, error, "Meetco", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

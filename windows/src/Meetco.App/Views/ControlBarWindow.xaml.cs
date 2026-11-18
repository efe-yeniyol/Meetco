using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Meetco.Core.Models;
using MeetcoApp.Platform;
using MeetcoApp.ViewModels;

namespace MeetcoApp.Views;

/// <summary>The always-on-top toolbar: listen, capture mode, levels, quick actions.</summary>
public partial class ControlBarWindow : Window
{
    private readonly ShellViewModel _viewModel;

    public ControlBarWindow(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowHelper.MakeFloating(this);
            WindowHelper.SetHiddenFromCapture(this, viewModel.Settings.HideFromScreenSharing);
        };
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse was released before the drag started.
        }
    }

    private void OnModeButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ModeButton,
            Placement = PlacementMode.Bottom,
        };
        var current = _viewModel.Session.ActiveMode ?? _viewModel.Settings.CaptureMode;
        foreach (var mode in Enum.GetValues<AudioCaptureMode>())
        {
            var item = new MenuItem
            {
                Header = mode.Title(),
                IsCheckable = true,
                IsChecked = mode == current,
                Command = _viewModel.SetCaptureModeCommand,
                CommandParameter = mode,
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}

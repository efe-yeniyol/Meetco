using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MeetcoApp.Platform;
using MeetcoApp.ViewModels;

namespace MeetcoApp.Views;

/// <summary>The resizable floating panel with the Answers and Transcript tabs.</summary>
public partial class MainPanelWindow : Window
{
    public static readonly Size DefaultExpandedSize = new(600, 400);
    public static readonly Size CompactSize = new(440, 220);

    private readonly ShellViewModel _viewModel;
    private Size _expandedSize = DefaultExpandedSize;

    public MainPanelWindow(ShellViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowHelper.MakeFloating(this);
            WindowHelper.SetHiddenFromCapture(this, viewModel.Settings.HideFromScreenSharing);
        };
        viewModel.PropertyChanged += OnViewModelChanged;
        ((INotifyCollectionChanged)viewModel.Transcript.Segments).CollectionChanged += OnTranscriptChanged;
    }

    /// <summary>Size used when leaving compact mode (and saved between launches).</summary>
    public Size ExpandedSize => _viewModel.IsCompact ? _expandedSize : new Size(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);

    public void ApplyInitialSize(Size expanded)
    {
        _expandedSize = expanded;
        var size = _viewModel.IsCompact ? CompactSize : expanded;
        Width = size.Width;
        Height = size.Height;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsCompact)) ApplyCompact(_viewModel.IsCompact);
        else if (e.PropertyName == nameof(ShellViewModel.IsTranscriptTab) && _viewModel.IsTranscriptTab) ScrollTranscriptToEnd();
    }

    private void ApplyCompact(bool compact)
    {
        // Keep the right edge in place so the panel stays next to the meeting window.
        var right = Left + ActualWidth;
        if (compact)
        {
            _expandedSize = new Size(ActualWidth, ActualHeight);
            Width = CompactSize.Width;
            Height = CompactSize.Height;
        }
        else
        {
            Width = _expandedSize.Width;
            Height = _expandedSize.Height;
        }
        Left = right - Width;
    }

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel.Settings.AutoScrollTranscript && e.Action == NotifyCollectionChangedAction.Add) ScrollTranscriptToEnd();
    }

    private void ScrollTranscriptToEnd()
    {
        // Wait for layout so the new item is measured.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => TranscriptScroll.ScrollToEnd()));
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
        }
    }
}

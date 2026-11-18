using System.Windows.Input;

namespace MeetcoApp.ViewModels;

/// <summary>Minimal ICommand. Commands are always enabled; buttons bind IsEnabled where needed.</summary>
public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>A value and its display title, for ComboBoxes.</summary>
public sealed record Option(object? Value, string Title)
{
    public override string ToString() => Title;
}

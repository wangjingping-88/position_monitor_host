using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace PositionMonitor.App.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected void Refresh() => Changed("");
}

public sealed class ActionCommand(Action action, Func<bool>? enabled = null) : ICommand
{
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public void Execute(object? parameter) => action();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

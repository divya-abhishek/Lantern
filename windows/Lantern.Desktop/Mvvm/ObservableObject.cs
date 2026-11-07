using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Lantern.Desktop.Mvvm;

/// <summary>
/// Minimal INotifyPropertyChanged base. The desktop app only needs change notification and
/// commands, so pulling in a full MVVM framework would be dead weight.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>Sets a field and notifies only when the value actually changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Raise(property);
        return true;
    }
}

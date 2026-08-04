using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BCSTool.Infrastructure;

/// <summary>
/// Small MVVM helper base class.
///
/// WPF data binding does not automatically know when a normal C# property
/// changes. INotifyPropertyChanged provides that notification mechanism.
///
/// ViewModels inherit from BindableBase so they can call SetProperty(...)
/// instead of manually raising PropertyChanged every time a value changes.
/// </summary>
public abstract class BindableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Updates a backing field only when the new value is actually different.
    /// If the value changes, WPF is notified so bound controls refresh.
    /// </summary>
    protected bool SetProperty<T>(
        ref T storage,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return false;

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Manually tells WPF that a property value should be re-read.
    /// CallerMemberName lets us omit the property name in most cases.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

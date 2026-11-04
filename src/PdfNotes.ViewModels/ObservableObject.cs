using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PdfNotes.ViewModels;

/// <summary>Screen state that tells its views when a property changes (P1). No MVVM library: the repository keeps outside dependencies to Clipper2 in Core.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Stores <paramref name="value"/> and raises <see cref="PropertyChanged"/> when it differs. Returns true when it changed.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

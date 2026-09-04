namespace Optimus.Shell.ViewModels;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Optimus.Providers;
using Optimus.Providers.Windows;

/// <summary>One destination card in the picker.</summary>
public sealed class DestinationOption : INotifyPropertyChanged
{
    private DestinationStatus _status;

    public DestinationOption(IDestinationAdapter adapter, DestinationStatus status)
    {
        Adapter = adapter;
        _status = status;
    }

    public IDestinationAdapter Adapter { get; }

    public string DestinationId => Adapter.DestinationId;

    public string DisplayName => Adapter.DisplayName;

    public DestinationStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(BoundLabel));
        }
    }

    public string StatusDetail => Status.Detail;

    public bool IsReady => Status.CanSend;

    public string BoundLabel => Status.Bound?.DisplayLabel ?? "not bound";

    public string StatusColor => Status.Readiness switch
    {
        DestinationReadiness.Ready => "#30D158",
        DestinationReadiness.AmbiguousWindow => "#FF9500",
        DestinationReadiness.NotBound => "#FF9500",
        DestinationReadiness.BoundWindowGone => "#FF453A",
        _ => "#636366"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

using System.ComponentModel;

namespace WhateverITPCCare.Models;

public enum HealthStatus { Good, Warning, Critical, Info }

/// <summary>
/// One line in a diagnostic report: what we checked, how it looks, a plain
/// English explanation and (optionally) a safe repair that can fix it.
/// </summary>
public class HealthItem : INotifyPropertyChanged
{
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";

    private HealthStatus _status = HealthStatus.Info;
    public HealthStatus Status
    {
        get => _status;
        set { _status = value; OnChanged(nameof(Status)); OnChanged(nameof(StatusText)); OnChanged(nameof(StatusColor)); }
    }

    private string _detail = "";
    public string Detail
    {
        get => _detail;
        set { _detail = value; OnChanged(nameof(Detail)); }
    }

    /// <summary>Plain-English explanation of what this means for the user.</summary>
    public string Explanation { get; set; } = "";

    /// <summary>Id of a RepairAction that can address this, or null.</summary>
    public string? RepairId { get; set; }

    public string StatusText => Status switch
    {
        HealthStatus.Good => "Healthy",
        HealthStatus.Warning => "Needs attention",
        HealthStatus.Critical => "Action needed",
        _ => "Info"
    };

    public string StatusColor => Status switch
    {
        HealthStatus.Good => "#FF34C759",
        HealthStatus.Warning => "#FFFFB020",
        HealthStatus.Critical => "#FFFF4D4F",
        _ => "#FF8CA0B8"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ods.Core.Catalog;
using Ods.Desktop.Services;

namespace Ods.Desktop.ViewModels;

public sealed record ServiceItem(ServiceInfo Info)
{
    public string Name => Info.Name;

    public string Description => Info.Description;

    public string Approach => Info.NativeApproach;

    public string StatusText => Info.NativeStatus switch
    {
        "built-in" => "Built in",
        "next" => "Next",
        "planned" => "Planned",
        "needs-design" => "Needs design",
        "external" => "Windows app",
        "not-planned" => "Not planned",
        _ => "Container",
    };

    /// <summary>ready, next, planned, attention or muted: drives the badge colour.</summary>
    public string StatusKind => Info.NativeStatus switch
    {
        "built-in" => "ready",
        "next" => "next",
        "needs-design" => "attention",
        "planned" => "planned",
        _ => "muted",
    };
}

/// <summary>
/// ODS services and how each runs on native Windows, from config/extensions-catalog.json
/// and windows-native-services.json.
/// </summary>
public sealed partial class ExtensionsViewModel : ObservableObject
{
    private readonly List<ServiceItem> _library;

    public ExtensionsViewModel(AppHost host)
    {
        Core = new ObservableCollection<ServiceItem>(host.Services.Core.Select(s => new ServiceItem(s)));
        _library = host.Services.Library.Select(s => new ServiceItem(s)).ToList();
        Library = new ObservableCollection<ServiceItem>(_library);
    }

    public ObservableCollection<ServiceItem> Core { get; }

    public ObservableCollection<ServiceItem> Library { get; }

    public int LibraryCount => _library.Count;

    public int BuiltInCount => Core.Count(s => s.Info.NativeStatus == "built-in");

    [ObservableProperty]
    private string _search = "";

    partial void OnSearchChanged(string value)
    {
        Library.Clear();
        foreach (var item in _library.Where(s => value.Length == 0
            || s.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
            || s.Description.Contains(value, StringComparison.OrdinalIgnoreCase)))
        {
            Library.Add(item);
        }
    }

    [RelayCommand]
    private static void OpenPlan() => AppHost.OpenUrl("https://github.com/Osmantic/ODS/blob/main/ods/docs/WINDOWS-NATIVE-MIGRATION-PLAN.md");
}

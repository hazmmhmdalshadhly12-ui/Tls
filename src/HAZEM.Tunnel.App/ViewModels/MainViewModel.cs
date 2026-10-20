using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HAZEM.Tunnel.App.Views;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.App.ViewModels;

/// <summary>One sidebar navigation entry.</summary>
public sealed record NavItem(string Glyph, string Title, Func<System.Windows.Controls.UserControl> CreateView);

/// <summary>Shell VM: builds pages lazily and hosts the active one.</summary>
public partial class MainViewModel : ObservableObject
{
    public string VersionLabel => "v1.1.0";

    public ObservableCollection<NavItem> Pages { get; }

    [ObservableProperty]
    private NavItem _selectedPage;

    [ObservableProperty]
    private System.Windows.Controls.UserControl? _currentView;

    private readonly Dictionary<NavItem, System.Windows.Controls.UserControl> _cache = new();

    public MainViewModel(
        DashboardViewModel dashboard,
        ServersViewModel servers,
        TunnelSettingsViewModel tunnelSettings,
        ProfilesViewModel profiles,
        StatisticsViewModel statistics,
        LogsViewModel logs,
        SettingsViewModel settings,
        AboutViewModel about)
    {
        Pages = new ObservableCollection<NavItem>
        {
            new("\uE9F5", "Dashboard",       () => new DashboardView { DataContext = dashboard }),
            new("\uEA18", "Servers",         () => new ServersView   { DataContext = servers }),
            new("\uE70F", "Tunnel Settings", () => new TunnelSettingsView { DataContext = tunnelSettings }),
            new("\uE8FD", "Profiles",        () => new ProfilesView  { DataContext = profiles }),
            new("\uE9D2", "Statistics",      () => new StatisticsView{ DataContext = statistics }),
            new("\uE81C", "Logs",            () => new LogsView      { DataContext = logs }),
            new("\uE713", "Settings",        () => new SettingsView  { DataContext = settings }),
            new("\uE946", "About",           () => new AboutView     { DataContext = about }),
        };
        SelectedPage = Pages[0];
    }

    partial void OnSelectedPageChanged(NavItem value)
    {
        if (value is null) return;
        if (!_cache.TryGetValue(value, out var view))
        {
            view = value.CreateView();
            _cache[value] = view;
        }
        CurrentView = view;
    }
}

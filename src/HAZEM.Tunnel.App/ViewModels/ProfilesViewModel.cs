using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HAZEM.Tunnel.App.Services;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Validation;
using HAZEM.Tunnel.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.App.ViewModels;

/// <summary>Profiles page: CRUD, import (with preview), export, validation display.</summary>
public partial class ProfilesViewModel : ObservableObject
{
    private readonly IProfileStore _store;
    private readonly ProfileValidator _validator;
    private readonly ProfileImporter _importer;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ProfilesViewModel> _logger;
    public ProfilesViewModel(IProfileStore store, ProfileValidator validator, ProfileImporter importer,
        IDialogService dialogs, ILogger<ProfilesViewModel> logger)
    {
        _store = store;
        _validator = validator;
        _importer = importer;
        _dialogs = dialogs;
        _logger = logger;
        Reload();
    }

    public ObservableCollection<ProfileRow> Rows { get; } = new();

    [ObservableProperty]
    private ProfileRow? _selected;

    [ObservableProperty]
    private string _validationSummary = string.Empty;

    public sealed class ProfileRow
    {
        public required TunnelProfile Profile { get; init; }
        public string Name => Profile.Name;
        public string Kind => Profile.TransportKind.ToString();
        public string Detail => Profile.Describe();
        public string Validation => ValidationText;
        public string ValidationText { get; init; } = "";
        public bool IsValid { get; init; }
    }

    [RelayCommand]
    private void Reload()
    {
        Selected = null;
        Rows.Clear();
        foreach (var p in _store.LoadAll().OrderBy(p => p.Name))
        {
            var v = _validator.Validate(p);
            Rows.Add(new ProfileRow
            {
                Profile = p,
                ValidationText = v.IsValid ? "✔ valid" : "✘ " + v.Issues.Count + " issue(s)",
                IsValid = v.IsValid
            });
        }
        ValidationSummary = $"{Rows.Count} profile(s) on disk in {_store.DirectoryPath}";
        _logger.LogDebug("Profiles page reloaded: {Count} profiles.", Rows.Count);
    }

    [RelayCommand]
    private void New()
    {
        var dialog = new Views.ProfileEditorDialog(new TunnelProfile(), isNew: true)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            _store.Save(dialog.ViewModel.Editing!);
            Reload();
        }
    }

    [RelayCommand]
    private void Edit()
    {
        if (Selected?.Profile is not { } profile) return;
        var copy = profile.Clone();
        var dialog = new Views.ProfileEditorDialog(copy, isNew: false)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            _store.Save(copy);
            Reload();
        }
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected?.Profile is not { } profile) return;
        var copy = profile.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = profile.Name + " (copy)";
        copy.CreatedAt = DateTimeOffset.UtcNow;
        _store.Save(copy);
        Reload();
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected?.Profile is not { } profile) return;
        if (!_dialogs.Confirm("Delete profile", $"Delete profile '{profile.Name}' permanently?")) return;
        _store.Delete(profile.Id);
        Reload();
    }

    [RelayCommand]
    private void Export()
    {
        if (Selected?.Profile is not { } profile) return;
        var path = _dialogs.SaveTextFile("Export profile", "HAZEM profile (*.json)|*.json", $"profile-{profile.Name}.json");
        if (path is null) return;
        try
        {
            File.WriteAllText(path, ProfileJson.Serialize(profile));
            _dialogs.Info("Export", "Profile exported. Secrets are NOT included — only credential references.");
        }
        catch (Exception ex)
        {
            _dialogs.Error("Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private void Import()
    {
        var path = _dialogs.OpenTextFile("Import profile", "HAZEM profile (*.json)|*.json|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            var preview = _importer.Preview(File.ReadAllText(path), Path.GetFileName(path));
            if (!preview.Validation.IsValid)
            {
                _dialogs.Error("Import blocked",
                    "The profile has validation problems and was NOT imported:\n\n" + preview.Validation.Describe());
                return;
            }
            var action = preview.Action == ImportAction.Replace ? "REPLACE the existing profile" : "ADD a new profile";
            if (_dialogs.Confirm("Import preview",
                    $"Profile '{preview.Profile.Name}' ({preview.Profile.TransportKind})\n" +
                    $"Detail: {preview.Profile.Describe()}\n\nThis will {action}. Continue?"))
            {
                _importer.Commit(preview);
                Reload();
                _dialogs.Info("Import", "Profile imported successfully. Re-enter any missing credentials in the editor.");
            }
        }
        catch (TunnelException te)
        {
            _dialogs.Error("Import failed", te.Message);
        }
    }
}

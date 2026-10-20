using System.IO;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Validation;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Infrastructure.Configuration;

/// <summary>Import flow with explicit preview → commit (user sees what will change before it happens).</summary>
public sealed class ProfileImporter
{
    private readonly IProfileStore _store;
    private readonly ProfileValidator _validator;
    private readonly ILogger<ProfileImporter> _logger;

    public ProfileImporter(IProfileStore store, ProfileValidator validator, ILogger<ProfileImporter> logger)
    {
        _store = store;
        _validator = validator;
        _logger = logger;
    }

    /// <summary>Preview: parse + validate without touching the store.</summary>
    public ImportPreview Preview(string json, string? sourceName = null)
    {
        var profile = ProfileJson.Parse(json, sourceName);
        var validation = _validator.Validate(profile);
        var existing = _store.LoadAll().FirstOrDefault(p => p.Id == profile.Id);
        return new ImportPreview(profile, validation, existing is not null ? ImportAction.Replace : ImportAction.Add);
    }

    /// <summary>Commit a previously previewed import.</summary>
    public void Commit(ImportPreview preview)
    {
        if (!preview.Validation.IsValid)
            throw new TunnelException(TunnelErrorCategory.Configuration,
                "Refusing to import an invalid profile: " + preview.Validation.Describe());
        _store.Save(preview.Profile);
        _logger.LogInformation("Imported profile '{Name}' ({Action}).", preview.Profile.Name, preview.Action);
    }

    /// <summary>Convenience: validate-and-import in one step (used by drag-drop).</summary>
    public ImportPreview Import(string json, string? sourceName = null)
    {
        var preview = Preview(json, sourceName);
        Commit(preview);
        return preview;
    }
}

public enum ImportAction { Add, Replace }

public sealed record ImportPreview(TunnelProfile Profile, ValidationResult Validation, ImportAction Action);

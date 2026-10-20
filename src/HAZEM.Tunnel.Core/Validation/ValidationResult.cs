namespace HAZEM.Tunnel.Core.Validation;

/// <summary>One validation problem, optionally pointing at the offending field.</summary>
public sealed record ValidationIssue(string Field, string Message);

/// <summary>Immutable validation result with rich issues (never just "invalid").</summary>
public sealed class ValidationResult
{
    private readonly List<ValidationIssue> _issues = new();

    public bool IsValid => _issues.Count == 0;
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public void Add(string field, string message) => _issues.Add(new ValidationIssue(field, message));

    public void AddRange(IEnumerable<ValidationIssue> issues) => _issues.AddRange(issues);

    public void Merge(ValidationResult other)
    {
        if (!ReferenceEquals(this, other)) _issues.AddRange(other._issues);
    }

    public string Describe() => IsValid
        ? "OK"
        : string.Join(Environment.NewLine, _issues.Select(i => i.Field is null ? i.Message : $"{i.Field}: {i.Message}"));

    public static ValidationResult Ok() => new();
}

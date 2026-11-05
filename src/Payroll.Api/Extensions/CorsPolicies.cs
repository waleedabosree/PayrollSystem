namespace Payroll.Api.Extensions;

/// <summary>
/// Named CORS policies in one place. Impossible to point <c>UseCors</c> and
/// <c>AddPolicy</c> at two different strings this way -- a silent misconfiguration
/// where CORS "works" for one path and not another is notoriously hard to spot.
/// </summary>
public static class CorsPolicies
{
    public const string AngularClient = "angular-dev";
}
namespace EFCoreSqlPreview.Core.Tests.EndToEnd;

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself, with the reason, when this machine cannot run a real
/// worker build.
/// </summary>
/// <remarks>
/// The skip is decided at discovery time from <see cref="EndToEndEnvironment.SkipReason"/>, which probes the
/// installed SDKs exactly once for the whole run.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EndToEndFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute, skipping when the environment is not capable.</summary>
    public EndToEndFactAttribute() => this.Skip = EndToEndEnvironment.SkipReason;
}

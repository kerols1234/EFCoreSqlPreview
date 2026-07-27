using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using EFCoreSqlPreview.Core.Projects;

namespace EFCoreSqlPreview.Core.Execution;

/// <summary>
/// One parameter attached to a captured <c>DbCommand</c>.
/// </summary>
/// <param name="Name">The provider's parameter name, e.g. <c>@p</c>, <c>@__search_0</c> or <c>:p_0</c>.</param>
/// <param name="DbType">The <c>DbType</c> the provider assigned.</param>
/// <param name="ClrType">
/// The value's CLR type, rendered the way a C# developer writes it: <c>decimal</c> rather than
/// <c>System.Decimal</c>, and <c>int[]</c> for a collection parameter Npgsql sends whole.
/// </param>
/// <param name="Value">The value rendered for display; collections and byte arrays are rendered specially.</param>
/// <param name="IsNull">Whether the value is <see langword="null"/> or <c>DBNull</c>.</param>
/// <param name="Size">The parameter size the provider reported.</param>
/// <param name="Direction">The parameter direction, normally <c>Input</c>.</param>
public sealed record CapturedParameter(
    string Name,
    string? DbType,
    string? ClrType,
    string? Value,
    bool IsNull,
    int Size,
    string? Direction);

/// <summary>
/// One SQL command EF Core built. A split query produces several.
/// </summary>
/// <param name="Sql">The command text, with line endings normalised to <c>\n</c> and trimmed.</param>
/// <param name="Parameters">The command's parameters, in provider order.</param>
public sealed record CapturedCommand(
    string Sql,
    IReadOnlyList<CapturedParameter> Parameters);

/// <summary>
/// What the query actually returned at runtime.
/// </summary>
/// <param name="IsAsync">Whether the terminal operator was awaited by the worker.</param>
/// <param name="Shape">The result shape, matching <see cref="Analysis.ResultShape"/> names.</param>
/// <param name="ElementType">The element type's display name, e.g. <c>ProductDto</c> or <c>anonymous&lt;int, string&gt;</c>.</param>
/// <param name="ElementKind">Whether the element is an entity, a DTO, anonymous, a tuple or a scalar.</param>
/// <param name="KeyType">The key type for a dictionary result; otherwise <see langword="null"/>.</param>
/// <param name="DeclaredResultType">The static type of the query expression, available even when the value is null.</param>
/// <param name="RuntimeResultType">The runtime type of the returned value, or <see langword="null"/> when it was null.</param>
public sealed record ResultDescription(
    bool IsAsync,
    string? Shape,
    string? ElementType,
    string? ElementKind,
    string? KeyType,
    string? DeclaredResultType,
    string? RuntimeResultType);

/// <summary>
/// Classifies why a preview failed, so the UI can offer the right next step.
/// </summary>
public enum PreviewErrorKind
{
    /// <summary>Nothing went wrong.</summary>
    None,

    /// <summary>The generated worker or the user's project failed to compile.</summary>
    CompileError,

    /// <summary>No <c>DbContext</c> could be constructed; the message suggests a design-time factory.</summary>
    ContextActivationFailed,

    /// <summary>EF Core refused to translate the query to SQL.</summary>
    NotTranslatable,

    /// <summary>The provider threw, typically a provider-exclusive translation under a foreign dialect.</summary>
    ProviderError,

    /// <summary>The requested dialect's provider has no build matching the project's EF Core major version.</summary>
    ProviderVersionMismatch,

    /// <summary>The worker produced no payload at all.</summary>
    NoPayload,

    /// <summary>The worker exceeded its time budget and was killed.</summary>
    Timeout,

    /// <summary>The selection was refused before the worker ran.</summary>
    OutOfScope,

    /// <summary>
    /// A free variable the analyzer could not recover a value for was read while building the query.
    /// </summary>
    FreeVariableValueRequired,

    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>
/// The worker's JSON payload, extracted from stdout between the sentinels.
/// </summary>
public sealed record PreviewResponse
{
    /// <summary>The wire-format version the worker template emits.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Marks the start of the JSON payload on the worker's stdout.</summary>
    public const string PayloadBeginSentinel = "<<<EFSQLPREVIEW-BEGIN>>>";

    /// <summary>Marks the end of the JSON payload on the worker's stdout.</summary>
    public const string PayloadEndSentinel = "<<<EFSQLPREVIEW-END>>>";

    /// <summary>The wire-format version of this payload.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Whether at least one command was captured and the run is worth showing.</summary>
    public bool Success { get; init; }

    /// <summary>The provider the worker actually ran under.</summary>
    public EfProvider Provider { get; init; } = EfProvider.Unknown;

    /// <summary>The EF Core assembly version the worker loaded.</summary>
    public string? EfCoreVersion { get; init; }

    /// <summary>The <c>DbContext</c> type the worker instantiated.</summary>
    public string? ContextType { get; init; }

    /// <summary>Which rung of the activation ladder produced the context, e.g. <c>DesignTimeFactory</c>.</summary>
    public string? ActivationStrategy { get; init; }

    /// <summary>Which capture-reader rung produced the result, e.g. <c>SyntheticRow</c> or <c>NoRows</c>.</summary>
    public string? CaptureMode { get; init; }

    /// <summary>The connection string in effect, with any password removed.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Every command EF Core built, in the order it built them.</summary>
    public IReadOnlyList<CapturedCommand> Commands { get; init; } = Array.Empty<CapturedCommand>();

    /// <summary>What the query returned, or <see langword="null"/> when it never got that far.</summary>
    public ResultDescription? Result { get; init; }

    /// <summary>Non-fatal notes, e.g. partial capture of a split query.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>The failure message, or <see langword="null"/> on success.</summary>
    public string? Error { get; init; }

    /// <summary>How to categorise <see cref="Error"/>.</summary>
    public PreviewErrorKind ErrorKind { get; init; } = PreviewErrorKind.None;

    /// <summary>
    /// The JSON contract shared by the worker and the extension: camelCase, case-insensitive on read,
    /// enums as strings, and relaxed escaping so SQL stays readable in the payload.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    /// <summary>Builds a failure response.</summary>
    /// <param name="kind">How to categorise the failure.</param>
    /// <param name="message">The message to show.</param>
    /// <returns>An unsuccessful response carrying the message.</returns>
    public static PreviewResponse Failure(PreviewErrorKind kind, string message)
        => new() { Success = false, ErrorKind = kind, Error = message };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        };

        // No naming policy on enums: the worker template writes PascalCase names such as "SqlServer",
        // and the converter matches those case-insensitively on read.
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

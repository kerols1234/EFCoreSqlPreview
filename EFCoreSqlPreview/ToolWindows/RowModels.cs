using System.Runtime.Serialization;
using EFCoreSqlPreview.Core.Projects;
using Microsoft.VisualStudio.Extensibility.UI;

namespace EFCoreSqlPreview.ToolWindows
{
    /// <summary>
    /// One entry in the dialect picker.
    /// </summary>
    /// <remarks>
    /// The provider travels as its enum name rather than as an un-serialized field: Remote UI only marshals
    /// <see cref="DataMemberAttribute"/> members, and relying on reference identity for state the view model
    /// must read back is a sharper edge than a two-line parse.
    /// </remarks>
    [DataContract]
    internal sealed class DialectOption
    {
        /// <summary>Text shown in the combo box.</summary>
        [DataMember]
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>The <see cref="EfProvider"/> name, or <c>Unknown</c> for the auto-detect entry.</summary>
        [DataMember]
        public string ProviderName { get; init; } = nameof(EfProvider.Unknown);

        /// <summary>The provider this option selects.</summary>
        /// <returns>The parsed provider, or <see cref="EfProvider.Unknown"/> for auto-detect.</returns>
        public EfProvider ToProvider()
            => Enum.TryParse<EfProvider>(this.ProviderName, out var provider) ? provider : EfProvider.Unknown;
    }

    /// <summary>
    /// One parameter row in the parameters table.
    /// </summary>
    [DataContract]
    internal sealed class ParameterRow
    {
        /// <summary>The provider's parameter name, e.g. <c>@__search_0</c>.</summary>
        [DataMember]
        public string Name { get; init; } = string.Empty;

        /// <summary>The <c>DbType</c> the provider assigned.</summary>
        [DataMember]
        public string DbType { get; init; } = string.Empty;

        /// <summary>The CLR type of the value.</summary>
        [DataMember]
        public string ClrType { get; init; } = string.Empty;

        /// <summary>The value rendered for display.</summary>
        [DataMember]
        public string Value { get; init; } = string.Empty;
    }

    /// <summary>
    /// One captured SQL command. A split query produces several, which is why the SQL tab has a list.
    /// </summary>
    [DataContract]
    internal sealed class CommandRow
    {
        /// <summary>Label shown in the command list, e.g. <c>Command 1 of 3</c>.</summary>
        [DataMember]
        public string Title { get; init; } = string.Empty;

        /// <summary>The command text.</summary>
        [DataMember]
        public string Sql { get; init; } = string.Empty;

        /// <summary>The command's parameters, in provider order.</summary>
        [DataMember]
        public ObservableList<ParameterRow> Parameters { get; } = new();

        /// <summary>A one-line summary of the parameter count, shown above the table.</summary>
        [DataMember]
        public string ParameterSummary { get; init; } = string.Empty;
    }

    /// <summary>
    /// One free variable the query referenced but did not declare. The value is editable so the user can
    /// replace a synthesized default with something that produces the SQL they actually care about.
    /// </summary>
    [DataContract]
    internal sealed class VariableRow : NotifyPropertyChangedObject
    {
        private string valueExpression = string.Empty;

        /// <summary>The identifier as it appears in the query.</summary>
        [DataMember]
        public string Name { get; init; } = string.Empty;

        /// <summary>The declared type, or <c>?</c> when the analyzer could not determine it syntactically.</summary>
        [DataMember]
        public string TypeName { get; init; } = "?";

        /// <summary>Where the value came from, e.g. <c>LiteralInitializer</c> or <c>SynthesizedDefault</c>.</summary>
        [DataMember]
        public string Source { get; init; } = string.Empty;

        /// <summary>Whether the analyzer had to guess, so the SQL is only as meaningful as the guess.</summary>
        [DataMember]
        public bool RequiresValue { get; init; }

        /// <summary>The value the analyzer suggested, kept so a re-run can tell edits from defaults.</summary>
        [DataMember]
        public string SuggestedValue { get; init; } = string.Empty;

        /// <summary>The C# expression that will be emitted for this variable. Edited in place by the user.</summary>
        [DataMember]
        public string ValueExpression
        {
            get => this.valueExpression;
            set => this.SetProperty(ref this.valueExpression, value);
        }

        /// <summary>Whether the user changed the value away from what the analyzer suggested.</summary>
        public bool IsOverridden
            => !string.Equals(this.valueExpression, this.SuggestedValue, StringComparison.Ordinal);
    }
}

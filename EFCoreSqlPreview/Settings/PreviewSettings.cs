using System.Text.Json;
using System.Text.Json.Serialization;
using EFCoreSqlPreview.Core.Projects;

namespace EFCoreSqlPreview.Settings
{
    /// <summary>
    /// User preferences that survive across Visual Studio sessions. Stored beside the generated worker scratch
    /// area under <c>%LOCALAPPDATA%\EFCoreSqlPreview</c> so nothing ever lands in the user's repository.
    /// </summary>
    internal sealed class PreviewSettings
    {
        private const int MinimumTimeoutSeconds = 10;
        private const int MaximumTimeoutSeconds = 900;

        /// <summary>
        /// How long the worker may run before its process tree is killed. A cold NuGet restore of the user's
        /// project happens inside this budget, so the default is generous.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// Absolute path to a <c>dotnet</c> executable to use instead of the one on <c>PATH</c>. Empty means
        /// use whatever the machine resolves. Useful when several SDKs are installed side by side.
        /// </summary>
        public string DotnetPath { get; set; } = string.Empty;

        /// <summary>
        /// The dialect the picker starts on. <see cref="EfProvider.Unknown"/> means "auto-detect from the
        /// project", which is what the picker shows as <c>Auto</c>.
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public EfProvider ProviderOverride { get; set; } = EfProvider.Unknown;

        /// <summary>
        /// Whether the diagnostics tab also shows the worker's raw stdout and stderr. Off by default because
        /// the build log is noisy; on when reporting a bug.
        /// </summary>
        public bool VerboseMode { get; set; }

        /// <summary>The extension's private data directory under <c>%LOCALAPPDATA%</c>.</summary>
        /// <remarks>Declared before <see cref="SettingsPath"/>: static initializers run in declaration order.</remarks>
        public static string RootDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EFCoreSqlPreview");

        /// <summary>Absolute path of the settings file.</summary>
        public static string SettingsPath { get; } = Path.Combine(RootDirectory, "settings.json");

        /// <summary>
        /// Reads the settings file, falling back to defaults for anything missing or corrupt.
        /// </summary>
        /// <returns>A settings instance; never <see langword="null"/>.</returns>
        public static PreviewSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<PreviewSettings>(File.ReadAllText(SettingsPath));
                    if (loaded is not null)
                    {
                        loaded.Normalize();
                        return loaded;
                    }
                }
            }
            catch (Exception)
            {
                // A corrupt or unreadable settings file must never stop the tool window from opening.
            }

            return new PreviewSettings();
        }

        /// <summary>
        /// Writes the settings file. Failures are swallowed: persistence is a convenience, not a feature the
        /// user asked for, and surfacing an I/O error here would only be noise.
        /// </summary>
        public void Save()
        {
            try
            {
                this.Normalize();
                Directory.CreateDirectory(RootDirectory);
                File.WriteAllText(
                    SettingsPath,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception)
            {
                // Intentionally ignored, see summary.
            }
        }

        /// <summary>The configured timeout, clamped into a range that cannot hang or instantly fail a run.</summary>
        /// <returns>The effective timeout.</returns>
        public TimeSpan EffectiveTimeout()
            => TimeSpan.FromSeconds(Math.Clamp(this.TimeoutSeconds, MinimumTimeoutSeconds, MaximumTimeoutSeconds));

        private void Normalize()
        {
            this.TimeoutSeconds = Math.Clamp(this.TimeoutSeconds, MinimumTimeoutSeconds, MaximumTimeoutSeconds);
            this.DotnetPath = this.DotnetPath?.Trim() ?? string.Empty;
        }
    }
}

using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ChangeMyVoice.Adapters.Persistence;

/// <summary>Einstellungen der Datenhaltung.</summary>
public sealed class PersistenceOptions
{
    /// <summary>Der Abschnitt in der Konfiguration.</summary>
    public const string SectionName = "Persistence";

    /// <summary>Der Pfad zur Datenbankdatei.</summary>
    [Required(AllowEmptyStrings = false)]
    public string DatabasePath { get; set; } = string.Empty;
}

/// <summary>Stellt Verbindungen zur SQLite-Datenbank bereit und legt das Schema an.</summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    /// <summary>Erzeugt die Fabrik und stellt sicher, dass das Schema existiert.</summary>
    public SqliteConnectionFactory(IOptions<PersistenceOptions> options)
    {
        var path = options.Value.DatabasePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Der Dienst läuft als eine Instanz, greift aber aus mehreren
            // Hintergrunddiensten zu; der gemeinsame Cache mit WAL hält die
            // Schreibvorgänge kurz.
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        EnsureSchema();
    }

    /// <summary>Öffnet eine Verbindung.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS reference_voices (
                id                  TEXT PRIMARY KEY,
                label               TEXT NOT NULL,
                label_key           TEXT NOT NULL UNIQUE,
                created_at_utc      TEXT NOT NULL,
                stored_codec        TEXT NOT NULL,
                stored_duration_ms  INTEGER NOT NULL,
                stored_sample_rate  INTEGER NOT NULL,
                stored_channels     INTEGER NOT NULL,
                original_codec      TEXT NOT NULL,
                original_duration_ms INTEGER NOT NULL,
                original_sample_rate INTEGER NOT NULL,
                original_channels   INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS conversion_jobs (
                id                   TEXT PRIMARY KEY,
                voice_id             TEXT NOT NULL,
                voice_label          TEXT NOT NULL,
                status               TEXT NOT NULL,
                diffusion_steps      INTEGER NOT NULL,
                inference_cfg_rate   REAL NOT NULL,
                length_adjust        REAL NOT NULL,
                f0_condition         INTEGER NOT NULL,
                fp16                 INTEGER NOT NULL,
                created_at_utc       TEXT NOT NULL,
                started_at_utc       TEXT NULL,
                finished_at_utc      TEXT NULL,
                downloaded_at_utc    TEXT NULL,
                error_code           TEXT NULL,
                error_message        TEXT NULL,
                output_size_bytes    INTEGER NULL,
                output_sha256        TEXT NULL,
                instance_id          TEXT NOT NULL,
                inference_process_id INTEGER NULL,
                artifacts_purged     INTEGER NOT NULL DEFAULT 0,
                source_length_ms     INTEGER NOT NULL DEFAULT 0
            );

            -- Nachtraeglich ergaenzt; bestehende Datenbanken bekommen die Spalte
            -- hier, neue haben sie schon aus der Tabellendefinition.
            CREATE INDEX IF NOT EXISTS ix_jobs_status ON conversion_jobs (status);
            CREATE INDEX IF NOT EXISTS ix_jobs_voice ON conversion_jobs (voice_id);
            CREATE INDEX IF NOT EXISTS ix_jobs_purged ON conversion_jobs (artifacts_purged);
            """;

        command.ExecuteNonQuery();

        EnsureColumn(connection, "conversion_jobs", "source_length_ms", "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>
    /// Ergänzt eine Spalte, falls sie noch fehlt.
    /// </summary>
    /// <remarks>
    /// Die Tabellendefinition oben greift nur bei einer neuen Datenbank. Eine
    /// bereits vorhandene würde sonst stillschweigend ohne die Spalte
    /// weiterlaufen und beim ersten Zugriff scheitern.
    /// </remarks>
    private static void EnsureColumn(
        SqliteConnection connection, string table, string column, string definition)
    {
        using var vorhanden = connection.CreateCommand();
        vorhanden.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name;";
        vorhanden.Parameters.AddWithValue("$name", column);

        if (Convert.ToInt64(vorhanden.ExecuteScalar()) > 0)
        {
            return;
        }

        using var ergaenzen = connection.CreateCommand();
        ergaenzen.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        ergaenzen.ExecuteNonQuery();
    }
}

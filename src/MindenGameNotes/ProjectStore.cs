using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MindenGameNotes;

public sealed class ProjectStore
{
    private readonly string path;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ProjectStore(string? storagePath = null)
    {
        path = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindenGameNotes", "project.json");
    }

    public async Task<BuilderWorkspace> LoadAsync()
    {
        if (!File.Exists(path)) return NewWorkspace();
        var json = await File.ReadAllTextAsync(path);
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException($"The workspace file is empty: {path}");
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException($"The workspace file contains malformed JSON and was not changed: {path}", ex); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"The workspace root must be a JSON object: {path}");
            var root = document.RootElement;
            BuilderWorkspace workspace;
            if (TryGet(root, "SchemaVersion", out var schema))
            {
                if (!schema.TryGetInt32(out var version) || version != BuilderWorkspace.CurrentSchemaVersion)
                    throw new InvalidDataException($"Unsupported workspace schema version '{schema}'; expected {BuilderWorkspace.CurrentSchemaVersion}. The file was not changed.");
                ValidateCurrentWorkspace(root);
                try { workspace = JsonSerializer.Deserialize<BuilderWorkspace>(json, JsonOptions) ?? throw new InvalidDataException("The workspace could not be read."); }
                catch (JsonException ex) { throw new InvalidDataException($"The schema-1 workspace is invalid and was not changed: {path}", ex); }
            }
            else if (IsRecognizableLegacyProject(root))
            {
                workspace = MigrateLegacy(root, json);
            }
            else
            {
                throw new InvalidDataException($"The file is neither a supported schema-1 workspace nor a recognizable legacy project and was not changed: {path}");
            }
            try { workspace.Normalize(); }
            catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
            {
                throw new InvalidDataException($"The workspace structure is incomplete or invalid and was not changed: {path}", ex);
            }
            RefreshSourceHealth(workspace);
            return workspace;
        }
    }

    public async Task SaveAsync(BuilderWorkspace workspace)
    {
        workspace.UpdatedUtc = DateTime.UtcNow;
        workspace.Normalize();
        foreach (var project in workspace.Projects) project.UpdatedUtc = workspace.UpdatedUtc;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, workspace, JsonOptions);
        File.Move(temp, path, true);
    }

    private static BuilderWorkspace NewWorkspace()
    {
        var project = new GameNotesProject();
        var workspace = new BuilderWorkspace { ActiveProjectId = project.Id, Projects = [project] };
        workspace.Normalize();
        return workspace;
    }

    private static BuilderWorkspace MigrateLegacy(JsonElement root, string json)
    {
        var project = JsonSerializer.Deserialize<GameNotesProject>(json, JsonOptions) ?? new GameNotesProject();
        project.Id = project.Id == Guid.Empty ? Guid.NewGuid() : project.Id;

        var legacyGameDate = default(DateTime);
        var hasGameDate = TryGet(root, "GameDate", out var gameDateElement) && gameDateElement.ValueKind == JsonValueKind.String && gameDateElement.TryGetDateTime(out legacyGameDate);
        project.GameDate = hasGameDate ? legacyGameDate.Date : null;
        project.Season = hasGameDate && legacyGameDate.Year is >= 1900 and <= 2200 ? legacyGameDate.Year : null;

        if (TryGet(root, "PageOne", out var pageOne) && pageOne.ValueKind == JsonValueKind.Object)
        {
            if (TryGet(pageOne, "Week", out var week) && week.TryGetInt32(out var weekNumber) && weekNumber > 0) project.Week = weekNumber;
            var rootOpponent = project.Opponent?.Trim() ?? "";
            var rootOpponentIsPlaceholder = rootOpponent.Length == 0 || rootOpponent.Equals("Opponent", StringComparison.OrdinalIgnoreCase);
            if (rootOpponentIsPlaceholder && TryGet(pageOne, "OpponentTeam", out var opponent) && opponent.ValueKind == JsonValueKind.String) project.Opponent = opponent.GetString() ?? "";
            else project.Opponent = rootOpponent;
            if (TryGet(pageOne, "Kickoff", out var kickoff) && kickoff.ValueKind == JsonValueKind.String && TryParseKickoff(kickoff.GetString(), out var parsed)) project.KickoffTime = parsed;
        }
        if (project.KickoffTime is null && hasGameDate && legacyGameDate.TimeOfDay != TimeSpan.Zero) project.KickoffTime = TimeOnly.FromTimeSpan(legacyGameDate.TimeOfDay);

        var workspace = new BuilderWorkspace { ActiveProjectId = project.Id, Projects = [project] };
        workspace.Normalize();
        return workspace;
    }

    private static bool TryParseKickoff(string? value, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Replace("a.m.", "AM", StringComparison.OrdinalIgnoreCase).Replace("p.m.", "PM", StringComparison.OrdinalIgnoreCase);
        return TimeOnly.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out time)
            || TimeOnly.TryParse(normalized, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out time);
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static void ValidateCurrentWorkspace(JsonElement root)
    {
        if (!TryGet(root, "ActiveProjectId", out var active) || active.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException("The schema-1 workspace is missing a valid ActiveProjectId.");
        if (!TryGet(root, "SourceFamilies", out var families) || families.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The schema-1 workspace is missing its SourceFamilies array.");
        if (!TryGet(root, "Projects", out var projects) || projects.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The schema-1 workspace is missing its Projects array.");
        if (!TryGet(root, "UpdatedUtc", out var updated) || updated.ValueKind != JsonValueKind.String || !updated.TryGetDateTime(out _)) throw new InvalidDataException("The schema-1 workspace is missing a valid UpdatedUtc timestamp.");
    }

    private static bool IsRecognizableLegacyProject(JsonElement root)
    {
        if (!TryGet(root, "PageOne", out var pageOne) || pageOne.ValueKind != JsonValueKind.Object) return false;
        return TryGet(root, "Opponent", out _) || TryGet(root, "GameDate", out _) || TryGet(root, "Venue", out _) || TryGet(root, "Players", out _) || TryGet(root, "Schedule", out _) || TryGet(root, "Imports", out _);
    }

    private static void RefreshSourceHealth(BuilderWorkspace workspace)
    {
        var families = workspace.SourceFamilies.ToDictionary(x => x.Id);
        foreach (var project in workspace.Projects)
            foreach (var expected in project.ExpectedDocuments)
                expected.RefreshStatus(families.GetValueOrDefault(expected.SourceFamilyId));
    }
}

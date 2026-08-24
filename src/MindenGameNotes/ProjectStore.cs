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
                if (!schema.TryGetInt32(out var version) || version is not (1 or BuilderWorkspace.CurrentSchemaVersion))
                    throw new InvalidDataException($"Unsupported workspace schema version '{schema}'; expected 1 or {BuilderWorkspace.CurrentSchemaVersion}. The file was not changed.");
                ValidateCurrentWorkspace(root);
                if (version == 1 && ContainsWp2Authority(root)) throw new InvalidDataException("A schema-1 workspace cannot contain WP 2 authority fields and was not changed.");
                if (version == BuilderWorkspace.CurrentSchemaVersion) ValidateNoExplicitNullWp2Collections(root);
                try { workspace = JsonSerializer.Deserialize<BuilderWorkspace>(json, JsonOptions) ?? throw new InvalidDataException("The workspace could not be read."); }
                catch (JsonException ex) { throw new InvalidDataException($"The schema-{version} workspace is invalid and was not changed: {path}", ex); }
                if (version == 1) workspace.SchemaVersion = BuilderWorkspace.CurrentSchemaVersion;
                else ValidatePersistedWp2IdentityBeforeNormalization(workspace);
            }
            else if (IsRecognizableLegacyProject(root))
            {
                workspace = MigrateLegacy(root, json);
            }
            else
            {
                throw new InvalidDataException($"The file is neither a supported workspace nor a recognizable legacy project and was not changed: {path}");
            }
            try { workspace.Normalize(); }
            catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
            {
                throw new InvalidDataException($"The workspace structure is incomplete or invalid and was not changed: {path}", ex);
            }
            ValidateWp2Integrity(workspace);
            RefreshSourceHealth(workspace);
            return workspace;
        }
    }

    public async Task SaveAsync(BuilderWorkspace workspace)
    {
        workspace.UpdatedUtc = DateTime.UtcNow;
        ValidatePersistedWp2IdentityBeforeNormalization(workspace);
        workspace.Normalize();
        ValidateWp2Integrity(workspace);
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
        if (!TryGet(root, "ActiveProjectId", out var active) || active.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException("The workspace is missing a valid ActiveProjectId.");
        if (!TryGet(root, "SourceFamilies", out var families) || families.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The workspace is missing its SourceFamilies array.");
        if (!TryGet(root, "Projects", out var projects) || projects.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The workspace is missing its Projects array.");
        if (!TryGet(root, "UpdatedUtc", out var updated) || updated.ValueKind != JsonValueKind.String || !updated.TryGetDateTime(out _)) throw new InvalidDataException("The workspace is missing a valid UpdatedUtc timestamp.");
    }

    private static bool ContainsWp2Authority(JsonElement root)
    {
        if (!TryGet(root, "Projects", out var projects) || projects.ValueKind != JsonValueKind.Array) return false;
        foreach (var project in projects.EnumerateArray())
            if (project.ValueKind == JsonValueKind.Object && (TryGet(project, "StagedGameReports", out _) || TryGet(project, "CompletedGames", out _) || TryGet(project, "CurrentAcceptedGameId", out _))) return true;
        return false;
    }

    private static void ValidateNoExplicitNullWp2Collections(JsonElement root)
    {
        if (!TryGet(root, "Projects", out var projects) || projects.ValueKind != JsonValueKind.Array) return;
        var nestedNames = new[] { "PeriodScores", "ScoringPlays", "TeamStatistics", "Rushing", "Passing", "Receiving", "Issues", "AcceptedIssues", "Corrections" };
        foreach (var project in projects.EnumerateArray())
        {
            foreach (var topName in new[] { "StagedGameReports", "CompletedGames" })
            {
                if (!TryGet(project, topName, out var collection)) continue;
                if (collection.ValueKind == JsonValueKind.Null) throw new InvalidDataException($"{topName} cannot be null in a schema-2 workspace.");
                if (collection.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in collection.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object)
                        foreach (var name in nestedNames)
                            if (TryGet(item, name, out var nested) && nested.ValueKind == JsonValueKind.Null) throw new InvalidDataException($"{name} cannot be null in persisted WP 2 authority.");
            }
        }
    }

    private static void ValidateWp2Integrity(BuilderWorkspace workspace)
    {
        if (workspace.SourceFamilies.Any(x => x is null) || workspace.Projects.Any(x => x is null)) throw new InvalidDataException("Workspace collections contain null entries.");
        RequireUnique(workspace.SourceFamilies.Select(x => x.Id), "source-family"); RequireUnique(workspace.Projects.Select(x => x.Id), "project");
        var familyIds = workspace.SourceFamilies.Select(x => x.Id).ToHashSet();
        foreach (var project in workspace.Projects)
        {
            if (project.ExpectedDocuments.Any(x => x is null) || project.Imports.Any(x => x is null) || project.StagedGameReports.Any(x => x is null) || project.CompletedGames.Any(x => x is null)) throw new InvalidDataException("Project collections contain null entries.");
            RequireUnique(project.ExpectedDocuments.Select(x => x.Id), "expected-document"); RequireUnique(project.Imports.Select(x => x.Id), "import"); RequireUnique(project.StagedGameReports.Select(x => x.Id), "staged-report"); RequireUnique(project.CompletedGames.Select(x => x.Id), "completed-game");
            var documents = project.ExpectedDocuments.ToDictionary(x => x.Id); var imports = project.Imports.ToDictionary(x => x.Id); var stagedReports = project.StagedGameReports.ToDictionary(x => x.Id); var games = project.CompletedGames.ToDictionary(x => x.Id);
            foreach (var staged in project.StagedGameReports)
            {
                ValidateNested(staged.PeriodScores, staged.ScoringPlays, staged.TeamStatistics, staged.Rushing, staged.Passing, staged.Receiving, staged.Issues, staged.Corrections, "staged report");
                if (staged.Id == Guid.Empty || staged.ProjectId != project.Id) throw new InvalidDataException("A staged report has invalid project ownership.");
                if (!documents.TryGetValue(staged.ExpectedDocumentId, out var document) || document.SourceFamilyId != staged.SourceFamilyId || !familyIds.Contains(staged.SourceFamilyId)) throw new InvalidDataException("A staged report has orphaned expected-document or source-family provenance.");
                if (!imports.TryGetValue(staged.ImportRecordId, out var import) || import.ProjectId != project.Id || import.ExpectedDocumentId != staged.ExpectedDocumentId || import.SourceFamilyId != staged.SourceFamilyId) throw new InvalidDataException("A staged report has missing or inconsistent import provenance.");
                var related = project.CompletedGames.Where(x => x.StagedReportId == staged.Id).ToList();
                if (staged.State == ReportReviewState.Accepted && related.Count != 1) throw new InvalidDataException("An accepted staged report must have exactly one completed game.");
                if (staged.State != ReportReviewState.Accepted && related.Count != 0) throw new InvalidDataException("Pending or rejected staging cannot establish completed-game authority.");
            }
            foreach (var game in project.CompletedGames)
            {
                ValidateNested(game.PeriodScores, game.ScoringPlays, game.TeamStatistics, game.Rushing, game.Passing, game.Receiving, game.AcceptedIssues, game.Corrections, "completed game");
                if (game.Id == Guid.Empty || game.ProjectId != project.Id || game.GameDate == default || string.IsNullOrWhiteSpace(game.Opponent)) throw new InvalidDataException("A completed game has invalid identity or ownership.");
                if (!stagedReports.TryGetValue(game.StagedReportId, out var staged) || staged.State != ReportReviewState.Accepted) throw new InvalidDataException("A completed game does not reference accepted staging.");
                if (!documents.TryGetValue(game.ExpectedDocumentId, out var document) || document.SourceFamilyId != game.SourceFamilyId || !familyIds.Contains(game.SourceFamilyId)) throw new InvalidDataException("A completed game has orphaned expected-document or source-family provenance.");
                if (!imports.TryGetValue(game.ImportRecordId, out var import) || import.ProjectId != project.Id || import.ExpectedDocumentId != game.ExpectedDocumentId || import.SourceFamilyId != game.SourceFamilyId) throw new InvalidDataException("A completed game has missing or inconsistent import provenance.");
                if (game.ExpectedDocumentId != staged.ExpectedDocumentId || game.SourceFamilyId != staged.SourceFamilyId || game.ImportRecordId != staged.ImportRecordId) throw new InvalidDataException("Completed-game provenance contradicts its staged report.");
            }
            foreach (var group in project.CompletedGames.GroupBy(x => x.GameDate.Date))
            {
                if (group.Count(x => x.IsCurrentAuthority) != 1) throw new InvalidDataException("Each accepted Minden game date must have exactly one current authority.");
                var opponents = group.Select(x => GameInformationWorkflow.NormalizeOpponent(x.Opponent)).Distinct(StringComparer.Ordinal).ToList(); if (opponents.Count != 1) throw new InvalidDataException("Completed games on the same date have contradictory opponent identities.");
            }
            if (project.CurrentAcceptedGameId is Guid currentId)
            {
                if (!games.TryGetValue(currentId, out var current) || !current.IsCurrentAuthority) throw new InvalidDataException("CurrentAcceptedGameId does not reference a current completed game.");
            }
            else if (project.CompletedGames.Count != 0) throw new InvalidDataException("Completed-game authority exists without CurrentAcceptedGameId.");
        }
    }

    private static void ValidatePersistedWp2IdentityBeforeNormalization(BuilderWorkspace workspace)
    {
        foreach (var project in workspace.Projects ?? [])
        {
            if (project is null || ((project.StagedGameReports?.Count ?? 0) == 0 && (project.CompletedGames?.Count ?? 0) == 0)) continue;
            if (project.Id == Guid.Empty) throw new InvalidDataException("Persisted WP 2 authority has an empty project ID and was not changed.");
            foreach (var staged in project.StagedGameReports ?? [])
                if (staged is null || staged.Id == Guid.Empty || staged.ProjectId == Guid.Empty || staged.ExpectedDocumentId == Guid.Empty || staged.SourceFamilyId == Guid.Empty || staged.ImportRecordId == Guid.Empty)
                    throw new InvalidDataException("A persisted staged report has an empty authority or provenance ID and was not changed.");
            foreach (var game in project.CompletedGames ?? [])
                if (game is null || game.Id == Guid.Empty || game.ProjectId == Guid.Empty || game.StagedReportId == Guid.Empty || game.ExpectedDocumentId == Guid.Empty || game.SourceFamilyId == Guid.Empty || game.ImportRecordId == Guid.Empty)
                    throw new InvalidDataException("A persisted completed game has an empty authority or provenance ID and was not changed.");
            var expectedDocumentIds = (project.StagedGameReports ?? []).Select(x => x.ExpectedDocumentId).Concat((project.CompletedGames ?? []).Select(x => x.ExpectedDocumentId)).ToHashSet();
            var importIds = (project.StagedGameReports ?? []).Select(x => x.ImportRecordId).Concat((project.CompletedGames ?? []).Select(x => x.ImportRecordId)).ToHashSet();
            foreach (var document in (project.ExpectedDocuments ?? []).Where(x => x is not null && expectedDocumentIds.Contains(x.Id)))
                if (document is null || document.Id == Guid.Empty || document.SourceFamilyId == Guid.Empty)
                    throw new InvalidDataException("A persisted expected document used by WP 2 authority has an empty identity or source-family ID and was not changed.");
            foreach (var import in (project.Imports ?? []).Where(x => x is not null && importIds.Contains(x.Id)))
                if (import is null || import.Id == Guid.Empty || import.ProjectId == Guid.Empty || import.ExpectedDocumentId == Guid.Empty || import.SourceFamilyId == Guid.Empty)
                    throw new InvalidDataException("A persisted import used by WP 2 authority has an empty identity, ownership, or provenance ID and was not changed.");
        }
    }

    private static void RequireUnique(IEnumerable<Guid> ids, string name)
    {
        var values = ids.ToList(); if (values.Count != values.Distinct().Count()) throw new InvalidDataException($"Duplicate {name} IDs are not allowed.");
    }

    private static void ValidateNested(List<PeriodScore> periods, List<ScoringPlay> plays, List<TeamGameStatistic> stats, List<RushingPerformance> rushing, List<PassingPerformance> passing, List<ReceivingPerformance> receiving, List<InformationValidationIssue> issues, List<StagedCorrection> corrections, string owner)
    {
        if (periods is null || plays is null || stats is null || rushing is null || passing is null || receiving is null || issues is null || corrections is null || periods.Any(x => x is null) || plays.Any(x => x is null) || stats.Any(x => x is null || x.Minden is null || x.Opponent is null) || rushing.Any(x => x is null) || passing.Any(x => x is null) || receiving.Any(x => x is null) || issues.Any(x => x is null) || corrections.Any(x => x is null)) throw new InvalidDataException($"A {owner} contains null required nested information.");
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

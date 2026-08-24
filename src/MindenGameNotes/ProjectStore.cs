using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
                if (!schema.TryGetInt32(out var version) || version is not (1 or 2 or 3 or BuilderWorkspace.CurrentSchemaVersion))
                    throw new InvalidDataException($"Unsupported workspace schema version '{schema}'; expected 1 through {BuilderWorkspace.CurrentSchemaVersion}. The file was not changed.");
                ValidateCurrentWorkspace(root);
                if (version == 1 && ContainsWp2Authority(root)) throw new InvalidDataException("A schema-1 workspace cannot contain WP 2 authority fields and was not changed.");
                if (version < 3 && ContainsWp3Authority(root)) throw new InvalidDataException($"A schema-{version} workspace cannot contain WP 3 authority fields and was not changed.");
                if (version < 4 && ContainsWp4Authority(root)) throw new InvalidDataException($"A schema-{version} workspace cannot contain WP 4 authority fields and was not changed.");
                if (version >= 2) ValidateNoExplicitNullWp2Collections(root);
                if (version >= 3) ValidateRequiredWp3Shape(root);
                if (version == BuilderWorkspace.CurrentSchemaVersion) ValidateRequiredWp4Shape(root);
                try { workspace = JsonSerializer.Deserialize<BuilderWorkspace>(json, JsonOptions) ?? throw new InvalidDataException("The workspace could not be read."); }
                catch (JsonException ex) { throw new InvalidDataException($"The schema-{version} workspace is invalid and was not changed: {path}", ex); }
                if (version >= 2) ValidatePersistedWp2IdentityBeforeNormalization(workspace);
                if (version >= 3) ValidatePersistedWp3IdentityBeforeNormalization(workspace);
                if (version == 4) ValidatePersistedWp4IdentityBeforeNormalization(workspace);
                if (version < BuilderWorkspace.CurrentSchemaVersion) workspace.SchemaVersion = BuilderWorkspace.CurrentSchemaVersion;
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
            ValidateWp3Integrity(workspace);
            ValidateWp4Integrity(workspace);
            RefreshSourceHealth(workspace);
            return workspace;
        }
    }

    public async Task SaveAsync(BuilderWorkspace workspace)
    {
        workspace.UpdatedUtc = DateTime.UtcNow;
        ValidatePersistedWp2IdentityBeforeNormalization(workspace);
        ValidatePersistedWp3IdentityBeforeNormalization(workspace);
        ValidatePersistedWp4IdentityBeforeNormalization(workspace);
        workspace.Normalize();
        ValidateWp2Integrity(workspace);
        ValidateWp3Integrity(workspace);
        ValidateWp4Integrity(workspace);
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

    private static bool ContainsWp3Authority(JsonElement root)
    {
        if (!TryGet(root, "Projects", out var projects) || projects.ValueKind != JsonValueKind.Array) return false;
        foreach (var project in projects.EnumerateArray())
            if (project.ValueKind == JsonValueKind.Object && (TryGet(project, "StagedDefensiveWorkbooks", out _) || TryGet(project, "AcceptedDefensiveGames", out _) || TryGet(project, "AcceptedDefensiveSeasonTotals", out _))) return true;
        return false;
    }

    private static bool ContainsWp4Authority(JsonElement root)
    {
        if (!TryGet(root, "Projects", out var projects) || projects.ValueKind != JsonValueKind.Array) return false;
        return projects.EnumerateArray().Any(project => project.ValueKind == JsonValueKind.Object && (TryGet(project, "StagedSupplementalSections", out _) || TryGet(project, "AcceptedSupplementalSections", out _) || TryGet(project, "DefensiveSeasonTotalsAuthorityId", out _)));
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

    private static void ValidateRequiredWp3Shape(JsonElement root)
    {
        if (!TryGet(root, "Projects", out var projects) || projects.ValueKind != JsonValueKind.Array) return;
        foreach (var project in projects.EnumerateArray())
        {
            var staged = Required(project, "StagedDefensiveWorkbooks", JsonValueKind.Array, "schema-3 project");
            var acceptedGames = Required(project, "AcceptedDefensiveGames", JsonValueKind.Array, "schema-3 project");
            var acceptedTotals = Required(project, "AcceptedDefensiveSeasonTotals", JsonValueKind.Array, "schema-3 project");
            foreach (var workbook in staged.EnumerateArray())
            {
                RequiredMembers(workbook, "staged defensive workbook", "Id", "ProjectId", "ExpectedDocumentId", "SourceFamilyId", "ImportRecordId", "ParsedUtc", "Games", "SeasonTotals", "Issues");
                var games = Required(workbook, "Games", JsonValueKind.Array, "staged defensive workbook"); Required(workbook, "Issues", JsonValueKind.Array, "staged defensive workbook");
                foreach (var game in games.EnumerateArray()) ValidateGameShape(game, "staged defensive game", accepted: false);
                var totals = Member(workbook, "SeasonTotals", "staged defensive workbook"); if (totals.ValueKind == JsonValueKind.Object) ValidateTotalsShape(totals, "staged defensive totals", accepted: false); else if (totals.ValueKind != JsonValueKind.Null) throw new InvalidDataException("SeasonTotals must be an object or null in persisted WP 3 staging.");
            }
            foreach (var game in acceptedGames.EnumerateArray()) ValidateGameShape(game, "accepted defensive game", accepted: true);
            foreach (var totals in acceptedTotals.EnumerateArray()) ValidateTotalsShape(totals, "accepted defensive totals", accepted: true);
        }
    }

    private static void ValidateRequiredWp4Shape(JsonElement root)
    {
        var projects = Required(root, "Projects", JsonValueKind.Array, "schema-4 workspace");
        foreach (var project in projects.EnumerateArray())
        {
            var staged = Required(project, "StagedSupplementalSections", JsonValueKind.Array, "schema-4 project");
            var accepted = Required(project, "AcceptedSupplementalSections", JsonValueKind.Array, "schema-4 project");
            var defensiveSelection = Member(project, "DefensiveSeasonTotalsAuthorityId", "schema-4 project"); if (defensiveSelection.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException("DefensiveSeasonTotalsAuthorityId must be a GUID or null in schema-4 persistence.");
            foreach (var section in staged.EnumerateArray()) ValidateSupplementalShape(section, false);
            foreach (var section in accepted.EnumerateArray()) ValidateSupplementalShape(section, true);
        }
    }

    private static void ValidateSupplementalShape(JsonElement section, bool accepted)
    {
        var owner = accepted ? "accepted supplemental section" : "staged supplemental section";
        if (accepted) RequiredMembers(section, owner, "Id", "ProjectId", "StagedSectionId", "Kind", "Season", "Week", "BaselineThroughSeason", "Payload", "Evidence", "AcceptedIssues", "AcceptanceNote", "AcceptedUtc", "IsCurrentAuthority");
        else RequiredMembers(section, owner, "Id", "ProjectId", "Kind", "Season", "Week", "BaselineThroughSeason", "Payload", "Evidence", "Issues", "State", "ReviewNote", "ParsedUtc", "ReviewedUtc", "AcceptedUtc");
        var payload = Required(section, "Payload", JsonValueKind.Object, owner); var discriminator = Required(payload, "$type", JsonValueKind.String, owner).GetString();
        string[] members = discriminator switch
        {
            "page1" => ["MindenRecord", "OpponentRecord", "Weather", "OpponentFacts", "SeriesHistory", "WinImplications", "StatsOfWeek", "ByTheNumbers", "PriorSeasonSummary", "SeriesExtremes", "Storyline"],
            "schedule" => ["TeamOrGroup", "Games"], "ranking" => ["Title", "SourceDate", "Entries", "SourceFooter"], "individual" => ["ProductionLabel", "StatisticalSeason", "Tables"],
            "playerOfGame" => ["Entries"], "coaching" or "program" => ["BaselineThroughSeason", "Sections"], "teamStats" => ["StatisticalSeason", "ReportLabel", "Rows"],
            "nerdNotes" => ["EditorialDirection", "Items"], "roster" => ["Team", "Season", "Players"], _ => throw new InvalidDataException($"Unknown supplemental payload discriminator '{discriminator}'.")
        };
        RequiredMembers(payload, $"{owner} payload", members);
        ValidateSupplementalPayloadShape(payload, discriminator!, owner);
        var evidence = Required(section, "Evidence", JsonValueKind.Array, owner); if (evidence.GetArrayLength() == 0) throw new InvalidDataException($"A persisted {owner} must contain evidence.");
        foreach (var item in evidence.EnumerateArray()) { RequiredMembers(item, $"{owner} evidence", "Id", "Kind", "ExpectedDocumentId", "SourceFamilyId", "ImportRecordId", "AuthorityName", "SourceLocator", "SourceAsOfUtc", "ApplicableSeason", "ApplicableWeek", "Note"); Required(item, "Id", JsonValueKind.String, owner); Required(item, "Kind", JsonValueKind.String, owner); Required(item, "AuthorityName", JsonValueKind.String, owner); Required(item, "SourceLocator", JsonValueKind.String, owner); Required(item, "Note", JsonValueKind.String, owner); }
        var issues = Required(section, accepted ? "AcceptedIssues" : "Issues", JsonValueKind.Array, owner); foreach (var issue in issues.EnumerateArray()) ValidateIssueShape(issue, owner);
    }

    private static void ValidateSupplementalPayloadShape(JsonElement payload, string discriminator, string owner)
    {
        void Text(JsonElement x, string name) => Required(x, name, JsonValueKind.String, owner);
        void Number(JsonElement x, string name) => Required(x, name, JsonValueKind.Number, owner);
        void Sourced(JsonElement x) { RequiredMembers(x, owner, "Value", "EvidenceId"); Text(x, "Value"); Required(x, "EvidenceId", JsonValueKind.String, owner); }
        void Row(JsonElement x) { RequiredMembers(x, owner, "Label", "Values", "EvidenceId"); Text(x, "Label"); var values = Required(x, "Values", JsonValueKind.Array, owner); foreach (var value in values.EnumerateArray()) if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Persisted {owner} row values must be strings."); Required(x, "EvidenceId", JsonValueKind.String, owner); }
        void Rows(JsonElement x, string name) { var rows = Required(x, name, JsonValueKind.Array, owner); foreach (var row in rows.EnumerateArray()) Row(row); }
        switch (discriminator)
        {
            case "page1":
                Sourced(Required(payload, "MindenRecord", JsonValueKind.Object, owner)); Sourced(Required(payload, "OpponentRecord", JsonValueKind.Object, owner));
                var weather = Required(payload, "Weather", JsonValueKind.Object, owner); RequiredMembers(weather, owner, "Temperature", "Sky", "Wind", "EvidenceId"); Text(weather, "Temperature"); Text(weather, "Sky"); Text(weather, "Wind"); Required(weather, "EvidenceId", JsonValueKind.String, owner);
                var facts = Required(payload, "OpponentFacts", JsonValueKind.Object, owner); Rows(facts, "Rows");
                foreach (var name in new[] { "SeriesHistory", "WinImplications", "StatsOfWeek", "ByTheNumbers" }) { var list = Required(payload, name, JsonValueKind.Array, owner); foreach (var item in list.EnumerateArray()) Sourced(item); }
                Sourced(Required(payload, "PriorSeasonSummary", JsonValueKind.Object, owner)); Sourced(Required(payload, "SeriesExtremes", JsonValueKind.Object, owner)); Sourced(Required(payload, "Storyline", JsonValueKind.Object, owner)); break;
            case "schedule": Text(payload, "TeamOrGroup"); foreach (var item in Required(payload, "Games", JsonValueKind.Array, owner).EnumerateArray()) { RequiredMembers(item, owner, "Date", "Opponent", "Site", "ResultOrTime", "IsDistrictGame", "EvidenceId"); Required(item, "Date", JsonValueKind.String, owner); Text(item, "Opponent"); Text(item, "Site"); Text(item, "ResultOrTime"); Required(item, "EvidenceId", JsonValueKind.String, owner); } break;
            case "ranking": Text(payload, "Title"); Required(payload, "SourceDate", JsonValueKind.String, owner); Text(payload, "SourceFooter"); foreach (var item in Required(payload, "Entries", JsonValueKind.Array, owner).EnumerateArray()) { RequiredMembers(item, owner, "Rank", "Team", "Record", "Value", "EvidenceId"); Number(item, "Rank"); Text(item, "Team"); Text(item, "Record"); Text(item, "Value"); Required(item, "EvidenceId", JsonValueKind.String, owner); } break;
            case "individual": Text(payload, "ProductionLabel"); Number(payload, "StatisticalSeason"); foreach (var table in Required(payload, "Tables", JsonValueKind.Array, owner).EnumerateArray()) { RequiredMembers(table, owner, "Title", "Columns", "Rows"); Text(table, "Title"); var columns = Required(table, "Columns", JsonValueKind.Array, owner); foreach (var column in columns.EnumerateArray()) if (column.ValueKind != JsonValueKind.String) throw new InvalidDataException("Persisted stat headings must be strings."); Rows(table, "Rows"); } break;
            case "playerOfGame": foreach (var item in Required(payload, "Entries", JsonValueKind.Array, owner).EnumerateArray()) { RequiredMembers(item, owner, "Week", "Player", "Description", "EvidenceId"); Number(item, "Week"); Text(item, "Player"); Text(item, "Description"); Required(item, "EvidenceId", JsonValueKind.String, owner); } break;
            case "coaching": case "program": Number(payload, "BaselineThroughSeason"); foreach (var section in Required(payload, "Sections", JsonValueKind.Array, owner).EnumerateArray()) { RequiredMembers(section, owner, "Title", "Rows"); Text(section, "Title"); Rows(section, "Rows"); } break;
            case "teamStats": Number(payload, "StatisticalSeason"); Text(payload, "ReportLabel"); foreach (var item in Required(payload, "Rows", JsonValueKind.Array, owner).EnumerateArray()) { RequiredMembers(item, owner, "Label", "Minden", "Opponent", "EvidenceId"); Text(item, "Label"); Text(item, "Minden"); Text(item, "Opponent"); Required(item, "EvidenceId", JsonValueKind.String, owner); } break;
            case "nerdNotes": Text(payload, "EditorialDirection"); foreach (var item in Required(payload, "Items", JsonValueKind.Array, owner).EnumerateArray()) { RequiredMembers(item, owner, "Title", "Content", "Disposition", "EvidenceIds", "Verified", "Note"); Text(item, "Title"); Text(item, "Content"); Required(item, "Disposition", JsonValueKind.String, owner); Required(item, "EvidenceIds", JsonValueKind.Array, owner); Text(item, "Note"); } break;
            case "roster": Text(payload, "Team"); Number(payload, "Season"); foreach (var item in Required(payload, "Players", JsonValueKind.Array, owner).EnumerateArray()) { RequiredMembers(item, owner, "SourceName", "DisplayName", "Number", "Position", "Grade", "EvidenceId"); Text(item, "SourceName"); Text(item, "DisplayName"); Text(item, "Number"); Text(item, "Position"); Text(item, "Grade"); Required(item, "EvidenceId", JsonValueKind.String, owner); } break;
        }
    }

    private static void ValidateGameShape(JsonElement game, string owner, bool accepted)
    {
        if (accepted) RequiredMembers(game, owner, "Id", "ProjectId", "StagedWorkbookId", "StagedSectionId", "ExpectedDocumentId", "SourceFamilyId", "ImportRecordId", "Season", "Week", "Opponent", "SiteIndicator", "Players", "AcceptedIssues", "AcceptanceNote", "AcceptedUtc", "IsCurrentAuthority");
        else RequiredMembers(game, owner, "Id", "Season", "Week", "Opponent", "SiteIndicator", "WorksheetName", "IdentityText", "State", "ReviewedUtc", "AcceptedUtc", "ReviewNote", "Players", "Issues");
        var players = Required(game, "Players", JsonValueKind.Array, owner); var issues = Required(game, accepted ? "AcceptedIssues" : "Issues", JsonValueKind.Array, owner);
        foreach (var player in players.EnumerateArray()) ValidatePlayerShape(player, owner); foreach (var issue in issues.EnumerateArray()) ValidateIssueShape(issue, owner);
    }

    private static void ValidateTotalsShape(JsonElement totals, string owner, bool accepted)
    {
        if (accepted) RequiredMembers(totals, owner, "Id", "ProjectId", "StagedWorkbookId", "StagedSectionId", "ExpectedDocumentId", "SourceFamilyId", "ImportRecordId", "Season", "Players", "AcceptedIssues", "AcceptanceNote", "AcceptedUtc", "IsCurrentAuthority");
        else RequiredMembers(totals, owner, "Id", "Season", "WorksheetName", "IdentityText", "State", "ReviewedUtc", "AcceptedUtc", "ReviewNote", "Players", "Issues");
        var players = Required(totals, "Players", JsonValueKind.Array, owner); var issues = Required(totals, accepted ? "AcceptedIssues" : "Issues", JsonValueKind.Array, owner);
        foreach (var player in players.EnumerateArray()) ValidatePlayerShape(player, owner); foreach (var issue in issues.EnumerateArray()) ValidateIssueShape(issue, owner);
    }

    private static void ValidatePlayerShape(JsonElement player, string owner)
    {
        RequiredMembers(player, owner, "PlayerName", "JerseyNumber", "WorksheetName", "SourceRow", "Solo", "Assisted", "Total", "TacklesForLoss", "Sacks", "QuarterbackHurries", "PassBreakups", "Interceptions", "ForcedFumbles", "FumbleRecoveries", "BlockedExtraPoints", "BlockedKicks");
        foreach (var name in new[] { "Solo", "Assisted", "Total", "TacklesForLoss", "Sacks", "QuarterbackHurries", "PassBreakups", "Interceptions", "ForcedFumbles", "FumbleRecoveries", "BlockedExtraPoints", "BlockedKicks" })
        {
            var value = Required(player, name, JsonValueKind.Object, owner); RequiredMembers(value, $"{owner} {name}", "State", "CellReference", "Raw", "Numeric", "Formula");
            Required(value, "State", JsonValueKind.String, owner); Required(value, "CellReference", JsonValueKind.String, owner); Required(value, "Raw", JsonValueKind.String, owner);
            var numeric = Member(value, "Numeric", owner); if (numeric.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null)) throw new InvalidDataException($"Numeric must be a number or null in persisted {owner}.");
            var formula = Member(value, "Formula", owner); if (formula.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException($"Formula must be a string or null in persisted {owner}.");
        }
    }

    private static void ValidateIssueShape(JsonElement issue, string owner) => RequiredMembers(issue, owner, "Severity", "Code", "Section", "Message");
    private static void RequiredMembers(JsonElement element, string owner, params string[] names) { if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"A {owner} must be an object."); foreach (var name in names) Member(element, name, owner); }
    private static JsonElement Member(JsonElement element, string name, string owner) { if (!TryGet(element, name, out var value)) throw new InvalidDataException($"Required member {name} is missing from persisted {owner}."); return value; }
    private static JsonElement Required(JsonElement element, string name, JsonValueKind kind, string owner) { var value = Member(element, name, owner); if (value.ValueKind != kind) throw new InvalidDataException($"Required member {name} in persisted {owner} must be {kind} and cannot be null."); return value; }

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

    private static void ValidateWp3Integrity(BuilderWorkspace workspace)
    {
        var familyIds = workspace.SourceFamilies.Select(x => x.Id).ToHashSet();
        foreach (var project in workspace.Projects)
        {
            if (project.StagedDefensiveWorkbooks.Any(x => x is null) || project.AcceptedDefensiveGames.Any(x => x is null) || project.AcceptedDefensiveSeasonTotals.Any(x => x is null)) throw new InvalidDataException("WP 3 collections contain null entries.");
            RequireUnique(project.StagedDefensiveWorkbooks.Select(x => x.Id), "staged defensive workbook"); RequireUnique(project.AcceptedDefensiveGames.Select(x => x.Id), "accepted defensive game"); RequireUnique(project.AcceptedDefensiveSeasonTotals.Select(x => x.Id), "accepted defensive season totals");
            var documents = project.ExpectedDocuments.ToDictionary(x => x.Id); var imports = project.Imports.ToDictionary(x => x.Id); var workbooks = project.StagedDefensiveWorkbooks.ToDictionary(x => x.Id);
            foreach (var workbook in project.StagedDefensiveWorkbooks)
            {
                if (workbook.Id == Guid.Empty || workbook.ProjectId != project.Id || workbook.Games is null || workbook.Issues is null || workbook.Games.Any(x => x is null) || workbook.Issues.Any(x => x is null)) throw new InvalidDataException("A staged defensive workbook has invalid identity, ownership, or nested collections.");
                if (!documents.TryGetValue(workbook.ExpectedDocumentId, out var document) || document.SourceFamilyId != workbook.SourceFamilyId || !familyIds.Contains(workbook.SourceFamilyId)) throw new InvalidDataException("A staged defensive workbook has orphaned source provenance.");
                if (!imports.TryGetValue(workbook.ImportRecordId, out var import) || import.ProjectId != project.Id || import.ExpectedDocumentId != workbook.ExpectedDocumentId || import.SourceFamilyId != workbook.SourceFamilyId || import.Kind != "XLSX-DEFENSIVE") throw new InvalidDataException("A staged defensive workbook has missing or inconsistent import provenance.");
                var sectionSeasons = workbook.Games.Select(x => x.Season).Concat(workbook.SeasonTotals is null ? [] : new[] { workbook.SeasonTotals.Season }).Where(x => x is not null).Distinct().ToList(); if (sectionSeasons.Any(x => x != import.ApplicableSeason)) throw new InvalidDataException("A staged defensive section season contradicts its import provenance.");
                RequireUnique(workbook.Games.Select(x => x.Id).Concat(workbook.SeasonTotals is null ? [] : new[] { workbook.SeasonTotals.Id }), "staged defensive section");
                foreach (var game in workbook.Games)
                {
                    ValidateDefensiveSection(game.Players, game.Issues, "staged defensive game");
                    var related = project.AcceptedDefensiveGames.Where(x => x.StagedWorkbookId == workbook.Id && x.StagedSectionId == game.Id).ToList();
                    if (game.State == ReportReviewState.Accepted && related.Count != 1) throw new InvalidDataException("An accepted defensive game section must have exactly one accepted snapshot.");
                    if (game.State != ReportReviewState.Accepted && related.Count != 0) throw new InvalidDataException("Pending or rejected defensive game staging cannot establish authority.");
                }
                if (workbook.SeasonTotals is { } totals)
                {
                    ValidateDefensiveSection(totals.Players, totals.Issues, "staged defensive totals");
                    if (totals.Season is not null && !TotalsIdentityMatches(totals.IdentityText, totals.Season.Value)) throw new InvalidDataException("Staged defensive totals season contradicts its source identity.");
                    var related = project.AcceptedDefensiveSeasonTotals.Where(x => x.StagedWorkbookId == workbook.Id && x.StagedSectionId == totals.Id).ToList();
                    if (totals.State == ReportReviewState.Accepted && related.Count != 1) throw new InvalidDataException("Accepted defensive season totals must have exactly one accepted snapshot.");
                    if (totals.State != ReportReviewState.Accepted && related.Count != 0) throw new InvalidDataException("Pending or rejected defensive totals staging cannot establish authority.");
                }
            }
            foreach (var game in project.AcceptedDefensiveGames)
            {
                ValidateDefensiveSection(game.Players, game.AcceptedIssues, "accepted defensive game");
                if (game.Id == Guid.Empty || game.ProjectId != project.Id || game.Season is < 1900 or > 2200 || game.Week <= 0 || string.IsNullOrWhiteSpace(game.Opponent) || project.Season != game.Season) throw new InvalidDataException("An accepted defensive game has invalid identity, season association, or ownership.");
                if (!workbooks.TryGetValue(game.StagedWorkbookId, out var workbook) || workbook.Games.SingleOrDefault(x => x.Id == game.StagedSectionId) is not { State: ReportReviewState.Accepted } staged) throw new InvalidDataException("An accepted defensive game does not reference accepted staging.");
                ValidateDefensiveProvenance(game.ProjectId, game.ExpectedDocumentId, game.SourceFamilyId, game.ImportRecordId, project, workbook);
                if (game.Season != staged.Season || game.Week != staged.Week || GameInformationWorkflow.NormalizeOpponent(game.Opponent) != GameInformationWorkflow.NormalizeOpponent(staged.Opponent)) throw new InvalidDataException("Accepted defensive game identity contradicts its staging snapshot.");
                if (!Equivalent(game.Players, staged.Players) || !Equivalent(game.AcceptedIssues, staged.Issues) || game.AcceptanceNote != staged.ReviewNote || game.AcceptedUtc != staged.AcceptedUtc) throw new InvalidDataException("Accepted defensive game content contradicts its immutable staging snapshot.");
            }
            foreach (var totals in project.AcceptedDefensiveSeasonTotals)
            {
                ValidateDefensiveSection(totals.Players, totals.AcceptedIssues, "accepted defensive season totals");
                if (totals.Id == Guid.Empty || totals.ProjectId != project.Id || totals.Season is < 1900 or > 2200 || project.Season != totals.Season) throw new InvalidDataException("Accepted defensive season totals have invalid identity, season association, or ownership.");
                if (!workbooks.TryGetValue(totals.StagedWorkbookId, out var workbook) || workbook.SeasonTotals is not { State: ReportReviewState.Accepted } staged || staged.Id != totals.StagedSectionId) throw new InvalidDataException("Accepted defensive totals do not reference accepted staging.");
                ValidateDefensiveProvenance(totals.ProjectId, totals.ExpectedDocumentId, totals.SourceFamilyId, totals.ImportRecordId, project, workbook);
                if (totals.Season != staged.Season) throw new InvalidDataException("Accepted defensive totals season contradicts staging.");
                if (!Equivalent(totals.Players, staged.Players) || !Equivalent(totals.AcceptedIssues, staged.Issues) || totals.AcceptanceNote != staged.ReviewNote || totals.AcceptedUtc != staged.AcceptedUtc) throw new InvalidDataException("Accepted defensive totals content contradicts its immutable staging snapshot.");
            }
            foreach (var group in project.AcceptedDefensiveGames.GroupBy(x => (x.Season, x.Week)))
            {
                if (group.Count(x => x.IsCurrentAuthority) != 1) throw new InvalidDataException("Each accepted defensive season/week must have exactly one current authority.");
                if (group.Select(x => GameInformationWorkflow.NormalizeOpponent(x.Opponent)).Distinct().Count() != 1) throw new InvalidDataException("Defensive authority history has conflicting opponents for one season/week.");
            }
            foreach (var group in project.AcceptedDefensiveSeasonTotals.GroupBy(x => x.Season)) if (group.Count(x => x.IsCurrentAuthority) != 1) throw new InvalidDataException("Each accepted defensive season-total history must have exactly one current authority.");
        }
    }

    private static void ValidateDefensiveProvenance(Guid projectId, Guid documentId, Guid familyId, Guid importId, GameNotesProject project, StagedDefensiveWorkbook workbook)
    {
        if (projectId != project.Id || documentId != workbook.ExpectedDocumentId || familyId != workbook.SourceFamilyId || importId != workbook.ImportRecordId) throw new InvalidDataException("Accepted defensive provenance contradicts staging.");
    }

    private static bool Equivalent<T>(T left, T right) => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);
    private static bool TotalsIdentityMatches(string identity, int season) { var match = Regex.Match(identity ?? "", @"^\s*(\d{4})\s*-\s*TOTALS\s*$", RegexOptions.IgnoreCase); return match.Success && int.TryParse(match.Groups[1].Value, out var parsed) && parsed == season; }

    private static void ValidateDefensiveSection(List<DefensiveStatLine> players, List<InformationValidationIssue> issues, string owner)
    {
        if (players is null || issues is null || players.Any(x => x is null) || issues.Any(x => x is null)) throw new InvalidDataException($"A {owner} contains null required information.");
        if (players.Count == 0) throw new InvalidDataException($"A {owner} must contain at least one defensive player row.");
        foreach (var player in players)
        {
            if (string.IsNullOrWhiteSpace(player.PlayerName) || player.SourceRow <= 0 || string.IsNullOrWhiteSpace(player.WorksheetName)) throw new InvalidDataException($"A {owner} contains an invalid player/source identity.");
            var values = new[] { player.Solo, player.Assisted, player.Total, player.TacklesForLoss, player.Sacks, player.QuarterbackHurries, player.PassBreakups, player.Interceptions, player.ForcedFumbles, player.FumbleRecoveries, player.BlockedExtraPoints, player.BlockedKicks };
            if (values.Any(x => x is null || x.State == DefensiveCellState.Numeric && x.Numeric is null || x.State != DefensiveCellState.Numeric && x.Numeric is not null)) throw new InvalidDataException($"A {owner} contains an inconsistent defensive source value.");
        }
    }

    private static void ValidatePersistedWp3IdentityBeforeNormalization(BuilderWorkspace workspace)
    {
        foreach (var project in workspace.Projects ?? [])
        {
            if (project is null || ((project.StagedDefensiveWorkbooks?.Count ?? 0) == 0 && (project.AcceptedDefensiveGames?.Count ?? 0) == 0 && (project.AcceptedDefensiveSeasonTotals?.Count ?? 0) == 0)) continue;
            if (project.Id == Guid.Empty) throw new InvalidDataException("Persisted WP 3 authority has an empty project ID and was not changed.");
            foreach (var workbook in project.StagedDefensiveWorkbooks ?? [])
            {
                if (workbook is null || workbook.Id == Guid.Empty || workbook.ProjectId == Guid.Empty || workbook.ExpectedDocumentId == Guid.Empty || workbook.SourceFamilyId == Guid.Empty || workbook.ImportRecordId == Guid.Empty) throw new InvalidDataException("Persisted defensive staging has an empty authority or provenance ID and was not changed.");
                if ((workbook.Games ?? []).Any(x => x is null || x.Id == Guid.Empty) || workbook.SeasonTotals is { Id: var id } && id == Guid.Empty) throw new InvalidDataException("Persisted defensive staging has an empty section ID and was not changed.");
            }
            foreach (var item in (project.AcceptedDefensiveGames ?? []).Select(x => (x?.Id ?? Guid.Empty, x?.ProjectId ?? Guid.Empty, x?.StagedWorkbookId ?? Guid.Empty, x?.StagedSectionId ?? Guid.Empty, x?.ExpectedDocumentId ?? Guid.Empty, x?.SourceFamilyId ?? Guid.Empty, x?.ImportRecordId ?? Guid.Empty)).Concat((project.AcceptedDefensiveSeasonTotals ?? []).Select(x => (x?.Id ?? Guid.Empty, x?.ProjectId ?? Guid.Empty, x?.StagedWorkbookId ?? Guid.Empty, x?.StagedSectionId ?? Guid.Empty, x?.ExpectedDocumentId ?? Guid.Empty, x?.SourceFamilyId ?? Guid.Empty, x?.ImportRecordId ?? Guid.Empty))))
                if (item.Item1 == Guid.Empty || item.Item2 == Guid.Empty || item.Item3 == Guid.Empty || item.Item4 == Guid.Empty || item.Item5 == Guid.Empty || item.Item6 == Guid.Empty || item.Item7 == Guid.Empty) throw new InvalidDataException("Persisted accepted defensive authority has an empty identity or provenance ID and was not changed.");
        }
    }

    private static void ValidatePersistedWp4IdentityBeforeNormalization(BuilderWorkspace workspace)
    {
        foreach (var project in workspace.Projects ?? [])
        {
            if (project is null || ((project.StagedSupplementalSections?.Count ?? 0) == 0 && (project.AcceptedSupplementalSections?.Count ?? 0) == 0)) continue;
            if (project.Id == Guid.Empty) throw new InvalidDataException("Persisted WP 4 authority has an empty project ID and was not changed.");
            foreach (var staged in project.StagedSupplementalSections ?? [])
                if (staged is null || staged.Id == Guid.Empty || staged.ProjectId == Guid.Empty || staged.Payload is null || staged.Evidence is null || staged.Issues is null)
                    throw new InvalidDataException("Persisted supplemental staging has an empty identity or required content and was not changed.");
            foreach (var accepted in project.AcceptedSupplementalSections ?? [])
                if (accepted is null || accepted.Id == Guid.Empty || accepted.ProjectId == Guid.Empty || accepted.StagedSectionId == Guid.Empty || accepted.Payload is null || accepted.Evidence is null || accepted.AcceptedIssues is null)
                    throw new InvalidDataException("Persisted accepted supplemental authority has an empty identity or required content and was not changed.");
        }
    }

    private static void ValidateWp4Integrity(BuilderWorkspace workspace)
    {
        var families = workspace.SourceFamilies.ToDictionary(x => x.Id);
        var selectableDefensiveTotals = workspace.Projects.SelectMany(x => x.AcceptedDefensiveSeasonTotals).Where(x => x.IsCurrentAuthority).ToDictionary(x => x.Id);
        foreach (var project in workspace.Projects)
        {
            if (project.DefensiveSeasonTotalsAuthorityId is Guid selected && (!selectableDefensiveTotals.TryGetValue(selected, out var selectedTotals) || project.Season is null || selectedTotals.Season != (project.Week == 1 ? project.Season - 1 : project.Season))) throw new InvalidDataException("The WP 4 defensive TOTALS selection is missing, superseded, or inapplicable to the weekly project.");
            if (project.StagedSupplementalSections.Any(x => x is null) || project.AcceptedSupplementalSections.Any(x => x is null)) throw new InvalidDataException("WP 4 collections contain null entries.");
            RequireUnique(project.StagedSupplementalSections.Select(x => x.Id), "staged supplemental section"); RequireUnique(project.AcceptedSupplementalSections.Select(x => x.Id), "accepted supplemental section");
            var stagedById = project.StagedSupplementalSections.ToDictionary(x => x.Id);
            foreach (var staged in project.StagedSupplementalSections)
            {
                ValidateSupplementalContent(project, staged.Kind, staged.Season, staged.Week, staged.BaselineThroughSeason, staged.Payload, staged.Evidence, staged.Issues, families, authority: false);
                if (staged.ProjectId != project.Id) throw new InvalidDataException("Supplemental staging has invalid project ownership.");
                if (staged.State == ReportReviewState.Accepted && (staged.AcceptedUtc is null || staged.ReviewedUtc is null) || staged.State != ReportReviewState.Accepted && staged.AcceptedUtc is not null) throw new InvalidDataException("Supplemental staging has invalid acceptance metadata.");
                var related = project.AcceptedSupplementalSections.Where(x => x.StagedSectionId == staged.Id).ToList();
                if (staged.State == ReportReviewState.Accepted && related.Count != 1) throw new InvalidDataException("Accepted supplemental staging must have exactly one accepted snapshot.");
                if (staged.State != ReportReviewState.Accepted && related.Count != 0) throw new InvalidDataException("Pending or rejected supplemental staging cannot establish authority.");
            }
            foreach (var accepted in project.AcceptedSupplementalSections)
            {
                ValidateSupplementalContent(project, accepted.Kind, accepted.Season, accepted.Week, accepted.BaselineThroughSeason, accepted.Payload, accepted.Evidence, accepted.AcceptedIssues, families, authority: true);
                if (accepted.ProjectId != project.Id || accepted.AcceptedUtc == default || accepted.AcceptedIssues.Any(x => x.Severity == InformationIssueSeverity.Blocking) || (accepted.AcceptedIssues.Any(x => x.Severity == InformationIssueSeverity.Advisory) || accepted.Evidence.Any(x => x.Kind == SupplementalEvidenceKind.EditorialDecision)) && string.IsNullOrWhiteSpace(accepted.AcceptanceNote) || !stagedById.TryGetValue(accepted.StagedSectionId, out var staged) || staged.State != ReportReviewState.Accepted) throw new InvalidDataException("Accepted supplemental authority has invalid acceptance metadata or does not reference accepted staging.");
                if (accepted.Kind != staged.Kind || accepted.Season != staged.Season || accepted.Week != staged.Week || accepted.BaselineThroughSeason != staged.BaselineThroughSeason) throw new InvalidDataException("Accepted supplemental identity contradicts staging.");
                if (!Equivalent(accepted.Payload, staged.Payload) || !Equivalent(accepted.Evidence, staged.Evidence) || !Equivalent(accepted.AcceptedIssues, staged.Issues)) throw new InvalidDataException("Accepted supplemental factual payload, evidence, or accepted issue state contradicts staging.");
                if (accepted.AcceptedUtc != staged.AcceptedUtc || accepted.AcceptanceNote != staged.ReviewNote) throw new InvalidDataException("Accepted supplemental metadata contradicts the acceptance operation recorded on staging.");
            }
            foreach (var group in project.AcceptedSupplementalSections.GroupBy(x => (x.Kind, x.Season, x.Week, x.BaselineThroughSeason)))
                if (group.Count(x => x.IsCurrentAuthority) != 1) throw new InvalidDataException("Each supplemental authority history must have exactly one current snapshot.");
        }
    }

    private static void ValidateSupplementalContent(GameNotesProject project, SupplementalSectionKind kind, int season, int? week, int? baseline, SupplementalPayload payload, List<SupplementalEvidence> evidence, List<InformationValidationIssue> issues, IReadOnlyDictionary<Guid, SourceFamilyConfiguration> families, bool authority)
    {
        if (payload is null || evidence is null || issues is null || evidence.Count == 0 || evidence.Any(x => x is null) || issues.Any(x => x is null) || !SupplementalValidation.Matches(kind, payload) || SupplementalValidation.IsEmpty(payload)) throw new InvalidDataException("A supplemental section has invalid or empty required content.");
        if (season != project.Season || !SupplementalInformationWorkflow.IsSeasonAuthority(kind) && week != project.Week) throw new InvalidDataException("A supplemental section contradicts its project season/week.");
        RequireUnique(evidence.Select(x => x.Id), "supplemental evidence");
        var synthetic = new StagedSupplementalSection { ProjectId = project.Id, Kind = kind, Season = season, Week = week, BaselineThroughSeason = baseline, Payload = payload, Evidence = evidence };
        foreach (var item in evidence)
        {
            if (item.Id == Guid.Empty || item.ApplicableSeason != season || !SupplementalInformationWorkflow.IsSeasonAuthority(kind) && item.ApplicableWeek != week) throw new InvalidDataException("Supplemental evidence has invalid applicability.");
            if (item.Kind == SupplementalEvidenceKind.EditorialDecision) { if (!SupplementalValidation.AllowsEditorial(kind) || string.IsNullOrWhiteSpace(item.AuthorityName) || string.IsNullOrWhiteSpace(item.Note)) throw new InvalidDataException("Editorial evidence is not authorized for this section or lacks its named authority/note."); continue; }
            var document = project.ExpectedDocuments.FirstOrDefault(x => x.Id == item.ExpectedDocumentId); var import = project.Imports.FirstOrDefault(x => x.Id == item.ImportRecordId);
            if (document is null || item.SourceFamilyId is not Guid familyId || !families.TryGetValue(familyId, out var family) || document.SourceFamilyId != familyId || import is null || !SupplementalValidation.ImportMatches(import, item, synthetic, document, family)) throw new InvalidDataException("Supplemental evidence has orphaned, inapplicable, or inconsistent source provenance.");
        }
        if (SupplementalValidation.RequiresSource(kind) && !evidence.Any(x => x.Kind == SupplementalEvidenceKind.ExpectedSourceDocument) || kind == SupplementalSectionKind.NerdNotes && !evidence.Any(x => x.Kind == SupplementalEvidenceKind.EditorialDecision)) throw new InvalidDataException("Supplemental evidence does not satisfy the section's authority policy.");
        if (authority && SupplementalValidation.Validate(synthetic, project).Any(x => x.Severity == InformationIssueSeverity.Blocking)) throw new InvalidDataException("Accepted supplemental authority fails its typed content contract.");
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

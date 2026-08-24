using MindenGameNotes;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private static int assertions;

    [STAThread]
    private static async Task Main(string[] args)
    {
        var proofProject = CompleteProject();
        if (args.Contains("--proof")) { MakeProof(proofProject); return; }
        var root = Path.Combine(Path.GetTempPath(), $"minden-wp1-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        try
        {
            await PersistenceAndStatusTests(root);
            await SchemaValidationTests(root);
            await ReloadSourceHealthTests(root);
            await ReorderedJsonPersistenceTests(root);
            await ReconfigurationTests(root);
            await LegacyMigrationTests(root);
            await ImportTests(root);
            await Wp2SingleGameTests(root);
            await Wp2ImmediateSourceHealthTests(root);
            await Wp2PersistenceIntegrityTests(root);
            RenderingSmoke(root, proofProject);
            Console.WriteLine($"PASS: WP 2 smoke suite ({assertions} assertions)");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task PersistenceAndStatusTests(string root)
    {
        var sourceRoot = Path.Combine(root, "sources"); Directory.CreateDirectory(sourceRoot);
        var currentPath = Path.Combine(sourceRoot, "current.xlsx"); await File.WriteAllTextAsync(currentPath, "current");
        var family = new SourceFamilyConfiguration { Name = "Game Stats", RootPath = sourceRoot };
        var project = CompleteProject();
        var present = new ExpectedSourceDocument { Name = "Present", SourceFamilyId = family.Id, ExpectedLocator = "current.xlsx" };
        present.RefreshStatus(family); Equal(SourceDocumentStatus.Present, present.Status, "Present without currency threshold");
        True(present.Verification == DocumentVerificationState.Unverified, "Refresh does not verify");
        present.SetVerified(true);
        var current = new ExpectedSourceDocument { Name = "Current", SourceFamilyId = family.Id, ExpectedLocator = "current.xlsx", ExpectedAsOfUtc = DateTime.UtcNow.AddDays(-1) };
        current.RefreshStatus(family); Equal(SourceDocumentStatus.Current, current.Status, "Current when threshold passes"); current.SetVerified(true);
        var equality = File.GetLastWriteTimeUtc(currentPath); var exactlyCurrent = new ExpectedSourceDocument { Name = "Equality", SourceFamilyId = family.Id, ExpectedLocator = "current.xlsx", ExpectedAsOfUtc = equality };
        exactlyCurrent.RefreshStatus(family, equality); Equal(SourceDocumentStatus.Current, exactlyCurrent.Status, "Timestamp equality is Current");
        var stale = new ExpectedSourceDocument { Name = "Stale", SourceFamilyId = family.Id, ExpectedLocator = "current.xlsx", ExpectedAsOfUtc = DateTime.UtcNow.AddDays(1) };
        stale.RefreshStatus(family); Equal(SourceDocumentStatus.Stale, stale.Status, "Stale when threshold fails");
        var missing = new ExpectedSourceDocument { Name = "Missing", SourceFamilyId = family.Id, ExpectedLocator = "missing.pdf" };
        missing.RefreshStatus(family); Equal(SourceDocumentStatus.Missing, missing.Status, "Missing file");
        var pending = new ExpectedSourceDocument { Name = "Pending", SourceFamilyId = family.Id, IsPending = true };
        pending.RefreshStatus(family); Equal(SourceDocumentStatus.Pending, pending.Status, "Pending override");
        var notApplicable = new ExpectedSourceDocument { Name = "N/A", SourceFamilyId = family.Id, IsApplicable = false };
        notApplicable.RefreshStatus(family); Equal(SourceDocumentStatus.NotApplicable, notApplicable.Status, "Not applicable override");

        project.ExpectedDocuments = [present, current, notApplicable];
        var second = CompleteProject(); second.Week = 2;
        var workspace = new BuilderWorkspace { SourceFamilies = [family], Projects = [project, second], ActiveProjectId = second.Id };
        workspace.Normalize();
        True(project.IsReady, "Present and Current both satisfy source health when verified");
        True(second.IsReady, "Complete identity with no applicable expected sources is ready");
        current.SetVerified(false); True(!project.IsReady, "Unverified document blocks readiness"); current.SetVerified(true);
        project.ExpectedDocuments.Add(missing); workspace.Normalize(); True(!project.IsReady, "Missing blocks readiness"); project.ExpectedDocuments.Remove(missing);
        project.ExpectedDocuments.Add(stale); workspace.Normalize(); True(!project.IsReady, "Stale blocks readiness"); project.ExpectedDocuments.Remove(stale);
        project.ExpectedDocuments.Add(pending); workspace.Normalize(); True(!project.IsReady, "Pending blocks readiness"); project.ExpectedDocuments.Remove(pending);
        project.Season = null; True(!project.IsReady, "Incomplete identity blocks readiness"); project.Season = 2026;
        present.SourceFamilyId = Guid.NewGuid(); workspace.Normalize(); True(!project.IsReady, "Broken source-family reference blocks readiness"); present.SourceFamilyId = family.Id; present.RefreshStatus(family); present.SetVerified(true); workspace.Normalize();
        True(project.IsReady, "Readiness recovers after blockers resolve");

        var path = Path.Combine(root, "workspace.json"); var store = new ProjectStore(path); await store.SaveAsync(workspace); var loaded = await store.LoadAsync();
        Equal(second.Id, loaded.ActiveProjectId, "Active project round trip"); Equal(2, loaded.Projects.Count, "Multiple project round trip");
        var loadedProject = loaded.Projects.Single(x => x.Id == project.Id);
        Equal(2026, loadedProject.Season, "Season round trip"); Equal(1, loadedProject.Week, "Week round trip"); Equal("North Webster", loadedProject.Opponent, "Opponent round trip"); Equal(new TimeOnly(19, 0), loadedProject.KickoffTime, "Kickoff round trip");
        var loadedFamily = loaded.SourceFamilies.Single();
        Equal(family.Id, loadedFamily.Id, "Source family identity round trip"); Equal("Game Stats", loadedFamily.Name, "Source family name round trip"); Equal(sourceRoot, loadedFamily.RootPath, "Source family root round trip"); True(loadedFamily.Enabled, "Source family enabled state round trip");
        Equal(3, loadedProject.ExpectedDocuments.Count, "Expected documents round trip");
        var loadedCurrent = loadedProject.ExpectedDocuments.Single(x => x.Name == "Current"); Equal(family.Id, loadedCurrent.SourceFamilyId, "Expected document family reference round trip"); Equal(SourceDocumentStatus.Current, loadedCurrent.Status, "Expected document status round trip"); Equal(DocumentVerificationState.Verified, loadedCurrent.Verification, "Expected document verification round trip"); Equal("current.xlsx", loadedCurrent.ExpectedLocator, "Expected locator round trip");
    }

    private static async Task SchemaValidationTests(string root)
    {
        var validPath = Path.Combine(root, "valid-schema.json"); var validStore = new ProjectStore(validPath); await validStore.SaveAsync(new BuilderWorkspace { Projects = [CompleteProject()] });
        Equal(BuilderWorkspace.CurrentSchemaVersion, (await validStore.LoadAsync()).SchemaVersion, "Valid schema-1 workspace loads");

        await Rejected("empty-object.json", "{}", "Empty object is rejected");
        await Rejected("malformed.json", "{", "Malformed JSON is rejected");
        await Rejected("partial-current.json", "{\"SchemaVersion\":1,\"Projects\":[]}", "Partial current workspace is rejected");
        await Rejected("future.json", "{\"SchemaVersion\":3,\"ActiveProjectId\":null,\"SourceFamilies\":[],\"Projects\":[],\"UpdatedUtc\":\"2026-01-01T00:00:00Z\"}", "Unsupported future schema is rejected");

        var schemaOnePath = Path.Combine(root, "schema-one.json"); var schemaOneNode = JsonNode.Parse(await File.ReadAllTextAsync(validPath))!.AsObject(); schemaOneNode["SchemaVersion"] = 1;
        foreach (var projectNode in schemaOneNode["Projects"]!.AsArray().Select(x => x!.AsObject())) { projectNode.Remove("StagedGameReports"); projectNode.Remove("CompletedGames"); projectNode.Remove("CurrentAcceptedGameId"); }
        await File.WriteAllTextAsync(schemaOnePath, schemaOneNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); var migrated = await new ProjectStore(schemaOnePath).LoadAsync(); Equal(2, migrated.SchemaVersion, "WP 1 schema-1 workspace migrates to schema 2"); True(migrated.Projects.All(x => x.StagedGameReports.Count == 0 && x.CompletedGames.Count == 0 && x.CurrentAcceptedGameId is null), "Schema-1 migration fabricates no WP 2 authority"); await new ProjectStore(schemaOnePath).SaveAsync(migrated); var schemaTwoSaved = JsonDocument.Parse(await File.ReadAllTextAsync(schemaOnePath)).RootElement.GetProperty("SchemaVersion").GetInt32(); Equal(2, schemaTwoSaved, "Migrated workspace saves as schema 2"); True(schemaTwoSaved != 1, "Pre-WP2 schema-1 version check identifies schema 2 as unsupported");
        var mislabeledPath = Path.Combine(root, "schema-one-with-wp2-fields.json"); var mislabeledNode = JsonNode.Parse(await File.ReadAllTextAsync(validPath))!.AsObject(); mislabeledNode["SchemaVersion"] = 1; var mislabeledContent = mislabeledNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); await File.WriteAllTextAsync(mislabeledPath, mislabeledContent); await ThrowsAsync<InvalidDataException>(() => new ProjectStore(mislabeledPath).LoadAsync(), "Schema-1 data carrying WP 2 fields is rejected rather than silently downgraded"); Equal(mislabeledContent, await File.ReadAllTextAsync(mislabeledPath), "Rejected mislabeled schema remains unchanged");

        async Task Rejected(string name, string content, string message)
        {
            var path = Path.Combine(root, name); await File.WriteAllTextAsync(path, content); await ThrowsAsync<InvalidDataException>(() => new ProjectStore(path).LoadAsync(), message); Equal(content, await File.ReadAllTextAsync(path), $"{message} without rewriting source file");
        }
    }

    private static async Task ReloadSourceHealthTests(string root)
    {
        var sourceRoot = Path.Combine(root, "reload-source"); Directory.CreateDirectory(sourceRoot); var path = Path.Combine(sourceRoot, "weekly.xlsx"); await File.WriteAllTextAsync(path, "weekly");
        var family = new SourceFamilyConfiguration { Name = "Weekly", RootPath = sourceRoot }; var expected = new ExpectedSourceDocument { Name = "Weekly source", SourceFamilyId = family.Id, ExpectedLocator = "weekly.xlsx" };
        expected.RefreshStatus(family); expected.SetVerified(true); var project = CompleteProject(); project.ExpectedDocuments.Add(expected); var workspace = new BuilderWorkspace { SourceFamilies = [family], Projects = [project], ActiveProjectId = project.Id }; workspace.Normalize(); True(project.IsReady, "Healthy saved project starts ready");
        var storePath = Path.Combine(root, "reload-workspace.json"); var store = new ProjectStore(storePath); await store.SaveAsync(workspace); File.Delete(path);
        var missing = await store.LoadAsync(); Equal(SourceDocumentStatus.Missing, missing.ActiveProject!.ExpectedDocuments.Single().Status, "Deleted source becomes Missing on reload"); True(!missing.ActiveProject.IsReady, "Deleted source cannot reopen READY");

        await File.WriteAllTextAsync(path, "weekly"); var threshold = DateTime.UtcNow.AddHours(-1); expected.ExpectedAsOfUtc = threshold; expected.RefreshStatus(family, DateTime.UtcNow); expected.SetVerified(true); await store.SaveAsync(workspace); File.SetLastWriteTimeUtc(path, threshold.AddMinutes(-1));
        var stale = await store.LoadAsync(); Equal(SourceDocumentStatus.Stale, stale.ActiveProject!.ExpectedDocuments.Single().Status, "Older source becomes Stale on reload"); True(!stale.ActiveProject.IsReady, "Stale source cannot reopen READY");
    }

    private static async Task ReconfigurationTests(string root)
    {
        var firstRoot = Path.Combine(root, "family-one"); var secondRoot = Path.Combine(root, "family-two"); Directory.CreateDirectory(firstRoot); Directory.CreateDirectory(secondRoot);
        var oldPath = Path.Combine(firstRoot, "old.xlsx"); var newPath = Path.Combine(secondRoot, "new.xlsx"); CreateWorkbook(oldPath); CreateWorkbook(newPath);
        var first = new SourceFamilyConfiguration { Name = "First", RootPath = firstRoot }; var second = new SourceFamilyConfiguration { Name = "Second", RootPath = secondRoot };
        var expected = new ExpectedSourceDocument { Name = "Configured", SourceFamilyId = first.Id, ExpectedLocator = "old.xlsx" }; expected.RefreshStatus(first); expected.SetVerified(true);
        Equal(SourceDocumentStatus.Present, expected.Status, "Precondition source is healthy");
        var resolvedPath = expected.ResolvedPath; var modifiedUtc = expected.SourceModifiedUtc; var checkedUtc = expected.LastCheckedUtc; var verifiedUtc = expected.VerifiedUtc;
        expected.SourceFamilyId = first.Id; expected.ExpectedLocator = "old.xlsx";
        Equal(resolvedPath, expected.ResolvedPath, "Unchanged source configuration preserves resolved path"); Equal(modifiedUtc, expected.SourceModifiedUtc, "Unchanged source configuration preserves source timestamp"); Equal(checkedUtc, expected.LastCheckedUtc, "Unchanged source configuration preserves checked timestamp");
        Equal(SourceDocumentStatus.Present, expected.Status, "Unchanged source configuration preserves status"); Equal(DocumentVerificationState.Verified, expected.Verification, "Unchanged source configuration preserves verification"); Equal(verifiedUtc, expected.VerifiedUtc, "Unchanged source configuration preserves verification timestamp");
        expected.SourceFamilyId = second.Id; Invalidated(expected, "Changing source family");
        expected.ExpectedLocator = "new.xlsx"; expected.RefreshStatus(second); expected.SetVerified(true); Equal(SourceDocumentStatus.Present, expected.Status, "Reconfigured source can become healthy");
        expected.ExpectedLocator = "other.xlsx"; Invalidated(expected, "Changing expected locator");
        var project = CompleteProject(); project.ExpectedDocuments.Add(expected);
        await ThrowsAsync<InvalidOperationException>(() => new ImportService().ImportAsync(oldPath, project, expected, second), "Import cannot reuse old resolved file after reconfiguration");

        static void Invalidated(ExpectedSourceDocument document, string message)
        {
            True(document.ResolvedPath is null, $"{message} clears resolved path"); True(document.SourceModifiedUtc is null && document.LastCheckedUtc is null, $"{message} clears source observation"); Equal(SourceDocumentStatus.Missing, document.Status, $"{message} invalidates status"); Equal(DocumentVerificationState.Unverified, document.Verification, $"{message} resets verification"); True(document.VerifiedUtc is null, $"{message} clears verification timestamp");
        }
    }

    private static async Task ReorderedJsonPersistenceTests(string root)
    {
        var sourceRoot = Path.Combine(root, "reordered-source"); Directory.CreateDirectory(sourceRoot);
        var sourcePath = Path.Combine(sourceRoot, "ordered.xlsx"); await File.WriteAllTextAsync(sourcePath, "ordered");
        var family = new SourceFamilyConfiguration { Name = "Reordered", RootPath = sourceRoot };
        var expected = new ExpectedSourceDocument { Name = "Reordered document", SourceFamilyId = family.Id, ExpectedLocator = "ordered.xlsx", VerificationNote = "Persist this note" };
        expected.RefreshStatus(family); expected.SetVerified(true);
        var persistedModifiedUtc = expected.SourceModifiedUtc; var persistedCheckedUtc = expected.LastCheckedUtc; var persistedVerifiedUtc = expected.VerifiedUtc;
        var project = CompleteProject(); project.ExpectedDocuments.Add(expected);
        var workspace = new BuilderWorkspace { SourceFamilies = [family], Projects = [project], ActiveProjectId = project.Id };
        var path = Path.Combine(root, "reordered-workspace.json"); var store = new ProjectStore(path); await store.SaveAsync(workspace);

        var rootNode = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var expectedArray = rootNode["Projects"]![0]!["ExpectedDocuments"]!.AsArray(); var original = expectedArray[0]!.AsObject();
        string[] reorderedNames = ["ResolvedPath", "SourceModifiedUtc", "LastCheckedUtc", "Status", "Verification", "VerifiedUtc", "VerificationNote", "Id", "Name", "IsApplicable", "IsPending", "ExpectedAsOfUtc", "SourceFamilyId", "ExpectedLocator"];
        var reordered = new JsonObject(); foreach (var name in reorderedNames) reordered[name] = original[name]?.DeepClone(); expectedArray[0] = reordered;
        var reorderedJson = rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); await File.WriteAllTextAsync(path, reorderedJson);

        var hydrated = JsonSerializer.Deserialize<BuilderWorkspace>(reorderedJson)!.Projects.Single().ExpectedDocuments.Single();
        Equal(family.Id, hydrated.SourceFamilyId, "Reordered hydration preserves source family"); Equal("ordered.xlsx", hydrated.ExpectedLocator, "Reordered hydration preserves locator"); Equal(sourcePath, hydrated.ResolvedPath, "Reordered hydration preserves resolved path");
        Equal(persistedModifiedUtc, hydrated.SourceModifiedUtc, "Reordered hydration preserves source timestamp"); Equal(persistedCheckedUtc, hydrated.LastCheckedUtc, "Reordered hydration preserves checked timestamp"); Equal(SourceDocumentStatus.Present, hydrated.Status, "Reordered hydration preserves status");
        Equal(DocumentVerificationState.Verified, hydrated.Verification, "Reordered hydration preserves verification"); Equal(persistedVerifiedUtc, hydrated.VerifiedUtc, "Reordered hydration preserves verification timestamp"); Equal("Persist this note", hydrated.VerificationNote, "Reordered hydration preserves verification note");

        var loaded = await store.LoadAsync(); var loadedDocument = loaded.ActiveProject!.ExpectedDocuments.Single();
        Equal(SourceDocumentStatus.Present, loadedDocument.Status, "ProjectStore refreshes reordered source against filesystem"); Equal(DocumentVerificationState.Verified, loadedDocument.Verification, "ProjectStore refresh does not erase reordered verification"); True(loaded.ActiveProject.IsReady, "Reordered persisted document remains ready after current-source refresh");
    }

    private static async Task LegacyMigrationTests(string root)
    {
        var legacyPath = Path.Combine(root, "legacy.json");
        const string legacy = """
        {"Opponent":"Opponent","GameDate":"2025-09-05T16:30:00","Venue":"Legacy Stadium","PageOne":{"Week":4,"OpponentTeam":"LEGACY RIVAL","Kickoff":"7:15 p.m.","MindenRecord":"1-2"}}
        """;
        await File.WriteAllTextAsync(legacyPath, legacy); var store = new ProjectStore(legacyPath); var workspace = await store.LoadAsync(); var project = workspace.ActiveProject!;
        Equal(2025, project.Season, "Legacy season derives from supported GameDate"); Equal(4, project.Week, "Legacy week migrates"); Equal("LEGACY RIVAL", project.Opponent, "Legacy PageOne opponent fallback");
        Equal(new TimeOnly(19, 15), project.KickoffTime, "Legacy PageOne kickoff takes precedence over GameDate time"); Equal(new DateTime(2025, 9, 5), project.GameDate, "Legacy calendar date retained");
        await store.SaveAsync(workspace); var saved = await File.ReadAllTextAsync(legacyPath);
        True(!saved.Contains("\"OpponentTeam\"", StringComparison.Ordinal) && !saved.Contains("\"Kickoff\"", StringComparison.Ordinal) && !JsonDocument.Parse(saved).RootElement.GetProperty("Projects")[0].GetProperty("PageOne").TryGetProperty("Week", out _), "New save has no duplicate PageOne identity authority");

        var unknownPath = Path.Combine(root, "legacy-unknown.json"); await File.WriteAllTextAsync(unknownPath, "{\"Opponent\":\"Rival\",\"Venue\":\"Somewhere\",\"PageOne\":{\"Week\":1}}");
        var unknown = (await new ProjectStore(unknownPath).LoadAsync()).ActiveProject!;
        True(unknown.Season is null, "Legacy season remains unknown without supporting date"); True(unknown.KickoffTime is null, "Legacy kickoff remains unknown without supporting value");

        await OpponentMigration("Opponent", "PAGE ONE RIVAL", "PAGE ONE RIVAL", "Exact opponent placeholder falls back");
        await OpponentMigration("oPpOnEnT", "CASE RIVAL", "CASE RIVAL", "Case-varied opponent placeholder falls back");
        await OpponentMigration(" Opponent ", "TRIM RIVAL", "TRIM RIVAL", "Whitespace opponent placeholder falls back");
        await OpponentMigration("Meaningful Rival", "PAGE ONE RIVAL", "Meaningful Rival", "Meaningful root opponent remains authoritative");

        var fallbackPath = Path.Combine(root, "legacy-time-fallback.json"); await File.WriteAllTextAsync(fallbackPath, "{\"Opponent\":\"Rival\",\"GameDate\":\"2025-09-05T18:45:00\",\"Venue\":\"Stadium\",\"PageOne\":{\"Week\":2,\"Kickoff\":\"not a time\"}}");
        Equal(new TimeOnly(18, 45), (await new ProjectStore(fallbackPath).LoadAsync()).ActiveProject!.KickoffTime, "Legacy GameDate time is fallback for invalid PageOne kickoff");

        async Task OpponentMigration(string rootOpponent, string pageOneOpponent, string expected, string message)
        {
            var path = Path.Combine(root, $"opponent-{Guid.NewGuid():N}.json"); var json = JsonSerializer.Serialize(new { Opponent = rootOpponent, Venue = "Stadium", PageOne = new { Week = 1, OpponentTeam = pageOneOpponent } }); await File.WriteAllTextAsync(path, json); Equal(expected, (await new ProjectStore(path).LoadAsync()).ActiveProject!.Opponent, message);
        }
    }

    private static async Task ImportTests(string root)
    {
        var sourceRoot = Path.Combine(root, "import"); Directory.CreateDirectory(sourceRoot); var xlsx = Path.Combine(sourceRoot, "players.xlsx"); CreateWorkbook(xlsx);
        var family = new SourceFamilyConfiguration { Name = "Roster", RootPath = sourceRoot };
        var document = new ExpectedSourceDocument { Name = "Minden roster", SourceFamilyId = family.Id, ExpectedLocator = "players.xlsx" };
        var project = CompleteProject(); project.ExpectedDocuments.Add(document);
        var workspace = new BuilderWorkspace { SourceFamilies = [family], Projects = [project], ActiveProjectId = project.Id }; workspace.Normalize();
        var rows = await new ImportService().ImportAsync(xlsx, project, document, family);
        Equal(1, rows, "Existing XLSX parser imports row"); Equal(1, project.Players.Count, "Imported player retained");
        True(document.Verification == DocumentVerificationState.Unverified, "Import does not verify document"); Equal(SourceDocumentStatus.Present, document.Status, "Import refreshes status");
        var provenance = project.Imports.Single(); Equal(project.Id, provenance.ProjectId, "Provenance project association"); Equal(family.Id, provenance.SourceFamilyId, "Provenance family association"); Equal(document.Id, provenance.ExpectedDocumentId, "Provenance document association");
        Equal(2026, provenance.ApplicableSeason, "Provenance season snapshot"); Equal(1, provenance.ApplicableWeek, "Provenance week snapshot"); Equal("XLSX", provenance.Kind, "Provenance kind"); Equal(1, provenance.RowCount, "Provenance row count");
        True(Path.GetFullPath(xlsx) == provenance.SourceLocator, "Provenance source locator");
        project.Season = null; await new ImportService().ImportAsync(xlsx, project, document, family); True(project.Imports[0].ApplicableSeason is null, "Unknown season remains unknown in provenance");
    }

    private static async Task Wp2SingleGameTests(string root)
    {
        var fixturePdf = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Minden vs. Wossman.pdf");
        var fixtureText = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Minden vs. Wossman.txt");
        True(File.Exists(fixturePdf) && File.Exists(fixtureText), "Approved PDF and faithful extracted-text fixtures exist");
        var text = await File.ReadAllTextAsync(fixtureText);
        var family = new SourceFamilyConfiguration { Name = "Game Stats single game", RootPath = Path.GetDirectoryName(fixturePdf)! };
        var source = new ExpectedSourceDocument { Name = "Looking Back single game", SourceFamilyId = family.Id, ExpectedLocator = Path.GetFileName(fixturePdf), IsSingleGameReport = true };
        var extracted = await ImportService.ExtractPdfTextAsync(fixturePdf); True(extracted.Contains("The Automated ScoreBook", StringComparison.Ordinal) && extracted.Contains("Score by Quarters", StringComparison.Ordinal), "Application PDF extraction path reads the real fixture");
        var integrationProject = CompleteProject(); integrationProject.Opponent = "Wossman"; integrationProject.GameDate = new DateTime(2025, 11, 14); integrationProject.ExpectedDocuments.Add(source);
        var importedStage = await new ImportService().ImportSingleGameAsync(fixturePdf, integrationProject, source, family); Equal(ReportReviewState.PendingReview, importedStage.State, "Actual PDF import creates PendingReview staging"); Equal(importedStage.ImportRecordId, integrationProject.Imports.Single().Id, "Actual PDF import creates linked WP 1 provenance"); Equal(DocumentVerificationState.Unverified, source.Verification, "PDF import does not verify its expected source");
        var project = CompleteProject(); project.Opponent = "Wossman"; project.GameDate = new DateTime(2025, 11, 14); project.Season = 2025; project.Week = 11; project.ExpectedDocuments.Add(source);
        var import = new ImportRecord { ProjectId = project.Id, SourceFamilyId = family.Id, ExpectedDocumentId = source.Id, ApplicableSeason = project.Season, ApplicableWeek = project.Week, SourceLocator = fixturePdf, Kind = "PDF-SINGLE-GAME", ImportedUtc = DateTime.UtcNow };
        project.Imports.Add(import);
        var parser = new SingleGameStatsParser(); var staged = parser.Parse(text, project, source, family, import); project.StagedGameReports.Add(staged);
        Equal(ReportReviewState.PendingReview, staged.State, "Parsing creates PendingReview report"); Equal("Wossman High", staged.Opponent, "Fixture opponent parsed"); Equal(new DateTime(2025, 11, 14), staged.GameDate, "Fixture date parsed"); Equal(14, staged.MindenScore, "Fixture Minden score parsed"); Equal(35, staged.OpponentScore, "Fixture opponent score parsed");
        Equal(4, staged.PeriodScores.Count, "Four period scores parsed"); Equal(7, staged.ScoringPlays.Count, "Seven scoring plays parsed"); Equal("35-14", staged.ScoringPlays.Last().ScoreAfterPlay, "Terminal scoring-play score parsed from fixture"); True(staged.TeamStatistics.Count >= 5, "Supported team statistics parsed"); Equal("59-235", staged.TeamStatistics.Single(x => x.Key == "TotalOffense").Minden.Reported, "Authoritative total offense representation preserved");
        Equal(5, staged.Rushing.Count, "Minden rushing rows parsed"); Equal(2, staged.Passing.Count, "Minden passing rows parsed"); Equal(5, staged.Receiving.Count, "Minden receiving rows parsed"); True(staged.Issues.Any(x => x.Code == "DefensiveIgnored" && x.Severity == InformationIssueSeverity.Informational), "Defensive PDF material is informational and ignored");
        var transformed = parser.Parse(text.Replace("Wossman High", "Arcadia High", StringComparison.Ordinal).Replace("WILDCATS", "HORNETS", StringComparison.Ordinal), project, source, family, import);
        Equal("Arcadia High", transformed.Opponent, "Opponent identity is parsed without a Wossman-specific nickname"); Equal(staged.TeamStatistics.Count, transformed.TeamStatistics.Count, "Transformed opponent report retains team-statistics parsing"); Equal("59-235", transformed.TeamStatistics.Single(x => x.Key == "TotalOffense").Minden.Reported, "Transformed opponent report retains total offense");
        True(!PageOneInformationSupply.Build(project).IsAvailable, "Staged report does not create authoritative Page 1 supply");
        var pendingStore = new ProjectStore(Path.Combine(root, "wp2-pending.json")); await pendingStore.SaveAsync(new BuilderWorkspace { SourceFamilies = [family], Projects = [project], ActiveProjectId = project.Id }); Equal(ReportReviewState.PendingReview, (await pendingStore.LoadAsync()).ActiveProject!.StagedGameReports.Single().State, "PendingReview staging survives persistence without becoming authoritative");

        var workflow = new GameInformationWorkflow();
        await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(project, staged, source, family)), "Unverified source blocks report acceptance");
        source.RefreshStatus(family); source.SetVerified(true); True(source.HasHealthySource, "Fixture source is healthy");
        var accepted = workflow.Accept(project, staged, source, family); Equal(ReportReviewState.Accepted, staged.State, "Accept Report changes whole report state atomically"); Equal(staged.Id, accepted.StagedReportId, "Accepted game links staged report"); Equal(import.Id, accepted.ImportRecordId, "Accepted game links import provenance"); Equal(14, accepted.MindenScore, "Accepted normalized game retains final score");
        var supply = PageOneInformationSupply.Build(project); True(supply.IsAvailable && supply.LookingBackGame?.Id == accepted.Id, "Page 1 supply uses accepted game only"); Equal(import.Id, supply.Provenance!.Id, "Page 1 supply exposes provenance");

        var storePath = Path.Combine(root, "wp2-workspace.json"); var store = new ProjectStore(storePath); var workspace = new BuilderWorkspace { SourceFamilies = [family], Projects = [project], ActiveProjectId = project.Id }; await store.SaveAsync(workspace); var loaded = await store.LoadAsync();
        Equal(ReportReviewState.Accepted, loaded.ActiveProject!.StagedGameReports.Single().State, "Accepted staging state persists"); Equal(accepted.Id, loaded.ActiveProject.CompletedGames.Single().Id, "Accepted normalized game persists"); True(PageOneInformationSupply.Build(loaded.ActiveProject).IsAvailable, "Accepted supply survives persistence");

        var import2 = new ImportRecord { ProjectId = project.Id, SourceFamilyId = family.Id, ExpectedDocumentId = source.Id, ApplicableSeason = project.Season, ApplicableWeek = project.Week, SourceLocator = fixturePdf, Kind = "PDF-SINGLE-GAME", ImportedUtc = DateTime.UtcNow }; project.Imports.Add(import2);
        var replacement = parser.Parse(text, project, source, family, import2); project.StagedGameReports.Add(replacement);
        await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(project, replacement, source, family)), "Reimport cannot silently replace accepted authority"); True(accepted.IsCurrentAuthority, "Existing accepted game remains authoritative after reimport");
        workflow.Correct(replacement, "Site", "Corrected W.W. Williams Stadium", "Report location normalized after operator review"); var correction = replacement.Corrections.Single(); Equal("Minden, LA", correction.OriginalValue, "Correction preserves original parsed value"); Equal("Corrected W.W. Williams Stadium", correction.CorrectedValue, "Correction preserves corrected value"); True(!string.IsNullOrWhiteSpace(correction.Note), "Correction preserves operator note");
        workflow.Correct(replacement, "Site", "W.W. Williams Stadium", "Second review refined the site display"); Equal(2, replacement.Corrections.Count(x => x.FieldKey == "Site"), "Successive corrections remain append-only");
        workflow.Correct(replacement, "TeamStatistic:TotalOffense:Minden", "236", "Operator-confirmed correction against source"); var statCorrection = replacement.Corrections.Single(x => x.FieldKey.StartsWith("TeamStatistic", StringComparison.Ordinal)); Equal("59-235", statCorrection.OriginalValue, "Detailed correction preserves original report representation");
        True(replacement.Issues.Any(x => x.Code == "TotalOffenseDiscrepancy"), "A correction that introduces a discrepancy exposes an advisory");
        await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(project, replacement, source, family, replace: true)), "Correction-introduced advisory requires a replacement note"); True(accepted.IsCurrentAuthority, "Failed advisory replacement leaves prior authority intact");
        var replaced = workflow.Accept(project, replacement, source, family, "Accepted operator-corrected total offense with advisory", true); True(!accepted.IsCurrentAuthority && replaced.IsCurrentAuthority, "Explicit replacement atomically changes current authority"); Equal(import2.Id, replaced.ImportRecordId, "Replacement uses new import provenance"); True(project.Imports.Any(x => x.Id == import.Id), "Replacement preserves prior import provenance"); Equal("W.W. Williams Stadium", replaced.Site, "Latest correction determines the accepted effective value");
        var acceptedTotal = replaced.TeamStatistics.Single(x => x.Key == "TotalOffense").Minden; Equal("59-235", acceptedTotal.Reported, "Accepted information preserves the source-reported value"); Equal("236", acceptedTotal.AcceptedValue, "Accepted information records the corrected effective value separately"); Equal("236", acceptedTotal.Effective, "Effective value resolves to the latest accepted correction");
        await store.SaveAsync(workspace); var replacementReload = await store.LoadAsync(); var persistedReplacement = replacementReload.ActiveProject!.CompletedGames.Single(x => x.IsCurrentAuthority); Equal(replaced.Id, persistedReplacement.Id, "Replacement authority survives persistence"); Equal(3, persistedReplacement.Corrections.Count, "Full correction revision history survives persistence"); True(replacementReload.ActiveProject.CompletedGames.Any(x => !x.IsCurrentAuthority && x.ImportRecordId == import.Id), "Superseded authority and prior provenance survive persistence");

        var advisoryProject = CompleteProject(); advisoryProject.Opponent = "Wossman"; advisoryProject.GameDate = new DateTime(2025, 11, 14); advisoryProject.ExpectedDocuments.Add(source);
        var discrepancyText = text.Replace("TOTAL OFFENSE PLAYS-YARDS               53-443          59-235", "TOTAL OFFENSE PLAYS-YARDS               53-443          59-999", StringComparison.Ordinal);
        var advisoryImport = new ImportRecord { ProjectId = advisoryProject.Id, SourceFamilyId = family.Id, ExpectedDocumentId = source.Id, SourceLocator = fixturePdf }; advisoryProject.Imports.Add(advisoryImport);
        var advisory = parser.Parse(discrepancyText, advisoryProject, source, family, advisoryImport); advisoryProject.StagedGameReports.Add(advisory); True(advisory.Issues.Any(x => x.Code == "TotalOffenseDiscrepancy" && x.Severity == InformationIssueSeverity.Advisory), "Total offense discrepancy is advisory"); Equal("59-999", advisory.TeamStatistics.Single(x => x.Key == "TotalOffense").Minden.Reported, "Validation does not rewrite authoritative supplied value");
        await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(advisoryProject, advisory, source, family)), "Advisory acceptance requires operator note"); workflow.Correct(advisory, "TeamStatistic:TotalOffense:Minden", "59-235", "Corrected transcription against verified PDF"); True(!advisory.Issues.Any(x => x.Code == "TotalOffenseDiscrepancy"), "Effective correction resolving arithmetic discrepancy removes advisory"); var advisoryGame = workflow.Accept(advisoryProject, advisory, source, family); Equal("59-999", advisoryGame.TeamStatistics.Single(x => x.Key == "TotalOffense").Minden.Reported, "Corrected acceptance retains source-reported value"); Equal("59-235", advisoryGame.TeamStatistics.Single(x => x.Key == "TotalOffense").Minden.Effective, "Corrected acceptance supplies effective value");
        var scoreImport = NewImport(advisoryProject, family, source, fixturePdf); advisoryProject.Imports.Add(scoreImport); var scoreCorrection = parser.Parse(text, advisoryProject, source, family, scoreImport); advisoryProject.StagedGameReports.Add(scoreCorrection); workflow.Correct(scoreCorrection, "MindenScore", "15", "Operator score correction under review"); True(scoreCorrection.Issues.Any(x => x.Code == "QuarterTotalDiscrepancy") && scoreCorrection.Issues.Any(x => x.Code == "TerminalScoreDiscrepancy"), "Effective final-score correction revalidates period totals and terminal scoring score"); workflow.Correct(scoreCorrection, "MindenScore", "14", "Second review restored the verified final score"); True(!scoreCorrection.Issues.Any(x => x.Code is "QuarterTotalDiscrepancy" or "TerminalScoreDiscrepancy"), "Latest final-score correction resolves both effective advisories");

        var rejectedProject = CompleteProject(); rejectedProject.Opponent = "Wossman"; rejectedProject.GameDate = new DateTime(2025, 11, 14); rejectedProject.ExpectedDocuments.Add(source); var rejectedImport = new ImportRecord { ProjectId = rejectedProject.Id, SourceFamilyId = family.Id, ExpectedDocumentId = source.Id, SourceLocator = fixturePdf }; rejectedProject.Imports.Add(rejectedImport); var rejected = parser.Parse(text, rejectedProject, source, family, rejectedImport); rejectedProject.StagedGameReports.Add(rejected); workflow.Reject(rejected, "Wrong weekly source selection"); Equal(ReportReviewState.Rejected, rejected.State, "Report rejection is retained"); True(rejectedProject.CompletedGames.Count == 0, "Rejected report creates no accepted game"); var rejectedStore = new ProjectStore(Path.Combine(root, "wp2-rejected.json")); await rejectedStore.SaveAsync(new BuilderWorkspace { SourceFamilies = [family], Projects = [rejectedProject], ActiveProjectId = rejectedProject.Id }); Equal(ReportReviewState.Rejected, (await rejectedStore.LoadAsync()).ActiveProject!.StagedGameReports.Single().State, "Rejected staging survives persistence");

        var blockingImport = NewImport(project, family, source, fixturePdf); project.Imports.Add(blockingImport); var blockingReplacement = parser.Parse(text.Replace("Score by Quarters", "Score section unavailable", StringComparison.Ordinal), project, source, family, blockingImport); project.StagedGameReports.Add(blockingReplacement); var authorityBeforeFailure = project.CurrentAcceptedGameId; await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(project, blockingReplacement, source, family, replace: true)), "Blocking replacement is rejected"); Equal(authorityBeforeFailure, project.CurrentAcceptedGameId, "Blocking replacement leaves prior authority selected"); True(project.CompletedGames.Single(x => x.Id == authorityBeforeFailure).IsCurrentAuthority, "Blocking replacement leaves prior authority current");
        var brokenImport = NewImport(project, family, source, fixturePdf); var brokenProvenance = parser.Parse(text, project, source, family, brokenImport); project.StagedGameReports.Add(brokenProvenance); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(project, brokenProvenance, source, family, replace: true)), "Replacement with orphan import provenance is rejected"); Equal(authorityBeforeFailure, project.CurrentAcceptedGameId, "Broken-provenance replacement leaves prior authority selected"); project.StagedGameReports.Remove(brokenProvenance);

        var variantImport = NewImport(project, family, source, fixturePdf); project.Imports.Add(variantImport); var variant = parser.Parse(text, project, source, family, variantImport); project.StagedGameReports.Add(variant); workflow.Correct(variant, "Opponent", "  WOSSMAN!!!  ", "Normalized display variation confirmed as the same opponent"); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(project, variant, source, family)), "Cosmetic opponent variation cannot bypass existing date authority"); var variantGame = workflow.Accept(project, variant, source, family, replace: true); Equal("wossman", GameInformationWorkflow.NormalizeOpponent(variantGame.Opponent), "Case, whitespace, and punctuation normalize deterministically");
        var suffixImport = NewImport(project, family, source, fixturePdf); project.Imports.Add(suffixImport); var suffixVariant = parser.Parse(text, project, source, family, suffixImport); project.StagedGameReports.Add(suffixVariant); workflow.Correct(suffixVariant, "Opponent", "Wossman High School", "Common school suffix variation confirmed"); var suffixGame = workflow.Accept(project, suffixVariant, source, family, replace: true); Equal("wossman", GameInformationWorkflow.NormalizeOpponent(suffixGame.Opponent), "Common school-name suffix variation normalizes deterministically"); Equal(1, project.CompletedGames.Count(x => x.GameDate.Date == suffixGame.GameDate.Date && x.IsCurrentAuthority), "Repeated replacement retains exactly one current authority for the calendar date");
        await store.SaveAsync(workspace); var repeatedReload = await store.LoadAsync(); Equal(suffixGame.Id, repeatedReload.ActiveProject!.CurrentAcceptedGameId, "Latest repeated replacement remains selected after reload"); Equal(1, repeatedReload.ActiveProject.CompletedGames.Count(x => x.GameDate.Date == suffixGame.GameDate.Date && x.IsCurrentAuthority), "Reload retains exactly one current authority after repeated replacement"); True(repeatedReload.ActiveProject.CompletedGames.Count(x => x.GameDate.Date == suffixGame.GameDate.Date && !x.IsCurrentAuthority) >= 3, "Repeated replacement retains superseded authority history");

        var sourceLossPdf = Path.Combine(root, "accepted-source-loss.pdf"); File.Copy(fixturePdf, sourceLossPdf, true); var sourceLossFamily = new SourceFamilyConfiguration { Name = "Source loss family", RootPath = root }; var sourceLossDocument = new ExpectedSourceDocument { Name = "Accepted source loss", SourceFamilyId = sourceLossFamily.Id, ExpectedLocator = Path.GetFileName(sourceLossPdf), IsSingleGameReport = true }; var sourceLossProject = CompleteProject(); sourceLossProject.ExpectedDocuments.Add(sourceLossDocument); var sourceLossImport = NewImport(sourceLossProject, sourceLossFamily, sourceLossDocument, sourceLossPdf); sourceLossProject.Imports.Add(sourceLossImport); var sourceLossStage = parser.Parse(text, sourceLossProject, sourceLossDocument, sourceLossFamily, sourceLossImport); sourceLossProject.StagedGameReports.Add(sourceLossStage); sourceLossDocument.RefreshStatus(sourceLossFamily); sourceLossDocument.SetVerified(true); var sourceLossGame = workflow.Accept(sourceLossProject, sourceLossStage, sourceLossDocument, sourceLossFamily); var sourceLossPath = Path.Combine(root, "wp2-source-loss.json"); var sourceLossStore = new ProjectStore(sourceLossPath); await sourceLossStore.SaveAsync(new BuilderWorkspace { SourceFamilies = [sourceLossFamily], Projects = [sourceLossProject], ActiveProjectId = sourceLossProject.Id }); File.Delete(sourceLossPdf); var sourceLossReload = await sourceLossStore.LoadAsync(); True(!sourceLossReload.ActiveProject!.ExpectedDocuments.Single().HasHealthySource, "WP 1 source health reflects deletion after acceptance"); True(PageOneInformationSupply.Build(sourceLossReload.ActiveProject).IsAvailable, "Accepted Page 1 snapshot remains available after source deletion"); Equal(sourceLossGame.ImportRecordId, PageOneInformationSupply.Build(sourceLossReload.ActiveProject).Provenance!.Id, "Accepted snapshot retains original import provenance after source deletion");

        var nextWeekProject = CompleteProject(); nextWeekProject.Opponent = "North Webster"; nextWeekProject.GameDate = new DateTime(2025, 11, 21); var priorGame = parser.Parse(text, nextWeekProject, source, family, new ImportRecord { ProjectId = nextWeekProject.Id }); True(!priorGame.HasBlockingIssues, "Upcoming weekly identity does not conflict with prior Looking Back game identity");
        var structurallyUnsafe = parser.Parse(text.Replace("Score by Quarters", "Score section unavailable", StringComparison.Ordinal), project, source, family, new ImportRecord { ProjectId = project.Id }); True(structurallyUnsafe.HasBlockingIssues, "Missing mandatory final-score structure blocks acceptance");

        var weekOne = CompleteProject(); var notApplicable = new ExpectedSourceDocument { Name = "Prior game Looking Back", SourceFamilyId = family.Id, IsApplicable = false, IsSingleGameReport = true }; weekOne.ExpectedDocuments.Add(notApplicable); var weekOneSupply = PageOneInformationSupply.Build(weekOne); True(!weekOneSupply.IsAvailable && weekOneSupply.IsNotApplicable && weekOne.CompletedGames.Count == 0, "Week 1 NotApplicable supply creates no fake completed game");

        static ImportRecord NewImport(GameNotesProject owner, SourceFamilyConfiguration sourceFamily, ExpectedSourceDocument document, string locator) => new() { ProjectId = owner.Id, SourceFamilyId = sourceFamily.Id, ExpectedDocumentId = document.Id, ApplicableSeason = owner.Season, ApplicableWeek = owner.Week, SourceLocator = locator, Kind = "PDF-SINGLE-GAME", ImportedUtc = DateTime.UtcNow };
    }

    private static async Task Wp2ImmediateSourceHealthTests(string root)
    {
        var fixturePdf = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Minden vs. Wossman.pdf"); var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Minden vs. Wossman.txt")); var parser = new SingleGameStatsParser(); var workflow = new GameInformationWorkflow();

        var deletedAcceptance = Prepare("deleted-before-acceptance.pdf"); File.Delete(deletedAcceptance.Path); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(deletedAcceptance.Project, deletedAcceptance.Stage, deletedAcceptance.Source, deletedAcceptance.Family)), "Acceptance refresh rejects a source deleted after verification"); Equal(ReportReviewState.PendingReview, deletedAcceptance.Stage.State, "Deleted-source acceptance leaves staging pending"); True(deletedAcceptance.Project.CompletedGames.Count == 0, "Deleted-source acceptance establishes no authority"); Equal(SourceDocumentStatus.Missing, deletedAcceptance.Source.Status, "Acceptance refresh records the deleted source as Missing");

        var deletedReplacement = Prepare("deleted-before-replacement.pdf"); var deletedOriginal = workflow.Accept(deletedReplacement.Project, deletedReplacement.Stage, deletedReplacement.Source, deletedReplacement.Family); var deletedReplacementImport = NewImport(deletedReplacement.Project, deletedReplacement.Family, deletedReplacement.Source, deletedReplacement.Path); deletedReplacement.Project.Imports.Add(deletedReplacementImport); var deletedReplacementStage = parser.Parse(text, deletedReplacement.Project, deletedReplacement.Source, deletedReplacement.Family, deletedReplacementImport); deletedReplacement.Project.StagedGameReports.Add(deletedReplacementStage); File.Delete(deletedReplacement.Path); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(deletedReplacement.Project, deletedReplacementStage, deletedReplacement.Source, deletedReplacement.Family, replace: true)), "Replacement refresh rejects a source deleted after staging"); Equal(deletedOriginal.Id, deletedReplacement.Project.CurrentAcceptedGameId, "Deleted-source replacement leaves prior authority selected"); True(deletedOriginal.IsCurrentAuthority && deletedReplacement.Project.CompletedGames.Count == 1, "Deleted-source replacement leaves prior authority unchanged");

        var staleAcceptance = Prepare("stale-before-acceptance.pdf"); staleAcceptance.Source.ExpectedAsOfUtc = DateTime.UtcNow.AddHours(1); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(staleAcceptance.Project, staleAcceptance.Stage, staleAcceptance.Source, staleAcceptance.Family)), "Acceptance refresh rejects a source that became stale after verification"); Equal(SourceDocumentStatus.Stale, staleAcceptance.Source.Status, "Acceptance refresh records the source as Stale"); True(staleAcceptance.Project.CompletedGames.Count == 0, "Stale-source acceptance establishes no authority");

        var staleReplacement = Prepare("stale-before-replacement.pdf"); var staleOriginal = workflow.Accept(staleReplacement.Project, staleReplacement.Stage, staleReplacement.Source, staleReplacement.Family); var staleReplacementImport = NewImport(staleReplacement.Project, staleReplacement.Family, staleReplacement.Source, staleReplacement.Path); staleReplacement.Project.Imports.Add(staleReplacementImport); var staleReplacementStage = parser.Parse(text, staleReplacement.Project, staleReplacement.Source, staleReplacement.Family, staleReplacementImport); staleReplacement.Project.StagedGameReports.Add(staleReplacementStage); staleReplacement.Source.ExpectedAsOfUtc = DateTime.UtcNow.AddHours(1); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.Accept(staleReplacement.Project, staleReplacementStage, staleReplacement.Source, staleReplacement.Family, replace: true)), "Replacement refresh rejects a source that became stale after staging"); Equal(staleOriginal.Id, staleReplacement.Project.CurrentAcceptedGameId, "Stale-source replacement leaves prior authority selected"); True(staleOriginal.IsCurrentAuthority && staleReplacement.Project.CompletedGames.Count == 1, "Stale-source replacement leaves prior authority unchanged");

        (string Path, SourceFamilyConfiguration Family, ExpectedSourceDocument Source, GameNotesProject Project, StagedSingleGameReport Stage) Prepare(string name)
        {
            var path = Path.Combine(root, name); File.Copy(fixturePdf, path, true); var family = new SourceFamilyConfiguration { Name = name, RootPath = root }; var source = new ExpectedSourceDocument { Name = name, SourceFamilyId = family.Id, ExpectedLocator = name, IsSingleGameReport = true }; var project = CompleteProject(); project.ExpectedDocuments.Add(source); var import = NewImport(project, family, source, path); project.Imports.Add(import); var stage = parser.Parse(text, project, source, family, import); project.StagedGameReports.Add(stage); source.RefreshStatus(family); source.SetVerified(true); True(source.HasHealthySource, $"{name} starts healthy and verified"); return (path, family, source, project, stage);
        }
        static ImportRecord NewImport(GameNotesProject owner, SourceFamilyConfiguration family, ExpectedSourceDocument source, string path) => new() { ProjectId = owner.Id, SourceFamilyId = family.Id, ExpectedDocumentId = source.Id, SourceLocator = path, Kind = "PDF-SINGLE-GAME", ImportedUtc = DateTime.UtcNow };
    }

    private static async Task Wp2PersistenceIntegrityTests(string root)
    {
        var fixturePdf = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Minden vs. Wossman.pdf"); var fixtureText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Minden vs. Wossman.txt"));
        var family = new SourceFamilyConfiguration { Name = "Integrity family", RootPath = Path.GetDirectoryName(fixturePdf)! }; var source = new ExpectedSourceDocument { Name = "Integrity single game", SourceFamilyId = family.Id, ExpectedLocator = Path.GetFileName(fixturePdf), IsSingleGameReport = true };
        var project = CompleteProject(); project.ExpectedDocuments.Add(source); var import = new ImportRecord { ProjectId = project.Id, SourceFamilyId = family.Id, ExpectedDocumentId = source.Id, SourceLocator = fixturePdf, ImportedUtc = DateTime.UtcNow }; project.Imports.Add(import);
        var staged = new SingleGameStatsParser().Parse(fixtureText, project, source, family, import); project.StagedGameReports.Add(staged); source.RefreshStatus(family); source.SetVerified(true); new GameInformationWorkflow().Accept(project, staged, source, family);
        var workspace = new BuilderWorkspace { SourceFamilies = [family], Projects = [project], ActiveProjectId = project.Id }; var validPath = Path.Combine(root, "wp2-integrity-valid.json"); var validStore = new ProjectStore(validPath); await validStore.SaveAsync(workspace); var validJson = await File.ReadAllTextAsync(validPath); Equal(2, (await validStore.LoadAsync()).SchemaVersion, "Valid schema-2 authority round trips");
        var invalidSave = await validStore.LoadAsync(); invalidSave.ActiveProject!.Imports.Single().ProjectId = Guid.Empty; await ThrowsAsync<InvalidDataException>(() => validStore.SaveAsync(invalidSave), "Saving malformed authority does not normalize empty import ownership"); Equal(validJson, await File.ReadAllTextAsync(validPath), "Rejected malformed authority save leaves the existing file unchanged");

        await RejectedMutation("duplicate-staged.json", node => { var reports = Project(node)["StagedGameReports"]!.AsArray(); reports.Add(reports[0]!.DeepClone()); }, "Duplicate staged IDs are rejected");
        await RejectedMutation("duplicate-game.json", node => { var games = Project(node)["CompletedGames"]!.AsArray(); games.Add(games[0]!.DeepClone()); }, "Duplicate CompletedGame IDs are rejected");
        await RejectedMutation("orphan-import.json", node => Stage(node)["ImportRecordId"] = Guid.NewGuid(), "Orphan staged ImportRecord is rejected");
        await RejectedMutation("orphan-document.json", node => Stage(node)["ExpectedDocumentId"] = Guid.NewGuid(), "Orphan staged expected document is rejected");
        await RejectedMutation("orphan-family.json", node => Stage(node)["SourceFamilyId"] = Guid.NewGuid(), "Orphan staged source family is rejected");
        await RejectedMutation("orphan-game-import.json", node => Game(node)["ImportRecordId"] = Guid.NewGuid(), "Orphan completed-game ImportRecord is rejected");
        await RejectedMutation("orphan-game-document.json", node => Game(node)["ExpectedDocumentId"] = Guid.NewGuid(), "Orphan completed-game expected document is rejected");
        await RejectedMutation("orphan-game-family.json", node => Game(node)["SourceFamilyId"] = Guid.NewGuid(), "Orphan completed-game source family is rejected");
        await RejectedMutation("wrong-stage-project.json", node => Stage(node)["ProjectId"] = Guid.NewGuid(), "Invalid staged-report project ownership is rejected");
        await RejectedMutation("wrong-game-project.json", node => Game(node)["ProjectId"] = Guid.NewGuid(), "Invalid completed-game project ownership is rejected");
        await RejectedMutation("empty-import-id.json", node => Import(node)["Id"] = Guid.Empty, "Empty persisted import identity participating in WP 2 provenance is rejected");
        await RejectedMutation("empty-import-project.json", node => Import(node)["ProjectId"] = Guid.Empty, "Empty persisted import project ownership is rejected before normalization");
        await RejectedMutation("empty-project-id.json", node => Project(node)["Id"] = Guid.Empty, "Empty persisted authority project identity is rejected before normalization");
        await RejectedMutation("empty-document-id.json", node => Project(node)["ExpectedDocuments"]![0]!["Id"] = Guid.Empty, "Empty persisted expected-document identity participating in authority is rejected");
        await RejectedMutation("invalid-current.json", node => Project(node)["CurrentAcceptedGameId"] = Guid.NewGuid(), "Invalid CurrentAcceptedGameId is rejected");
        await RejectedMutation("noncurrent-current-id.json", node => Game(node)["IsCurrentAuthority"] = false, "CurrentAcceptedGameId pointing to noncurrent game is rejected");
        await RejectedMutation("accepted-without-game.json", node => { Project(node)["CompletedGames"] = new JsonArray(); Project(node)["CurrentAcceptedGameId"] = null; }, "Accepted staging without CompletedGame is rejected");
        await RejectedMutation("pending-authority.json", node => Stage(node)["State"] = "PendingReview", "Pending staging establishing authority is rejected");
        await RejectedMutation("rejected-authority.json", node => Stage(node)["State"] = "Rejected", "Rejected staging establishing authority is rejected");
        await RejectedMutation("multiple-current.json", node => { var p = Project(node); var stagedClone = Stage(node).DeepClone().AsObject(); var stagedId = Guid.NewGuid(); stagedClone["Id"] = stagedId; p["StagedGameReports"]!.AsArray().Add(stagedClone); var gameClone = Game(node).DeepClone().AsObject(); gameClone["Id"] = Guid.NewGuid(); gameClone["StagedReportId"] = stagedId; p["CompletedGames"]!.AsArray().Add(gameClone); }, "Multiple current authorities for one game date are rejected");
        await RejectedMutation("superseded-only.json", node => { Game(node)["IsCurrentAuthority"] = false; Project(node)["CurrentAcceptedGameId"] = null; }, "Current/superseded contradiction is rejected");
        await RejectedMutation("null-nested.json", node => Stage(node)["PeriodScores"] = null, "Null nested authority collections are rejected");

        async Task RejectedMutation(string name, Action<JsonObject> mutate, string message)
        {
            var node = JsonNode.Parse(validJson)!.AsObject(); mutate(node); var path = Path.Combine(root, name); var content = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); await File.WriteAllTextAsync(path, content); await ThrowsAsync<InvalidDataException>(() => new ProjectStore(path).LoadAsync(), message); Equal(content, await File.ReadAllTextAsync(path), $"{message} without rewriting source file");
        }
        static JsonObject Project(JsonObject node) => node["Projects"]![0]!.AsObject();
        static JsonObject Stage(JsonObject node) => Project(node)["StagedGameReports"]![0]!.AsObject();
        static JsonObject Game(JsonObject node) => Project(node)["CompletedGames"]![0]!.AsObject();
        static JsonObject Import(JsonObject node) => Project(node)["Imports"]![0]!.AsObject();
    }

    private static void RenderingSmoke(string root, GameNotesProject project)
    {
        var output = Path.Combine(root, "diagnostic.pdf"); PdfExporter.Export(output, project); var pdf = File.ReadAllText(output, Encoding.Latin1);
        True(pdf.StartsWith("%PDF-1.4", StringComparison.Ordinal), "Legacy technical PDF remains buildable");
        True(pdf.Contains("/Type /Page ", StringComparison.Ordinal), "Diagnostic PDF contains at least one page");
    }

    private static GameNotesProject CompleteProject() => new()
    {
        Season = 2026, Week = 1, Opponent = "North Webster", GameDate = new DateTime(2026, 9, 4), KickoffTime = new TimeOnly(19, 0), Venue = "North Webster High School\nBaucum-Farrar Stadium — Springhill, LA"
    };

    private static void CreateWorkbook(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create); var entry = zip.CreateEntry("xl/worksheets/sheet1.xml"); using var stream = entry.Open(); using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row><c t="inlineStr"><is><t>Player</t></is></c><c t="inlineStr"><is><t>Number</t></is></c><c t="inlineStr"><is><t>GP</t></is></c></row><row><c t="inlineStr"><is><t>Test Player</t></is></c><c t="inlineStr"><is><t>7</t></is></c><c t="inlineStr"><is><t>1</t></is></c></row></sheetData></worksheet>""");
    }

    private static void True(bool condition, string message) { assertions++; if (!condition) throw new Exception($"FAIL: {message}"); }
    private static void Equal<T>(T expected, T actual, string message) { assertions++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"FAIL: {message}; expected {expected}, actual {actual}"); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception { assertions++; try { await action(); } catch (T) { return; } throw new Exception($"FAIL: {message}; expected {typeof(T).Name}"); }

    private static void MakeProof(GameNotesProject project)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")); var dir = Path.Combine(root, "artifacts", "wp1"); Directory.CreateDirectory(dir);
        foreach (var variant in TypographyVariant.Candidates) { PdfExporter.ExportPageOne(Path.Combine(dir, "Page-1-Final-WP1-Proof.pdf"), project, variant); PageRasterizer.SavePageOnePng(Path.Combine(dir, "Page-1-Final-WP1-Proof.png"), project, 150, variant); }
        var reference = Path.Combine(root, "references", "Game Notes Page 1.png"); var primary = Path.Combine(dir, "Page-1-Final-WP1-Proof.png"); if (File.Exists(reference)) SaveComparison(reference, primary, Path.Combine(dir, "Page-1-Comparison.png"));
    }

    private static void SaveComparison(string referencePath, string proofPath, string output)
    {
        BitmapImage Load(string p) { var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(p); b.EndInit(); b.Freeze(); return b; }
        var a = Load(referencePath); var b = Load(proofPath); const int gap = 30; var h = Math.Max(a.PixelHeight, b.PixelHeight); var w = a.PixelWidth + b.PixelWidth + gap;
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h)); dc.DrawImage(a, new Rect(0, 0, a.PixelWidth, a.PixelHeight)); dc.DrawImage(b, new Rect(a.PixelWidth + gap, 0, b.PixelWidth, b.PixelHeight)); }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bmp.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using var outputStream = File.Create(output); encoder.Save(outputStream);
    }
}

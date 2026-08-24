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
            if (args.Contains("--wp3")) { await Wp3DefensiveWorkbookTests(root); Console.WriteLine($"PASS: WP 3 focused suite ({assertions} assertions)"); return; }
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
            await Wp3DefensiveWorkbookTests(root);
            RenderingSmoke(root, proofProject);
            Console.WriteLine($"PASS: WP 3 smoke suite ({assertions} assertions)");
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
        await Rejected("future.json", "{\"SchemaVersion\":4,\"ActiveProjectId\":null,\"SourceFamilies\":[],\"Projects\":[],\"UpdatedUtc\":\"2026-01-01T00:00:00Z\"}", "Unsupported future schema is rejected");

        var schemaOnePath = Path.Combine(root, "schema-one.json"); var schemaOneNode = JsonNode.Parse(await File.ReadAllTextAsync(validPath))!.AsObject(); schemaOneNode["SchemaVersion"] = 1;
        foreach (var projectNode in schemaOneNode["Projects"]!.AsArray().Select(x => x!.AsObject())) { projectNode.Remove("StagedGameReports"); projectNode.Remove("CompletedGames"); projectNode.Remove("CurrentAcceptedGameId"); projectNode.Remove("StagedDefensiveWorkbooks"); projectNode.Remove("AcceptedDefensiveGames"); projectNode.Remove("AcceptedDefensiveSeasonTotals"); }
        await File.WriteAllTextAsync(schemaOnePath, schemaOneNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); var migrated = await new ProjectStore(schemaOnePath).LoadAsync(); Equal(3, migrated.SchemaVersion, "WP 1 schema-1 workspace migrates to schema 3"); True(migrated.Projects.All(x => x.StagedGameReports.Count == 0 && x.CompletedGames.Count == 0 && x.CurrentAcceptedGameId is null && x.StagedDefensiveWorkbooks.Count == 0), "Schema-1 migration fabricates no WP 2/WP 3 authority"); await new ProjectStore(schemaOnePath).SaveAsync(migrated); var schemaSaved = JsonDocument.Parse(await File.ReadAllTextAsync(schemaOnePath)).RootElement.GetProperty("SchemaVersion").GetInt32(); Equal(3, schemaSaved, "Migrated workspace saves as schema 3");
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
        var workspace = new BuilderWorkspace { SourceFamilies = [family], Projects = [project], ActiveProjectId = project.Id }; var validPath = Path.Combine(root, "wp2-integrity-valid.json"); var validStore = new ProjectStore(validPath); await validStore.SaveAsync(workspace); var validJson = await File.ReadAllTextAsync(validPath); Equal(3, (await validStore.LoadAsync()).SchemaVersion, "Valid schema-3 authority round trips");
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

    private static async Task Wp3DefensiveWorkbookTests(string root)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "DefensiveFixture", "2025.xlsx"); True(File.Exists(fixture), "Representative 2025 defensive workbook is available");
        var sourceRoot = Path.Combine(root, "defensive-source"); Directory.CreateDirectory(sourceRoot); var sourcePath = Path.Combine(sourceRoot, "2025.xlsx"); File.Copy(fixture, sourcePath, true);
        var family = new SourceFamilyConfiguration { Name = "Jake defensive", RootPath = sourceRoot };
        var document = new ExpectedSourceDocument { Name = "Jake defensive workbook", SourceFamilyId = family.Id, ExpectedLocator = "2025.xlsx" };
        var project = CompleteProject(); project.Season = 2025; project.ExpectedDocuments.Add(document);
        var importer = new DefensiveWorkbookImportService(); var staged = importer.Import(sourcePath, project, document, family);
        Equal(11, staged.Games.Count, "Actual workbook parses eleven game sections"); True(staged.SeasonTotals is not null, "Actual workbook parses a separate TOTALS section"); Equal(26, staged.SeasonTotals!.Players.Count, "Actual TOTALS player count");
        Equal("Homer", staged.Games.Single(x => x.Week == 2).Opponent, "Game identity is parsed from worksheet title"); Equal("at", staged.Games.Single(x => x.Week == 2).SiteIndicator, "Game site indicator is preserved");
        var homerCarey = staged.Games.Single(x => x.Week == 2).Players.Single(x => x.PlayerName == "Jamall Carey"); Equal(DefensiveCellState.PresentBlank, homerCarey.Solo.State, "Present blank is preserved"); Equal(0m, homerCarey.Total.Numeric, "Formula cached zero is retained independently");
        var mansfieldDavis = staged.Games.Single(x => x.Week == 1).Players.Single(x => x.PlayerName == "Tyree Davis"); True(mansfieldDavis.Solo.IsExplicitZero, "Explicit numeric zero is distinct from blank"); True(mansfieldDavis.Total.Formula is not null, "Source formula provenance is retained");
        Equal("XLSX-DEFENSIVE", project.Imports.Single().Kind, "Dedicated defensive import kind is recorded"); True(project.Imports.Single().ApplicableWeek is null, "Season workbook provenance does not fabricate a workbook-wide week"); Equal(0, project.Players.Count, "Defensive import does not use legacy immediate player upsert"); Equal(0, project.AcceptedDefensiveGames.Count, "Import creates no defensive authority");

        document.SetVerified(true); var workflow = new DefensiveInformationWorkflow(); var weekOne = staged.Games.Single(x => x.Week == 1);
        var acceptedGame = workflow.AcceptGame(project, staged, weekOne, document, family); var acceptedTotals = workflow.AcceptSeasonTotals(project, staged, document, family, "Reviewed source totals");
        True(acceptedGame.IsCurrentAuthority && acceptedTotals.IsCurrentAuthority, "Game and totals can be accepted independently");
        var supply = AcceptedDefensiveInformationSupply.Build(project, 2025); Equal(1, supply.Games.Count, "Nonvisual supply exposes accepted games only"); True(supply.SeasonTotals?.Id == acceptedTotals.Id && supply.Provenance.ContainsKey(acceptedGame.ImportRecordId), "Nonvisual supply includes separate totals and provenance");

        var replacementStage = importer.Import(sourcePath, project, document, family); document.SetVerified(true); var replacementGameStage = replacementStage.Games.Single(x => x.Week == 1);
        await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptGame(project, replacementStage, replacementGameStage, document, family)), "Reimport cannot silently replace defensive game authority");
        var replacement = workflow.AcceptGame(project, replacementStage, replacementGameStage, document, family, replace: true); True(!acceptedGame.IsCurrentAuthority && replacement.IsCurrentAuthority, "Explicit game replacement preserves superseded history"); True(project.Imports.Any(x => x.Id == acceptedGame.ImportRecordId), "Replacement preserves prior defensive provenance");
        var staleSeasonStage = importer.Import(sourcePath, project, document, family); document.SetVerified(true); project.Season = 2026; await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptGame(project, staleSeasonStage, staleSeasonStage.Games.Single(x => x.Week == 2), document, family)), "Project season change after staging prevents acceptance"); True(project.AcceptedDefensiveGames.All(x => x.Week != 2), "Failed stale-season acceptance establishes no new authority"); project.Season = 2025;

        var storePath = Path.Combine(root, "wp3-valid.json"); var store = new ProjectStore(storePath); var workspace = new BuilderWorkspace { SourceFamilies = [family], Projects = [project], ActiveProjectId = project.Id }; await store.SaveAsync(workspace); var loaded = await store.LoadAsync();
        Equal(2, loaded.ActiveProject!.AcceptedDefensiveGames.Count, "Defensive authority history round trips"); Equal(1, loaded.ActiveProject.AcceptedDefensiveGames.Count(x => x.IsCurrentAuthority), "Exactly one current game authority survives reload");
        var loadedFixture = loaded.ActiveProject.StagedDefensiveWorkbooks.Last(); var loadedHomer = loadedFixture.Games.Single(x => x.Week == 2).Players.Single(x => x.PlayerName == "Jamall Carey"); var loadedMansfield = loadedFixture.Games.Single(x => x.Week == 1).Players.Single(x => x.PlayerName == "Tyree Davis"); var loadedNorthWebster = loadedFixture.Games.Single(x => x.Week == 3).Players.Single(x => x.PlayerName == "Kameron Harris");
        Equal(DefensiveCellState.PresentBlank, loadedHomer.Solo.State, "Present blank survives persistence"); Equal(DefensiveCellState.Numeric, loadedHomer.Total.State, "Formula cached numeric state survives persistence"); Equal(0m, loadedHomer.Total.Numeric, "Formula cached zero survives persistence without changing blank Solo"); True(loadedMansfield.Solo.IsExplicitZero, "Explicit zero survives persistence"); Equal(DefensiveCellState.Absent, loadedNorthWebster.QuarterbackHurries.State, "Absent cell survives persistence"); Equal(4m, loadedNorthWebster.Solo.Numeric, "Nonzero numeric cell survives persistence");
        var validJson = await File.ReadAllTextAsync(storePath); var invalidNode = JsonNode.Parse(validJson)!.AsObject(); invalidNode["Projects"]![0]!["AcceptedDefensiveGames"]![0]!["ImportRecordId"] = Guid.NewGuid(); var invalidPath = Path.Combine(root, "wp3-orphan.json"); await File.WriteAllTextAsync(invalidPath, invalidNode.ToJsonString()); await ThrowsAsync<InvalidDataException>(() => new ProjectStore(invalidPath).LoadAsync(), "Orphan accepted defensive provenance is rejected");
        var nullNode = JsonNode.Parse(validJson)!.AsObject(); nullNode["Projects"]![0]!["StagedDefensiveWorkbooks"]![0]!["Games"] = null; var nullPath = Path.Combine(root, "wp3-null.json"); await File.WriteAllTextAsync(nullPath, nullNode.ToJsonString()); await ThrowsAsync<InvalidDataException>(() => new ProjectStore(nullPath).LoadAsync(), "Null WP 3 nested collections are rejected");
        foreach (var name in new[] { "StagedDefensiveWorkbooks", "AcceptedDefensiveGames", "AcceptedDefensiveSeasonTotals" }) await RejectedWp3($"omitted-{name}.json", node => node["Projects"]![0]!.AsObject().Remove(name), $"Omitted schema-3 {name} is rejected");
        await RejectedWp3("omitted-staged-players.json", node => node["Projects"]![0]!["StagedDefensiveWorkbooks"]![0]!["Games"]![0]!.AsObject().Remove("Players"), "Omitted staged Players is rejected");
        await RejectedWp3("omitted-staged-issues.json", node => node["Projects"]![0]!["StagedDefensiveWorkbooks"]![0]!["Games"]![0]!.AsObject().Remove("Issues"), "Omitted staged Issues is rejected");
        await RejectedWp3("omitted-stat.json", node => node["Projects"]![0]!["StagedDefensiveWorkbooks"]![0]!["Games"]![0]!["Players"]![0]!.AsObject().Remove("Solo"), "Omitted defensive stat member is rejected");
        await RejectedWp3("omitted-stat-state.json", node => node["Projects"]![0]!["StagedDefensiveWorkbooks"]![0]!["Games"]![0]!["Players"]![0]!["Solo"]!.AsObject().Remove("State"), "Omitted DefensiveSourceValue member is rejected");
        await RejectedWp3("null-stat.json", node => node["Projects"]![0]!["StagedDefensiveWorkbooks"]![0]!["Games"]![0]!["Players"]![0]!["Solo"] = null, "Explicit null defensive stat is rejected");
        await RejectedWp3("omitted-accepted-players.json", node => node["Projects"]![0]!["AcceptedDefensiveGames"]![0]!.AsObject().Remove("Players"), "Accepted snapshot with omitted Players is rejected");
        await RejectedWp3("emptied-accepted-players.json", node => node["Projects"]![0]!["AcceptedDefensiveGames"]![0]!["Players"] = new JsonArray(), "Accepted snapshot cannot be emptied while retaining lineage");
        await RejectedWp3("empty-pending-game.json", node => { var game = node["Projects"]![0]!["StagedDefensiveWorkbooks"]![0]!["Games"]![0]!; game["Players"] = new JsonArray(); game["Issues"] = new JsonArray(); }, "Pending game staging with empty Players is rejected independently of issues");
        await RejectedWp3("empty-pending-totals.json", node => { var totals = node["Projects"]![0]!["StagedDefensiveWorkbooks"]![0]!["SeasonTotals"]!; totals["Players"] = new JsonArray(); totals["Issues"] = new JsonArray(); }, "Pending TOTALS staging with empty Players is rejected independently of issues");
        await RejectedWp3("empty-accepted-game.json", node => { var owner = node["Projects"]![0]!; var accepted = owner["AcceptedDefensiveGames"]![0]!; var workbookId = accepted["StagedWorkbookId"]!.GetValue<Guid>(); var sectionId = accepted["StagedSectionId"]!.GetValue<Guid>(); var staged = owner["StagedDefensiveWorkbooks"]!.AsArray().Select(x => x!).Single(x => x["Id"]!.GetValue<Guid>() == workbookId)["Games"]!.AsArray().Select(x => x!).Single(x => x["Id"]!.GetValue<Guid>() == sectionId); staged["Players"] = new JsonArray(); accepted["Players"] = new JsonArray(); }, "Matching empty accepted game staging and snapshot are rejected");
        await RejectedWp3("empty-accepted-totals.json", node => { var owner = node["Projects"]![0]!; var accepted = owner["AcceptedDefensiveSeasonTotals"]![0]!; var workbookId = accepted["StagedWorkbookId"]!.GetValue<Guid>(); var staged = owner["StagedDefensiveWorkbooks"]!.AsArray().Select(x => x!).Single(x => x["Id"]!.GetValue<Guid>() == workbookId)["SeasonTotals"]!; staged["Players"] = new JsonArray(); accepted["Players"] = new JsonArray(); }, "Matching empty accepted TOTALS staging and snapshot are rejected");
        await RejectedWp3("changed-accepted-stat.json", node => node["Projects"]![0]!["AcceptedDefensiveGames"]![0]!["Players"]![0]!["Solo"]!["Numeric"] = 999, "Accepted defensive content must match staging");
        await RejectedWp3("changed-accepted-formula.json", node => node["Projects"]![0]!["AcceptedDefensiveGames"]![0]!["Players"]![0]!["Total"]!["Formula"] = "1+1", "Accepted source-formula evidence must match staging");
        await RejectedWp3("changed-accepted-issues.json", node => node["Projects"]![0]!["AcceptedDefensiveGames"]![0]!["AcceptedIssues"]!.AsArray().Add(new JsonObject { ["Severity"] = "Informational", ["Code"] = "Injected", ["Section"] = "test", ["Message"] = "Injected" }), "Accepted validation issues must match staging");

        var schemaTwoNode = JsonNode.Parse(validJson)!.AsObject(); schemaTwoNode["SchemaVersion"] = 2; foreach (var p in schemaTwoNode["Projects"]!.AsArray().Select(x => x!.AsObject())) { p.Remove("StagedDefensiveWorkbooks"); p.Remove("AcceptedDefensiveGames"); p.Remove("AcceptedDefensiveSeasonTotals"); }
        var schemaTwoPath = Path.Combine(root, "schema-two.json"); await File.WriteAllTextAsync(schemaTwoPath, schemaTwoNode.ToJsonString()); var schemaTwo = await new ProjectStore(schemaTwoPath).LoadAsync(); Equal(3, schemaTwo.SchemaVersion, "Schema 2 explicitly migrates to schema 3"); True(schemaTwo.ActiveProject!.StagedDefensiveWorkbooks.Count == 0, "Schema 2 migration fabricates no defensive information");

        async Task RejectedWp3(string name, Action<JsonObject> mutate, string message) { var node = JsonNode.Parse(validJson)!.AsObject(); mutate(node); var path = Path.Combine(root, name); var content = node.ToJsonString(); await File.WriteAllTextAsync(path, content); await ThrowsAsync<InvalidDataException>(() => new ProjectStore(path).LoadAsync(), message); Equal(content, await File.ReadAllTextAsync(path), $"{message} without rewriting the source file"); }

        var unhealthyReplacementStage = importer.Import(sourcePath, project, document, family); document.SetVerified(true); File.Delete(sourcePath); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptGame(project, unhealthyReplacementStage, unhealthyReplacementStage.Games.Single(x => x.Week == 1), document, family, replace: true)), "Source deletion immediately before defensive replacement is rejected"); True(replacement.IsCurrentAuthority && project.AcceptedDefensiveGames.Count(x => x.IsCurrentAuthority && x.Season == 2025 && x.Week == 1) == 1, "Failed source-health replacement leaves prior authority current");

        var syntheticPath = Path.Combine(sourceRoot, "synthetic.xlsx"); CreateDefensiveWorkbook(syntheticPath, duplicate: false, totalMismatch: false); var syntheticDocument = new ExpectedSourceDocument { Name = "Synthetic defensive", SourceFamilyId = family.Id, ExpectedLocator = "synthetic.xlsx" }; var syntheticProject = CompleteProject(); syntheticProject.Season = 2026; syntheticProject.ExpectedDocuments.Add(syntheticDocument); var synthetic = importer.Import(syntheticPath, syntheticProject, syntheticDocument, family);
        Equal(1, synthetic.Games.Count, "Reordered synthetic game worksheet is recognized"); True(synthetic.SeasonTotals is null && synthetic.Issues.Any(x => x.Code == "SeasonTotalsMissing"), "Missing TOTALS prevents totals staging without blocking games");
        var syntheticLine = synthetic.Games.Single().Players.Single(); Equal(7, syntheticLine.SourceRow, "Player row is not fixed"); Equal(1.5m, syntheticLine.Solo.Numeric, "Decimal defensive value is preserved"); Equal(DefensiveCellState.Absent, syntheticLine.QuarterbackHurries.State, "Absent cell is preserved"); Equal(DefensiveCellState.PresentBlank, syntheticLine.PassBreakups.State, "Present blank cell is preserved separately"); True(syntheticLine.Interceptions.IsExplicitZero, "Synthetic explicit zero is preserved"); True(!synthetic.Games.Single().HasBlockingIssues, "Missing TOTALS does not invalidate a valid game section");

        var malformedPath = Path.Combine(sourceRoot, "malformed.xlsx"); CreateDefensiveWorkbook(malformedPath, duplicate: true, totalMismatch: true, invalidStat: true, omitBk: true); var malformedDocument = new ExpectedSourceDocument { Name = "Malformed defensive", SourceFamilyId = family.Id, ExpectedLocator = "malformed.xlsx" }; var malformedProject = CompleteProject(); malformedProject.Season = 2026; malformedProject.ExpectedDocuments.Add(malformedDocument); var malformed = importer.Import(malformedPath, malformedProject, malformedDocument, family).Games.Single();
        True(malformed.Issues.Any(x => x.Code == "DuplicatePlayerIdentity" && x.Severity == InformationIssueSeverity.Blocking), "Duplicate player identity blocks game acceptance"); True(malformed.Issues.Any(x => x.Code == "DefensiveTotalDiscrepancy" && x.Severity == InformationIssueSeverity.Advisory), "Total discrepancy is surfaced without correction"); True(malformed.Issues.Any(x => x.Code == "DefensiveStatInvalid"), "Invalid numeric source value is surfaced"); True(malformed.Issues.Any(x => x.Code == "RequiredDefensiveHeadingMissing"), "Missing required heading blocks the malformed game only");

        var emptyGame = ImportSectionWorkbook("empty-game.xlsx", emptyGame: true); var emptyGameSection = emptyGame.Stage.Games.Single(); True(emptyGameSection.Issues.Any(x => x.Code == "DefensivePlayersMissing" && x.Severity == InformationIssueSeverity.Blocking), "Parser-generated empty game section is blocking"); emptyGame.Document.SetVerified(true); emptyGameSection.Issues.Clear(); True(!workflow.CanAcceptGame(emptyGame.Project, emptyGame.Stage, emptyGameSection, emptyGame.Document, false), "Directly constructed empty game cannot establish authority without parser issues"); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptGame(emptyGame.Project, emptyGame.Stage, emptyGameSection, emptyGame.Document, emptyGame.Family)), "Empty game acceptance operation rejects independently of parser issues"); True(emptyGame.Project.AcceptedDefensiveGames.Count == 0, "Failed empty game acceptance establishes no authority");
        var emptyTotals = ImportSectionWorkbook("empty-totals.xlsx", emptyTotals: true); var emptyTotalsSection = emptyTotals.Stage.SeasonTotals!; True(emptyTotalsSection.Issues.Any(x => x.Code == "DefensivePlayersMissing" && x.Severity == InformationIssueSeverity.Blocking), "Parser-generated empty TOTALS section is blocking"); emptyTotals.Document.SetVerified(true); emptyTotalsSection.Issues.Clear(); True(!workflow.CanAcceptSeasonTotals(emptyTotals.Project, emptyTotals.Stage, emptyTotals.Document, false), "Directly constructed empty TOTALS cannot establish authority without parser issues"); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptSeasonTotals(emptyTotals.Project, emptyTotals.Stage, emptyTotals.Document, emptyTotals.Family)), "Empty TOTALS acceptance operation rejects independently of parser issues"); True(emptyTotals.Project.AcceptedDefensiveSeasonTotals.Count == 0, "Failed empty TOTALS acceptance establishes no authority");

        var badTotals = ImportSectionWorkbook("bad-totals.xlsx", malformedTotals: true); badTotals.Document.SetVerified(true); Equal("Game", badTotals.Stage.Games.Single().WorksheetName, "Workbook relationships resolve a game stored in non-sheet1.xml physical part"); Equal(8, badTotals.Stage.Games.Single().Players.Single().SourceRow, "Relationship-based parser remains independent of player row"); True(badTotals.Stage.SeasonTotals!.HasBlockingIssues && !badTotals.Stage.Games.Single().HasBlockingIssues, "Malformed TOTALS does not invalidate valid game staging"); var independentGame = workflow.AcceptGame(badTotals.Project, badTotals.Stage, badTotals.Stage.Games.Single(), badTotals.Document, badTotals.Family); True(independentGame.IsCurrentAuthority, "Valid game is independently acceptable when TOTALS is malformed");
        var badGame = ImportSectionWorkbook("bad-game.xlsx", malformedGame: true); badGame.Document.SetVerified(true); True(badGame.Stage.Games.Single().HasBlockingIssues && !badGame.Stage.SeasonTotals!.HasBlockingIssues, "Malformed game does not invalidate valid TOTALS staging"); var independentTotals = workflow.AcceptSeasonTotals(badGame.Project, badGame.Stage, badGame.Document, badGame.Family); True(independentTotals.IsCurrentAuthority, "Valid TOTALS is independently acceptable when game is malformed");

        var totalsReplacementCase = ImportSectionWorkbook("totals-replacement.xlsx", totalsSolo: 2m); totalsReplacementCase.Document.SetVerified(true); True(totalsReplacementCase.Stage.SeasonTotals!.Issues.Any(x => x.Code == "SeasonGameAggregationDiscrepancy"), "Complete game/TOTALS discrepancy is advisory evidence"); var originalTotals = workflow.AcceptSeasonTotals(totalsReplacementCase.Project, totalsReplacementCase.Stage, totalsReplacementCase.Document, totalsReplacementCase.Family, "Reviewed discrepancy"); var totalsReplacementStage = importer.Import(totalsReplacementCase.Path, totalsReplacementCase.Project, totalsReplacementCase.Document, totalsReplacementCase.Family); totalsReplacementCase.Document.SetVerified(true); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptSeasonTotals(totalsReplacementCase.Project, totalsReplacementStage, totalsReplacementCase.Document, totalsReplacementCase.Family, "Reviewed discrepancy")), "TOTALS reimport cannot silently replace authority"); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptSeasonTotals(totalsReplacementCase.Project, totalsReplacementStage, totalsReplacementCase.Document, totalsReplacementCase.Family, replace: true)), "Advisory TOTALS replacement requires a review note"); True(originalTotals.IsCurrentAuthority, "Failed advisory TOTALS replacement leaves existing authority unchanged"); var replacementTotals = workflow.AcceptSeasonTotals(totalsReplacementCase.Project, totalsReplacementStage, totalsReplacementCase.Document, totalsReplacementCase.Family, "Replacement discrepancy reviewed", replace: true); True(!originalTotals.IsCurrentAuthority && replacementTotals.IsCurrentAuthority && totalsReplacementCase.Project.AcceptedDefensiveSeasonTotals.Count == 2, "Explicit TOTALS replacement preserves superseded history"); var emptyTotalsReplacement = importer.Import(totalsReplacementCase.Path, totalsReplacementCase.Project, totalsReplacementCase.Document, totalsReplacementCase.Family); totalsReplacementCase.Document.SetVerified(true); emptyTotalsReplacement.SeasonTotals!.Players.Clear(); emptyTotalsReplacement.SeasonTotals.Issues.Clear(); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptSeasonTotals(totalsReplacementCase.Project, emptyTotalsReplacement, totalsReplacementCase.Document, totalsReplacementCase.Family, "Empty replacement reviewed", replace: true)), "Empty TOTALS replacement cannot supersede existing authority"); True(replacementTotals.IsCurrentAuthority && totalsReplacementCase.Project.AcceptedDefensiveSeasonTotals.Count == 2, "Failed empty TOTALS replacement leaves existing authority untouched");

        var partialComparison = ImportSectionWorkbook("partial-comparison.xlsx", totalsSolo: 2m, includeSecondGame: true); True(!partialComparison.Stage.SeasonTotals!.Issues.Any(x => x.Code == "SeasonGameAggregationDiscrepancy" && x.Message.Contains("Fixture Player Solo")), "Player absence from a game suppresses incomplete aggregation discrepancy");

        var opponentA = ImportSectionWorkbook("opponent-a.xlsx", opponent: "Opponent A", includeTotals: false); opponentA.Document.SetVerified(true); var currentOpponent = workflow.AcceptGame(opponentA.Project, opponentA.Stage, opponentA.Stage.Games.Single(), opponentA.Document, opponentA.Family); var emptyGameReplacement = importer.Import(opponentA.Path, opponentA.Project, opponentA.Document, opponentA.Family); opponentA.Document.SetVerified(true); emptyGameReplacement.Games.Single().Players.Clear(); emptyGameReplacement.Games.Single().Issues.Clear(); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptGame(opponentA.Project, emptyGameReplacement, emptyGameReplacement.Games.Single(), opponentA.Document, opponentA.Family, replace: true)), "Empty game replacement cannot supersede existing authority"); True(currentOpponent.IsCurrentAuthority && opponentA.Project.AcceptedDefensiveGames.Count == 1, "Failed empty game replacement leaves existing authority untouched"); var opponentBPath = Path.Combine(sourceRoot, "opponent-b.xlsx"); CreateSectionWorkbook(opponentBPath, opponent: "Opponent B", includeTotals: false); var opponentBDocument = new ExpectedSourceDocument { Name = "Opponent B", SourceFamilyId = family.Id, ExpectedLocator = "opponent-b.xlsx" }; opponentA.Project.ExpectedDocuments.Add(opponentBDocument); var opponentBStage = importer.Import(opponentBPath, opponentA.Project, opponentBDocument, family); opponentBDocument.SetVerified(true); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptGame(opponentA.Project, opponentBStage, opponentBStage.Games.Single(), opponentBDocument, family, replace: true)), "Conflicting opponent cannot replace the same season/week authority"); True(currentOpponent.IsCurrentAuthority, "Opponent-conflict failure leaves existing authority unchanged");

        var lossRoot = Path.Combine(root, "defensive-loss"); Directory.CreateDirectory(lossRoot); var lossPath = Path.Combine(lossRoot, "loss.xlsx"); File.Copy(fixture, lossPath); var lossFamily = new SourceFamilyConfiguration { Name = "Loss", RootPath = lossRoot }; var lossDocument = new ExpectedSourceDocument { Name = "Loss", SourceFamilyId = lossFamily.Id, ExpectedLocator = "loss.xlsx" }; var lossProject = CompleteProject(); lossProject.Season = 2025; lossProject.ExpectedDocuments.Add(lossDocument); var lossStage = importer.Import(lossPath, lossProject, lossDocument, lossFamily); lossDocument.SetVerified(true); File.Delete(lossPath);
        await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptGame(lossProject, lossStage, lossStage.Games.First(), lossDocument, lossFamily)), "Source deletion immediately before defensive acceptance is rejected"); True(lossProject.AcceptedDefensiveGames.Count == 0 && lossDocument.Status == SourceDocumentStatus.Missing, "Failed source-health acceptance creates no authority and records missing status");

        var historicalReload = await store.LoadAsync(); True(!historicalReload.ActiveProject!.ExpectedDocuments.Single(x => x.Id == document.Id).HasHealthySource, "Reload refreshes deleted defensive source health"); var historicalSupply = AcceptedDefensiveInformationSupply.Build(historicalReload.ActiveProject, 2025); True(historicalSupply.Games.Count == 1 && historicalSupply.SeasonTotals is not null && historicalSupply.Provenance.Count >= 2, "Accepted defensive history and provenance survive source loss and reload");

        var staleCase = ImportSectionWorkbook("stale-replacement.xlsx", includeTotals: false); staleCase.Document.SetVerified(true); var staleOriginal = workflow.AcceptGame(staleCase.Project, staleCase.Stage, staleCase.Stage.Games.Single(), staleCase.Document, staleCase.Family); var staleReplacementStage = importer.Import(staleCase.Path, staleCase.Project, staleCase.Document, staleCase.Family); staleCase.Document.SetVerified(true); staleCase.Document.ExpectedAsOfUtc = DateTime.UtcNow.AddHours(1); await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(workflow.AcceptGame(staleCase.Project, staleReplacementStage, staleReplacementStage.Games.Single(), staleCase.Document, staleCase.Family, replace: true)), "Stale source immediately before defensive replacement is rejected"); True(staleOriginal.IsCurrentAuthority && staleCase.Project.AcceptedDefensiveGames.Count == 1, "Stale-source replacement leaves existing authority unchanged");

        (string Path, SourceFamilyConfiguration Family, ExpectedSourceDocument Document, GameNotesProject Project, StagedDefensiveWorkbook Stage) ImportSectionWorkbook(string name, bool emptyGame = false, bool emptyTotals = false, bool malformedGame = false, bool malformedTotals = false, string opponent = "Fixture Opponent", decimal totalsSolo = 1m, bool includeTotals = true, bool includeSecondGame = false)
        {
            var path = Path.Combine(sourceRoot, name); CreateSectionWorkbook(path, emptyGame, emptyTotals, malformedGame, malformedTotals, opponent, 1m, totalsSolo, includeTotals, includeSecondGame); var doc = new ExpectedSourceDocument { Name = name, SourceFamilyId = family.Id, ExpectedLocator = name }; var owner = CompleteProject(); owner.Season = 2026; owner.ExpectedDocuments.Add(doc); return (path, family, doc, owner, importer.Import(path, owner, doc, family));
        }
    }

    private static void CreateDefensiveWorkbook(string path, bool duplicate, bool totalMismatch, bool invalidStat = false, bool omitBk = false)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write("xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Flexible" sheetId="1" r:id="rId1"/></sheets></workbook>""");
        Write("xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
        var total = totalMismatch ? "9" : "3.5"; var duplicateRow = duplicate ? "<row r=\"12\"><c r=\"E12\" t=\"inlineStr\"><is><t>Flexible Player</t></is></c><c r=\"F12\"><v>8</v></c></row>" : ""; var sacks = invalidStat ? "<c r=\"B7\" t=\"inlineStr\"><is><t>bad</t></is></c>" : "<c r=\"B7\"><v>0.5</v></c>"; var bkHeading = omitBk ? "" : "<c r=\"N4\" t=\"inlineStr\"><is><t>BK</t></is></c>";
        Write("xl/worksheets/sheet1.xml", $"""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="C1" t="inlineStr"><is><t>WEEK 4 - at Flexible Opponent</t></is></c></row><row r="4"><c r="A4" t="inlineStr"><is><t>Total</t></is></c><c r="B4" t="inlineStr"><is><t>Sacks</t></is></c><c r="C4" t="inlineStr"><is><t>Assisted</t></is></c><c r="D4" t="inlineStr"><is><t>Solo</t></is></c><c r="E4" t="inlineStr"><is><t>Name</t></is></c><c r="F4" t="inlineStr"><is><t>#</t></is></c><c r="G4" t="inlineStr"><is><t>TFL</t></is></c><c r="H4" t="inlineStr"><is><t>Hurry</t></is></c><c r="I4" t="inlineStr"><is><t>PBU</t></is></c><c r="J4" t="inlineStr"><is><t>INT</t></is></c><c r="K4" t="inlineStr"><is><t>FF</t></is></c><c r="L4" t="inlineStr"><is><t>FR</t></is></c><c r="M4" t="inlineStr"><is><t>BEP</t></is></c>{bkHeading}</row><row r="7"><c r="A7"><f>SUM(C7:D7)</f><v>{total}</v></c>{sacks}<c r="C7"><v>2</v></c><c r="D7"><v>1.5</v></c><c r="E7" t="inlineStr"><is><t>Flexible Player</t></is></c><c r="F7"><v>7</v></c><c r="G7"><v>0.5</v></c><c r="I7"/><c r="J7"><v>0</v></c><c r="K7"><v>0</v></c><c r="L7"><v>0</v></c><c r="M7"><v>0</v></c><c r="N7"><v>0</v></c></row>{duplicateRow}</sheetData></worksheet>""");
        void Write(string name, string content) { var entry = zip.CreateEntry(name); using var stream = entry.Open(); using var writer = new StreamWriter(stream, Encoding.UTF8); writer.Write(content); }
    }

    private static void CreateSectionWorkbook(string path, bool emptyGame = false, bool emptyTotals = false, bool malformedGame = false, bool malformedTotals = false, string opponent = "Fixture Opponent", decimal gameSolo = 1m, decimal totalsSolo = 1m, bool includeTotals = true, bool includeSecondGame = false)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create); var sheets = $"<sheet name=\"Game\" sheetId=\"1\" r:id=\"rGame\"/>{(includeSecondGame ? "<sheet name=\"Second\" sheetId=\"2\" r:id=\"rSecond\"/>" : "")}{(includeTotals ? "<sheet name=\"TOTALS\" sheetId=\"3\" r:id=\"rTotals\"/>" : "")}"; var rels = $"<Relationship Id=\"rGame\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet9.xml\"/>{(includeSecondGame ? "<Relationship Id=\"rSecond\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet7.xml\"/>" : "")}{(includeTotals ? "<Relationship Id=\"rTotals\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet4.xml\"/>" : "")}";
        Write("xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>{sheets}</sheets></workbook>"); Write("xl/_rels/workbook.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{rels}</Relationships>");
        Write("xl/worksheets/sheet9.xml", Sheet($"WEEK 4 - at {opponent}", emptyGame, malformedGame, "Fixture Player", gameSolo));
        if (includeSecondGame) Write("xl/worksheets/sheet7.xml", Sheet("WEEK 5 - Second Opponent", false, false, "Other Player", 1m));
        if (includeTotals) Write("xl/worksheets/sheet4.xml", Sheet("2026 - TOTALS", emptyTotals, malformedTotals, "Fixture Player", totalsSolo));
        static string Sheet(string identity, bool empty, bool malformed, string player, decimal solo)
        {
            var headings = new[] { "Name", "#", "Solo", "Assisted", "Total", "TFL", "Sacks", "Hurry", "PBU", "INT", "FF", "FR", "BEP", "BK" }; var header = string.Concat(headings.Select((x, i) => malformed && i == 13 ? "" : $"<c r=\"{(char)('A' + i)}3\" t=\"inlineStr\"><is><t>{x}</t></is></c>"));
            var row = empty ? "" : $"<row r=\"8\"><c r=\"A8\" t=\"inlineStr\"><is><t>{player}</t></is></c><c r=\"B8\"><v>7</v></c><c r=\"C8\"><v>{solo.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c><c r=\"D8\"><v>0</v></c><c r=\"E8\"><f>SUM(C8:D8)</f><v>{solo.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c><c r=\"F8\"><v>0</v></c><c r=\"G8\"><v>0</v></c><c r=\"H8\"><v>0</v></c><c r=\"I8\"><v>0</v></c><c r=\"J8\"><v>0</v></c><c r=\"K8\"><v>0</v></c><c r=\"L8\"><v>0</v></c><c r=\"M8\"><v>0</v></c><c r=\"N8\"><v>0</v></c></row>";
            return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"D1\" t=\"inlineStr\"><is><t>{identity}</t></is></c></row><row r=\"3\">{header}</row>{row}</sheetData></worksheet>";
        }
        void Write(string name, string content) { var entry = zip.CreateEntry(name); using var stream = entry.Open(); using var writer = new StreamWriter(stream, Encoding.UTF8); writer.Write(content); }
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

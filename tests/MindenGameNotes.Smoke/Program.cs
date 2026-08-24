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
            RenderingSmoke(root, proofProject);
            Console.WriteLine($"PASS: WP 1 smoke suite ({assertions} assertions)");
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
        await Rejected("future.json", "{\"SchemaVersion\":2,\"ActiveProjectId\":null,\"SourceFamilies\":[],\"Projects\":[],\"UpdatedUtc\":\"2026-01-01T00:00:00Z\"}", "Unsupported future schema is rejected");

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

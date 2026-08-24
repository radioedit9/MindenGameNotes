using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MindenGameNotes;

public sealed partial class DefensiveWorkbookParser
{
    private static readonly string[] Required = ["name", "number", "solo", "assisted", "total", "tfl", "sacks", "hurry", "pbu", "int", "ff", "fr", "bep", "bk"];

    public StagedDefensiveWorkbook Parse(string path, GameNotesProject project, ExpectedSourceDocument document, SourceFamilyConfiguration family, ImportRecord import)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = ReadSharedStrings(zip);
        var sheets = ReadSheetRelationships(zip);
        var staged = new StagedDefensiveWorkbook { ProjectId = project.Id, ExpectedDocumentId = document.Id, SourceFamilyId = family.Id, ImportRecordId = import.Id };
        var parsed = new List<(string Name, string Identity, Dictionary<string, string> Headers, List<DefensiveStatLine> Players, List<InformationValidationIssue> Issues)>();
        foreach (var sheet in sheets)
        {
            var entry = zip.GetEntry(sheet.Target) ?? throw new InvalidDataException($"Worksheet part '{sheet.Target}' is missing.");
            using var stream = entry.Open(); var xml = XDocument.Load(stream);
            var rows = xml.Descendants().Where(x => x.Name.LocalName == "row").ToList();
            var cells = rows.Select(r => new SheetRow((int?)r.Attribute("r") ?? 0, r.Elements().Where(x => x.Name.LocalName == "c").ToDictionary(CellColumn, c => ReadCell(c, shared), StringComparer.OrdinalIgnoreCase))).ToList();
            var identity = cells.SelectMany(x => x.Cells.Values).Select(x => x.Raw.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
            var headerRow = cells.FirstOrDefault(r => Required.Count(key => r.Cells.Values.Any(c => NormalizeHeading(c.Raw) == key)) >= 5);
            if (headerRow is null)
            {
                if (WeekIdentity().IsMatch(identity) || sheet.Name.Equals("TOTALS", StringComparison.OrdinalIgnoreCase))
                    parsed.Add((sheet.Name, identity, [], [], [Issue(InformationIssueSeverity.Blocking, "DefensiveHeadingsMissing", sheet.Name, "The worksheet does not contain a recognizable defensive heading row.")]));
                continue;
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var issues = new List<InformationValidationIssue>();
            foreach (var cell in headerRow.Cells)
            {
                var key = NormalizeHeading(cell.Value.Raw);
                if (!Required.Contains(key)) continue;
                if (!headers.TryAdd(key, cell.Key)) issues.Add(Issue(InformationIssueSeverity.Blocking, "DuplicateDefensiveHeading", sheet.Name, $"Heading '{cell.Value.Raw}' occurs more than once."));
            }
            foreach (var key in Required.Where(x => !headers.ContainsKey(x))) issues.Add(Issue(InformationIssueSeverity.Blocking, "RequiredDefensiveHeadingMissing", sheet.Name, $"Required defensive heading '{key}' is missing."));
            var players = new List<DefensiveStatLine>();
            foreach (var row in cells.Where(x => x.Number > headerRow.Number))
            {
                var name = Source(row, headers.GetValueOrDefault("name"));
                var jersey = Source(row, headers.GetValueOrDefault("number"));
                if (name.State is DefensiveCellState.Absent or DefensiveCellState.PresentBlank)
                {
                    if (Required.Where(x => x is not ("name" or "number" or "total")).Select(x => Source(row, headers.GetValueOrDefault(x))).Any(x => x.State is DefensiveCellState.Numeric or DefensiveCellState.Invalid)) issues.Add(Issue(InformationIssueSeverity.Blocking, "PlayerNameMissing", sheet.Name, $"Row {row.Number} contains defensive information without a player name."));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(name.Raw)) { issues.Add(Issue(InformationIssueSeverity.Blocking, "PlayerNameInvalid", sheet.Name, $"Row {row.Number} has an invalid player name.")); continue; }
                var line = new DefensiveStatLine
                {
                    PlayerName = name.Raw.Trim(), JerseyNumber = jersey.Raw.Trim(), WorksheetName = sheet.Name, SourceRow = row.Number,
                    Solo = Source(row, headers.GetValueOrDefault("solo")), Assisted = Source(row, headers.GetValueOrDefault("assisted")), Total = Source(row, headers.GetValueOrDefault("total")),
                    TacklesForLoss = Source(row, headers.GetValueOrDefault("tfl")), Sacks = Source(row, headers.GetValueOrDefault("sacks")), QuarterbackHurries = Source(row, headers.GetValueOrDefault("hurry")),
                    PassBreakups = Source(row, headers.GetValueOrDefault("pbu")), Interceptions = Source(row, headers.GetValueOrDefault("int")), ForcedFumbles = Source(row, headers.GetValueOrDefault("ff")),
                    FumbleRecoveries = Source(row, headers.GetValueOrDefault("fr")), BlockedExtraPoints = Source(row, headers.GetValueOrDefault("bep")), BlockedKicks = Source(row, headers.GetValueOrDefault("bk"))
                };
                players.Add(line); ValidateLine(line, issues);
            }
            foreach (var duplicate in players.GroupBy(x => NormalizePlayer(x.PlayerName)).Where(x => x.Count() > 1)) issues.Add(Issue(InformationIssueSeverity.Blocking, "DuplicatePlayerIdentity", sheet.Name, $"Player identity '{duplicate.First().PlayerName}' occurs more than once."));
            if (players.Count == 0) issues.Add(Issue(InformationIssueSeverity.Blocking, "DefensivePlayersMissing", sheet.Name, "The defensive section contains no valid player rows."));
            parsed.Add((sheet.Name, identity, headers, players, issues));
        }

        var totalCandidates = parsed.Where(x => x.Name.Equals("TOTALS", StringComparison.OrdinalIgnoreCase) || TotalsIdentity().IsMatch(x.Identity)).ToList();
        if (totalCandidates.Count == 1)
        {
            var source = totalCandidates[0]; var match = TotalsIdentity().Match(source.Identity);
            var totals = new StagedDefensiveSeasonTotals { WorksheetName = source.Name, IdentityText = source.Identity, Players = source.Players, Issues = source.Issues, Season = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null };
            if (totals.Season is null) totals.Issues.Add(Issue(InformationIssueSeverity.Blocking, "SeasonTotalsIdentityInvalid", source.Name, "The TOTALS worksheet does not identify its season."));
            staged.SeasonTotals = totals;
        }
        else staged.Issues.Add(Issue(InformationIssueSeverity.Advisory, totalCandidates.Count == 0 ? "SeasonTotalsMissing" : "SeasonTotalsAmbiguous", "workbook", totalCandidates.Count == 0 ? "No recognizable TOTALS worksheet was found; game sections may still be accepted." : "More than one TOTALS worksheet was recognized; season totals cannot be accepted."));

        foreach (var source in parsed.Except(totalCandidates))
        {
            var match = WeekIdentity().Match(source.Identity); var issues = source.Issues;
            var game = new StagedDefensiveGame { WorksheetName = source.Name, IdentityText = source.Identity, Players = source.Players, Issues = issues };
            if (match.Success)
            {
                game.Week = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); game.SiteIndicator = match.Groups[2].Value.Trim().ToLowerInvariant(); game.Opponent = match.Groups[3].Value.Trim();
                if (string.IsNullOrWhiteSpace(game.Opponent)) issues.Add(Issue(InformationIssueSeverity.Blocking, "GameOpponentMissing", source.Name, "The game worksheet identity has no opponent."));
            }
            else issues.Add(Issue(InformationIssueSeverity.Blocking, "GameSheetIdentityInvalid", source.Name, "The defensive worksheet title must identify WEEK and opponent."));
            staged.Games.Add(game);
        }
        foreach (var duplicate in staged.Games.Where(x => x.Week is not null).GroupBy(x => x.Week).Where(x => x.Count() > 1)) foreach (var game in duplicate) game.Issues.Add(Issue(InformationIssueSeverity.Blocking, "DuplicateDefensiveGameWeek", game.WorksheetName, $"Week {duplicate.Key} occurs more than once in the workbook."));
        ApplySeason(project, staged); ValidateCrossSheetIdentity(staged); CompareTotals(staged); return staged;
    }

    private static void ApplySeason(GameNotesProject project, StagedDefensiveWorkbook staged)
    {
        var sourceSeason = staged.SeasonTotals?.Season; var season = project.Season is >= 1900 and <= 2200 ? project.Season : sourceSeason;
        foreach (var game in staged.Games) game.Season = season;
        if (season is null) foreach (var game in staged.Games) game.Issues.Add(Issue(InformationIssueSeverity.Blocking, "DefensiveSeasonMissing", game.WorksheetName, "The game cannot be associated with a season."));
        if (project.Season is not null && sourceSeason is not null && project.Season != sourceSeason)
        {
            foreach (var game in staged.Games) game.Issues.Add(Issue(InformationIssueSeverity.Blocking, "DefensiveSeasonConflict", game.WorksheetName, $"Project season {project.Season} conflicts with source season {sourceSeason}."));
            staged.SeasonTotals!.Issues.Add(Issue(InformationIssueSeverity.Blocking, "DefensiveSeasonConflict", staged.SeasonTotals.WorksheetName, $"Project season {project.Season} conflicts with source season {sourceSeason}."));
        }
    }

    private static void CompareTotals(StagedDefensiveWorkbook staged)
    {
        if (staged.SeasonTotals is null || staged.Games.Count == 0) return;
        var selectors = StatSelectors();
        foreach (var totalLine in staged.SeasonTotals.Players)
        {
            var games = staged.Games.Select(game => game.Players.Where(x => NormalizePlayer(x.PlayerName) == NormalizePlayer(totalLine.PlayerName) && (string.IsNullOrWhiteSpace(totalLine.JerseyNumber) || string.IsNullOrWhiteSpace(x.JerseyNumber) || x.JerseyNumber == totalLine.JerseyNumber)).ToList()).ToList();
            foreach (var selector in selectors)
            {
                var expected = selector.Value(totalLine); if (games.Any(x => x.Count != 1)) continue; var values = games.Select(x => selector.Value(x[0])).ToList();
                if (expected.State != DefensiveCellState.Numeric || values.Any(x => x.State != DefensiveCellState.Numeric)) continue;
                var sum = values.Sum(x => x.Numeric!.Value); if (sum != expected.Numeric) staged.SeasonTotals.Issues.Add(Issue(InformationIssueSeverity.Advisory, "SeasonGameAggregationDiscrepancy", staged.SeasonTotals.WorksheetName, $"{totalLine.PlayerName} {selector.Key}: source TOTALS {expected.Numeric} differs from complete game-sheet aggregation {sum}; neither value was changed."));
            }
        }
    }

    private static void ValidateCrossSheetIdentity(StagedDefensiveWorkbook staged)
    {
        var lines = staged.Games.SelectMany(game => game.Players.Select(player => (Game: game, Player: player))).ToList();
        foreach (var conflict in lines.Where(x => !string.IsNullOrWhiteSpace(x.Player.JerseyNumber)).GroupBy(x => NormalizePlayer(x.Player.PlayerName)).Where(x => x.Select(y => y.Player.JerseyNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            foreach (var game in conflict.Select(x => x.Game).Distinct()) game.Issues.Add(Issue(InformationIssueSeverity.Advisory, "DefensiveJerseyConflict", game.WorksheetName, $"{conflict.First().Player.PlayerName} has conflicting jersey numbers across game worksheets."));
    }

    private static void ValidateLine(DefensiveStatLine line, List<InformationValidationIssue> issues)
    {
        foreach (var item in StatSelectors())
        {
            var value = item.Value(line);
            if (value.State == DefensiveCellState.Invalid || value.Numeric < 0) issues.Add(Issue(InformationIssueSeverity.Blocking, "DefensiveStatInvalid", line.WorksheetName, $"{line.PlayerName} {item.Key} at {value.CellReference} is not a valid nonnegative number: '{value.Raw}'."));
        }
        if (line.Solo.State == DefensiveCellState.Numeric && line.Assisted.State == DefensiveCellState.Numeric && line.Total.State == DefensiveCellState.Numeric && line.Total.Numeric != line.Solo.Numeric + line.Assisted.Numeric)
            issues.Add(Issue(InformationIssueSeverity.Advisory, "DefensiveTotalDiscrepancy", line.WorksheetName, $"{line.PlayerName} Total {line.Total.Numeric} differs from Solo + Assisted ({line.Solo.Numeric + line.Assisted.Numeric}); source values were not changed."));
    }

    private static Dictionary<string, Func<DefensiveStatLine, DefensiveSourceValue>> StatSelectors() => new()
    {
        ["Solo"] = x => x.Solo, ["Assisted"] = x => x.Assisted, ["Total"] = x => x.Total, ["TFL"] = x => x.TacklesForLoss, ["Sacks"] = x => x.Sacks,
        ["Hurry"] = x => x.QuarterbackHurries, ["PBU"] = x => x.PassBreakups, ["INT"] = x => x.Interceptions, ["FF"] = x => x.ForcedFumbles,
        ["FR"] = x => x.FumbleRecoveries, ["BEP"] = x => x.BlockedExtraPoints, ["BK"] = x => x.BlockedKicks
    };
    private static DefensiveSourceValue Source(SheetRow row, string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return new() { State = DefensiveCellState.Absent };
        if (!row.Cells.TryGetValue(column, out var cell)) return new() { State = DefensiveCellState.Absent, CellReference = column + row.Number };
        return cell;
    }
    private static DefensiveSourceValue ReadCell(XElement cell, List<string> shared)
    {
        var reference = (string?)cell.Attribute("r") ?? ""; var type = (string?)cell.Attribute("t"); var formula = cell.Elements().FirstOrDefault(x => x.Name.LocalName == "f")?.Value;
        var raw = cell.Elements().FirstOrDefault(x => x.Name.LocalName == "v")?.Value ?? string.Concat(cell.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value));
        if (type == "s" && int.TryParse(raw, out var index) && index >= 0 && index < shared.Count) raw = shared[index];
        if (raw.Length == 0) return new() { State = DefensiveCellState.PresentBlank, CellReference = reference, Formula = formula };
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric)) return new() { State = DefensiveCellState.Numeric, CellReference = reference, Raw = raw, Numeric = numeric, Formula = formula };
        return new() { State = DefensiveCellState.Invalid, CellReference = reference, Raw = raw, Formula = formula };
    }
    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml"); if (entry is null) return [];
        using var stream = entry.Open(); return XDocument.Load(stream).Descendants().Where(x => x.Name.LocalName == "si").Select(x => string.Concat(x.Descendants().Where(y => y.Name.LocalName == "t").Select(y => y.Value))).ToList();
    }
    private static List<(string Name, string Target)> ReadSheetRelationships(ZipArchive zip)
    {
        using var workbookStream = (zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("The workbook manifest is missing.")).Open(); var workbook = XDocument.Load(workbookStream);
        using var relStream = (zip.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("The workbook relationships are missing.")).Open(); var rels = XDocument.Load(relStream).Descendants().Where(x => x.Name.LocalName == "Relationship").ToDictionary(x => (string)x.Attribute("Id")!, x => (string)x.Attribute("Target")!);
        return workbook.Descendants().Where(x => x.Name.LocalName == "sheet").Select(x => { var id = x.Attributes().First(a => a.Name.LocalName == "id").Value; var target = rels[id].Replace('\\', '/').TrimStart('/'); if (!target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)) target = "xl/" + target.TrimStart('.', '/'); return ((string)x.Attribute("name")!, target); }).ToList();
    }
    private static string CellColumn(XElement cell) => Regex.Match((string?)cell.Attribute("r") ?? "", "^[A-Z]+").Value;
    private static string NormalizeHeading(string value)
    {
        if (value.Trim() == "#") return "number";
        return Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]", "") switch { "no" or "jersey" or "jerseynumber" => "number", "name" or "player" or "playername" => "name", "assist" or "assists" => "assisted", "qbhurry" or "hurries" => "hurry", var x => x };
    }
    private static string NormalizePlayer(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]", "");
    private static InformationValidationIssue Issue(InformationIssueSeverity severity, string code, string section, string message) => new() { Severity = severity, Code = code, Section = section, Message = message };
    private sealed record SheetRow(int Number, Dictionary<string, DefensiveSourceValue> Cells);
    [GeneratedRegex(@"^\s*WEEK\s+(\d+)\s*-\s*(?:(at|vs)\s+)?(.+?)\s*$", RegexOptions.IgnoreCase)] private static partial Regex WeekIdentity();
    [GeneratedRegex(@"^\s*(\d{4})\s*-\s*TOTALS\s*$", RegexOptions.IgnoreCase)] private static partial Regex TotalsIdentity();
}

public sealed class DefensiveWorkbookImportService
{
    public StagedDefensiveWorkbook Import(string path, GameNotesProject project, ExpectedSourceDocument document, SourceFamilyConfiguration family)
    {
        if (!project.ExpectedDocuments.Contains(document) || document.SourceFamilyId != family.Id) throw new InvalidOperationException("The selected defensive source does not belong to the active project/source family.");
        var fullPath = Path.GetFullPath(path); var resolved = document.ResolvePath(family);
        if (string.IsNullOrWhiteSpace(resolved) || !Path.GetFullPath(resolved).Equals(fullPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Resolve the defensive workbook against its current source configuration before importing it.");
        if (!Path.GetExtension(fullPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Defensive intake requires an .xlsx workbook.");
        document.ResolvedPath = fullPath; document.RefreshStatus(family); document.SetVerified(false);
        document.IsDefensiveWorkbook = true;
        var import = new ImportRecord { ProjectId = project.Id, SourceFamilyId = family.Id, ExpectedDocumentId = document.Id, FileName = Path.GetFileName(fullPath), SourceLocator = fullPath, SourceModifiedUtc = document.SourceModifiedUtc, ApplicableSeason = project.Season, ApplicableWeek = null, ImportedUtc = DateTime.UtcNow, Kind = "XLSX-DEFENSIVE" };
        var staged = new DefensiveWorkbookParser().Parse(fullPath, project, document, family, import); import.RowCount = staged.Games.Sum(x => x.Players.Count) + (staged.SeasonTotals?.Players.Count ?? 0);
        project.Imports.Insert(0, import); project.StagedDefensiveWorkbooks.Insert(0, staged); return staged;
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace MindenGameNotes;

public sealed class DefensiveInformationWorkflow
{
    public bool CanAcceptGame(GameNotesProject project, StagedDefensiveWorkbook workbook, StagedDefensiveGame game, ExpectedSourceDocument? source, bool replace)
    {
        if (!ValidCommon(project, workbook, source) || !workbook.Games.Contains(game) || game.State != ReportReviewState.PendingReview || game.Players.Count == 0 || game.HasBlockingIssues || game.Season is null || game.Week is null || !ValidSeason(project, workbook, game.Season.Value)) return false;
        var existing = CurrentGame(project, game.Season.Value, game.Week.Value);
        if (replace && existing is not null && GameInformationWorkflow.NormalizeOpponent(existing.Opponent) != GameInformationWorkflow.NormalizeOpponent(game.Opponent)) return false;
        return replace ? existing is not null : existing is null;
    }

    public AcceptedDefensiveGame AcceptGame(GameNotesProject project, StagedDefensiveWorkbook workbook, StagedDefensiveGame game, ExpectedSourceDocument source, SourceFamilyConfiguration family, string note = "", bool replace = false)
    {
        ValidateSource(project, workbook, source, family); source.RefreshStatus(family);
        if (!source.HasHealthySource || source.Verification != DocumentVerificationState.Verified) throw new InvalidOperationException("The defensive source must be healthy and explicitly verified immediately before acceptance.");
        if (!CanAcceptGame(project, workbook, game, source, replace)) throw new InvalidOperationException("The defensive game section is not eligible for this acceptance operation." + (game.Issues.Count == 0 ? "" : " Issues: " + string.Join(", ", game.Issues.Select(x => x.Code))));
        if (game.HasAdvisories && string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("An operator note is required when accepting defensive game advisories.");
        var existing = CurrentGame(project, game.Season!.Value, game.Week!.Value);
        var accepted = new AcceptedDefensiveGame
        {
            ProjectId = project.Id, StagedWorkbookId = workbook.Id, StagedSectionId = game.Id, ExpectedDocumentId = workbook.ExpectedDocumentId, SourceFamilyId = workbook.SourceFamilyId, ImportRecordId = workbook.ImportRecordId,
            Season = game.Season.Value, Week = game.Week.Value, Opponent = game.Opponent, SiteIndicator = game.SiteIndicator, Players = Clone(game.Players), AcceptedIssues = Clone(game.Issues), AcceptanceNote = note, AcceptedUtc = DateTime.UtcNow
        };
        if (existing is not null) existing.IsCurrentAuthority = false;
        game.State = ReportReviewState.Accepted; game.ReviewedUtc = game.AcceptedUtc = accepted.AcceptedUtc; game.ReviewNote = note; project.AcceptedDefensiveGames.Add(accepted); return accepted;
    }

    public bool CanAcceptSeasonTotals(GameNotesProject project, StagedDefensiveWorkbook workbook, ExpectedSourceDocument? source, bool replace)
    {
        var totals = workbook.SeasonTotals;
        if (!ValidCommon(project, workbook, source) || totals is null || totals.State != ReportReviewState.PendingReview || totals.Players.Count == 0 || totals.HasBlockingIssues || totals.Season is null || !ValidSeason(project, workbook, totals.Season.Value) || !TotalsIdentityMatches(totals)) return false;
        var existing = project.AcceptedDefensiveSeasonTotals.FirstOrDefault(x => x.IsCurrentAuthority && x.Season == totals.Season);
        return replace ? existing is not null : existing is null;
    }

    public AcceptedDefensiveSeasonTotals AcceptSeasonTotals(GameNotesProject project, StagedDefensiveWorkbook workbook, ExpectedSourceDocument source, SourceFamilyConfiguration family, string note = "", bool replace = false)
    {
        ValidateSource(project, workbook, source, family); source.RefreshStatus(family);
        if (!source.HasHealthySource || source.Verification != DocumentVerificationState.Verified) throw new InvalidOperationException("The defensive source must be healthy and explicitly verified immediately before acceptance.");
        if (!CanAcceptSeasonTotals(project, workbook, source, replace)) throw new InvalidOperationException("The defensive season-total section is not eligible for this acceptance operation.");
        var totals = workbook.SeasonTotals!;
        if (totals.HasAdvisories && string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("An operator note is required when accepting season-total advisories.");
        var existing = project.AcceptedDefensiveSeasonTotals.FirstOrDefault(x => x.IsCurrentAuthority && x.Season == totals.Season);
        var accepted = new AcceptedDefensiveSeasonTotals
        {
            ProjectId = project.Id, StagedWorkbookId = workbook.Id, StagedSectionId = totals.Id, ExpectedDocumentId = workbook.ExpectedDocumentId, SourceFamilyId = workbook.SourceFamilyId, ImportRecordId = workbook.ImportRecordId,
            Season = totals.Season!.Value, Players = Clone(totals.Players), AcceptedIssues = Clone(totals.Issues), AcceptanceNote = note, AcceptedUtc = DateTime.UtcNow
        };
        if (existing is not null) existing.IsCurrentAuthority = false;
        totals.State = ReportReviewState.Accepted; totals.ReviewedUtc = totals.AcceptedUtc = accepted.AcceptedUtc; totals.ReviewNote = note; project.AcceptedDefensiveSeasonTotals.Add(accepted); return accepted;
    }

    public void RejectGame(StagedDefensiveGame game, string note)
    {
        if (game.State != ReportReviewState.PendingReview) throw new InvalidOperationException("Only a pending defensive game can be rejected.");
        game.State = ReportReviewState.Rejected; game.ReviewedUtc = DateTime.UtcNow; game.ReviewNote = note;
    }
    public void RejectSeasonTotals(StagedDefensiveSeasonTotals totals, string note)
    {
        if (totals.State != ReportReviewState.PendingReview) throw new InvalidOperationException("Only pending defensive season totals can be rejected.");
        totals.State = ReportReviewState.Rejected; totals.ReviewedUtc = DateTime.UtcNow; totals.ReviewNote = note;
    }

    private static bool ValidCommon(GameNotesProject project, StagedDefensiveWorkbook workbook, ExpectedSourceDocument? source)
    {
        if (source is null || !project.StagedDefensiveWorkbooks.Contains(workbook) || !project.ExpectedDocuments.Contains(source) || workbook.ProjectId != project.Id || workbook.ExpectedDocumentId != source.Id || source.SourceFamilyId != workbook.SourceFamilyId || !source.HasHealthySource || source.Verification != DocumentVerificationState.Verified) return false;
        var import = project.Imports.FirstOrDefault(x => x.Id == workbook.ImportRecordId); return import is not null && import.ProjectId == project.Id && import.ExpectedDocumentId == source.Id && import.SourceFamilyId == workbook.SourceFamilyId && import.Kind == "XLSX-DEFENSIVE";
    }
    private static void ValidateSource(GameNotesProject project, StagedDefensiveWorkbook workbook, ExpectedSourceDocument source, SourceFamilyConfiguration family)
    {
        if (!project.StagedDefensiveWorkbooks.Contains(workbook) || !project.ExpectedDocuments.Contains(source) || source.SourceFamilyId != family.Id || workbook.ProjectId != project.Id || workbook.ExpectedDocumentId != source.Id || workbook.SourceFamilyId != family.Id) throw new InvalidOperationException("Defensive staging provenance does not match the active project/source.");
        var import = project.Imports.FirstOrDefault(x => x.Id == workbook.ImportRecordId); if (import is null || import.ProjectId != project.Id || import.ExpectedDocumentId != source.Id || import.SourceFamilyId != family.Id || import.Kind != "XLSX-DEFENSIVE") throw new InvalidOperationException("Defensive import provenance is missing or inconsistent.");
    }
    private static AcceptedDefensiveGame? CurrentGame(GameNotesProject project, int season, int week) => project.AcceptedDefensiveGames.FirstOrDefault(x => x.IsCurrentAuthority && x.Season == season && x.Week == week);
    private static bool ValidSeason(GameNotesProject project, StagedDefensiveWorkbook workbook, int season)
    {
        var import = project.Imports.FirstOrDefault(x => x.Id == workbook.ImportRecordId); return project.Season == season && import?.ApplicableSeason == season;
    }
    private static bool TotalsIdentityMatches(StagedDefensiveSeasonTotals totals)
    {
        var match = Regex.Match(totals.IdentityText, @"^\s*(\d{4})\s*-\s*TOTALS\s*$", RegexOptions.IgnoreCase); return match.Success && int.TryParse(match.Groups[1].Value, out var season) && season == totals.Season;
    }
    private static List<T> Clone<T>(List<T> source) => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(source)) ?? [];
}

public sealed record DefensiveInformationSupply(IReadOnlyList<AcceptedDefensiveGame> Games, AcceptedDefensiveSeasonTotals? SeasonTotals, IReadOnlyDictionary<Guid, ImportRecord> Provenance);

public static class AcceptedDefensiveInformationSupply
{
    public static DefensiveInformationSupply Build(GameNotesProject project, int season)
    {
        var games = project.AcceptedDefensiveGames.Where(x => x.IsCurrentAuthority && x.Season == season).OrderBy(x => x.Week).ToList();
        var totals = project.AcceptedDefensiveSeasonTotals.FirstOrDefault(x => x.IsCurrentAuthority && x.Season == season);
        var ids = games.Select(x => x.ImportRecordId).Concat(totals is null ? [] : new[] { totals.ImportRecordId }).Distinct();
        return new(games, totals, project.Imports.Where(x => ids.Contains(x.Id)).ToDictionary(x => x.Id));
    }
}

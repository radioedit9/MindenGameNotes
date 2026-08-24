using System.Text.Json;
using System.Text.RegularExpressions;

namespace MindenGameNotes;

public sealed class GameInformationWorkflow
{
    public bool CanAccept(GameNotesProject project, StagedSingleGameReport report, ExpectedSourceDocument? source, bool replace)
    {
        RefreshEffectiveValidation(report);
        if (source is null || !project.ExpectedDocuments.Contains(source) || !project.StagedGameReports.Contains(report) || report.State != ReportReviewState.PendingReview || !source.HasHealthySource || source.Verification != DocumentVerificationState.Verified || report.ProjectId != project.Id || report.ExpectedDocumentId != source.Id || UnresolvedBlockingIssues(project, report)) return false;
        var provenance = project.Imports.FirstOrDefault(x => x.Id == report.ImportRecordId); if (provenance is null || provenance.ProjectId != project.Id || provenance.ExpectedDocumentId != source.Id || provenance.SourceFamilyId != report.SourceFamilyId) return false;
        var existing = project.CompletedGames.FirstOrDefault(x => x.IsCurrentAuthority && SameGame(x, report));
        if (replace && existing is not null && NormalizeOpponent(existing.Opponent) != NormalizeOpponent(Effective(report, "Opponent", report.Opponent))) return false;
        return replace ? existing is not null : existing is null;
    }

    public void Correct(StagedSingleGameReport report, string fieldKey, string correctedValue, string note)
    {
        if (report.State != ReportReviewState.PendingReview) throw new InvalidOperationException("Only a pending report can be corrected.");
        if (string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("A correction note is required.");
        var original = Field(report, fieldKey);
        report.Corrections.Add(new StagedCorrection { FieldKey = fieldKey, OriginalValue = original, CorrectedValue = correctedValue, Note = note });
        RefreshEffectiveValidation(report);
    }

    public CompletedGame Accept(GameNotesProject project, StagedSingleGameReport report, ExpectedSourceDocument source, SourceFamilyConfiguration family, string advisoryNote = "", bool replace = false)
    {
        if (report.State != ReportReviewState.PendingReview) throw new InvalidOperationException("Only a pending report can be accepted.");
        if (!project.StagedGameReports.Contains(report) || !project.ExpectedDocuments.Contains(source)) throw new InvalidOperationException("The staged report/source does not belong to the active project.");
        if (source.SourceFamilyId != family.Id) throw new InvalidOperationException("The expected source does not belong to the supplied source family.");
        source.RefreshStatus(family);
        if (!source.HasHealthySource || source.Verification != DocumentVerificationState.Verified) throw new InvalidOperationException("The expected source must be healthy and explicitly verified before acceptance.");
        if (report.ProjectId != project.Id || report.ExpectedDocumentId != source.Id) throw new InvalidOperationException("The staged report provenance does not match the active project/source.");
        var provenance = project.Imports.FirstOrDefault(x => x.Id == report.ImportRecordId);
        if (provenance is null || provenance.ProjectId != project.Id || provenance.ExpectedDocumentId != source.Id || provenance.SourceFamilyId != report.SourceFamilyId) throw new InvalidOperationException("The staged report import provenance is missing or inconsistent.");
        RefreshEffectiveValidation(report);
        if (UnresolvedBlockingIssues(project, report)) throw new InvalidOperationException("Resolve blocking report issues before acceptance.");
        if (report.HasAdvisories && string.IsNullOrWhiteSpace(advisoryNote)) throw new InvalidOperationException("An operator note is required when accepting advisory discrepancies.");
        var existing = project.CompletedGames.FirstOrDefault(x => x.IsCurrentAuthority && SameGame(x, report));
        if (existing is not null && !replace) throw new InvalidOperationException("Accepted information already exists for this game. Use Replace Accepted Report explicitly.");
        if (replace && existing is null) throw new InvalidOperationException("There is no accepted report for this game to replace.");
        if (existing is not null && NormalizeOpponent(existing.Opponent) != NormalizeOpponent(Effective(report, "Opponent", report.Opponent))) throw new InvalidOperationException("The replacement opponent conflicts with the accepted game on this date.");
        var game = BuildAccepted(project, report);
        if (existing is not null) existing.IsCurrentAuthority = false;
        report.State = ReportReviewState.Accepted; report.ReviewedUtc = report.AcceptedUtc = game.AcceptedUtc; report.ReviewNote = advisoryNote;
        project.CompletedGames.Add(game); project.CurrentAcceptedGameId = game.Id; return game;
    }

    public void Reject(StagedSingleGameReport report, string note)
    {
        if (report.State != ReportReviewState.PendingReview) throw new InvalidOperationException("Only a pending report can be rejected.");
        report.State = ReportReviewState.Rejected; report.ReviewedUtc = DateTime.UtcNow; report.ReviewNote = note;
    }

    private static CompletedGame BuildAccepted(GameNotesProject project, StagedSingleGameReport report)
    {
        var date = DateTime.Parse(Effective(report, "GameDate", report.GameDate?.ToString("yyyy-MM-dd") ?? ""), System.Globalization.CultureInfo.InvariantCulture);
        var game = new CompletedGame
        {
            ProjectId = project.Id, StagedReportId = report.Id, ExpectedDocumentId = report.ExpectedDocumentId, SourceFamilyId = report.SourceFamilyId, ImportRecordId = report.ImportRecordId,
            Season = report.ApplicableSeason, Week = report.ApplicableWeek, Opponent = Effective(report, "Opponent", report.Opponent), GameDate = date,
            Site = Effective(report, "Site", report.Site), MindenScore = int.Parse(Effective(report, "MindenScore", report.MindenScore?.ToString() ?? "")), OpponentScore = int.Parse(Effective(report, "OpponentScore", report.OpponentScore?.ToString() ?? "")),
            PeriodScores = Clone(report.PeriodScores), ScoringPlays = Clone(report.ScoringPlays), TeamStatistics = Clone(report.TeamStatistics), Rushing = Clone(report.Rushing), Passing = Clone(report.Passing), Receiving = Clone(report.Receiving),
            AcceptedIssues = Clone(report.Issues), Corrections = Clone(report.Corrections), AcceptedUtc = DateTime.UtcNow
        };
        ApplyDetailedCorrections(game); return game;
    }

    private static bool SameGame(CompletedGame game, StagedSingleGameReport report) => DateTime.TryParse(Effective(report, "GameDate", report.GameDate?.ToString("yyyy-MM-dd") ?? ""), out var date) && game.GameDate.Date == date.Date;
    public static string NormalizeOpponent(string value)
    {
        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]", "");
        foreach (var suffix in new[] { "highschool", "school", "high", "hs" }) if (normalized.EndsWith(suffix, StringComparison.Ordinal) && normalized.Length > suffix.Length) { normalized = normalized[..^suffix.Length]; break; }
        return normalized;
    }
    private static bool UnresolvedBlockingIssues(GameNotesProject project, StagedSingleGameReport report)
    {
        foreach (var issue in report.Issues.Where(x => x.Severity == InformationIssueSeverity.Blocking))
        {
            if (issue.Code is "MandatorySummaryMissing" or "GameDateMissing" && DateTime.TryParse(Effective(report, "GameDate", report.GameDate?.ToString("yyyy-MM-dd") ?? ""), out _) && int.TryParse(Effective(report, "MindenScore", report.MindenScore?.ToString() ?? ""), out _) && int.TryParse(Effective(report, "OpponentScore", report.OpponentScore?.ToString() ?? ""), out _)) continue;
            return true;
        }
        return false;
    }
    private static string Effective(StagedSingleGameReport report, string key, string original) => report.Corrections.LastOrDefault(x => x.FieldKey.Equals(key, StringComparison.OrdinalIgnoreCase))?.CorrectedValue ?? original;
    private static void RefreshEffectiveValidation(StagedSingleGameReport report)
    {
        report.Issues.RemoveAll(x => x.Code is "QuarterTotalDiscrepancy" or "TerminalScoreDiscrepancy" or "TotalOffenseDiscrepancy");
        var mindenScoreText = Effective(report, "MindenScore", report.MindenScore?.ToString() ?? ""); var opponentScoreText = Effective(report, "OpponentScore", report.OpponentScore?.ToString() ?? "");
        if (int.TryParse(mindenScoreText, out var mindenScore) && int.TryParse(opponentScoreText, out var opponentScore))
        {
            if (report.PeriodScores.Count > 0 && (report.PeriodScores.Sum(x => x.MindenPoints) != mindenScore || report.PeriodScores.Sum(x => x.OpponentPoints) != opponentScore)) AddAdvisory(report, "QuarterTotalDiscrepancy", "periods", "Effective final score differs from summed period scores; values were not rewritten.");
            var terminal = report.ScoringPlays.LastOrDefault(x => !string.IsNullOrWhiteSpace(x.ScoreAfterPlay))?.ScoreAfterPlay.Split('-');
            if (terminal is { Length: 2 } && int.TryParse(terminal[0], out var away) && int.TryParse(terminal[1], out var home))
            {
                var terminalMinden = report.AwayTeam.Contains("Minden", StringComparison.OrdinalIgnoreCase) ? away : home; var terminalOpponent = report.AwayTeam.Contains("Minden", StringComparison.OrdinalIgnoreCase) ? home : away;
                if (terminalMinden != mindenScore || terminalOpponent != opponentScore) AddAdvisory(report, "TerminalScoreDiscrepancy", "scoring", "The terminal scoring-play score differs from the effective final score; values were not rewritten.");
            }
        }
        var rush = EffectiveTeamStat(report, "Rushing").Split('-').LastOrDefault(); var pass = EffectiveTeamStat(report, "Passing"); var total = EffectiveTeamStat(report, "TotalOffense").Split('-').LastOrDefault();
        if (int.TryParse(rush, out var rushing) && int.TryParse(pass, out var passing) && int.TryParse(total, out var offense) && rushing + passing != offense) AddAdvisory(report, "TotalOffenseDiscrepancy", "team statistics", "Effective total offense differs from rushing plus passing; values were not rewritten.");
    }
    private static string EffectiveTeamStat(StagedSingleGameReport report, string key)
    {
        var stat = report.TeamStatistics.FirstOrDefault(x => x.Key == key); if (stat is null) return ""; return Effective(report, $"TeamStatistic:{key}:Minden", stat.Minden.Reported);
    }
    private static void AddAdvisory(StagedSingleGameReport report, string code, string section, string message) => report.Issues.Add(new() { Severity = InformationIssueSeverity.Advisory, Code = code, Section = section, Message = message });
    private static string Field(StagedSingleGameReport report, string key)
    {
        var simple = key.ToLowerInvariant() switch { "opponent" => report.Opponent, "gamedate" => report.GameDate?.ToString("yyyy-MM-dd") ?? "", "site" => report.Site, "mindenscore" => report.MindenScore?.ToString() ?? "", "opponentscore" => report.OpponentScore?.ToString() ?? "", _ => null };
        if (simple is not null) return simple;
        var parts = key.Split(':'); if (parts.Length != 3) throw new InvalidOperationException("Use a summary field or Section:row:field correction key.");
        if (parts[0].Equals("TeamStatistic", StringComparison.OrdinalIgnoreCase)) return parts[2].Equals("Minden", StringComparison.OrdinalIgnoreCase) ? report.TeamStatistics.Single(x => x.Key.Equals(parts[1], StringComparison.OrdinalIgnoreCase)).Minden.Reported : report.TeamStatistics.Single(x => x.Key.Equals(parts[1], StringComparison.OrdinalIgnoreCase)).Opponent.Reported;
        OffensivePerformance row = parts[0].ToLowerInvariant() switch { "rushing" => report.Rushing.Single(x => x.Player.Equals(parts[1], StringComparison.OrdinalIgnoreCase)), "passing" => report.Passing.Single(x => x.Player.Equals(parts[1], StringComparison.OrdinalIgnoreCase)), "receiving" => report.Receiving.Single(x => x.Player.Equals(parts[1], StringComparison.OrdinalIgnoreCase)), _ => throw new InvalidOperationException("Unknown correction section.") };
        return (row, parts[2].ToLowerInvariant()) switch { (RushingPerformance r, "attempts") => r.Attempts.ToString(), (RushingPerformance r, "yards") => r.Yards.ToString(), (PassingPerformance p, "completions") => p.Completions.ToString(), (PassingPerformance p, "attempts") => p.Attempts.ToString(), (PassingPerformance p, "interceptions") => p.Interceptions.ToString(), (PassingPerformance p, "yards") => p.Yards.ToString(), (ReceivingPerformance r, "receptions") => r.Receptions.ToString(), (ReceivingPerformance r, "yards") => r.Yards.ToString(), _ => throw new InvalidOperationException("Unknown correction field.") };
    }

    private static void ApplyDetailedCorrections(CompletedGame game)
    {
        foreach (var correction in game.Corrections.Where(x => x.FieldKey.Contains(':')))
        {
            var parts = correction.FieldKey.Split(':'); if (parts.Length != 3) continue;
            if (parts[0].Equals("TeamStatistic", StringComparison.OrdinalIgnoreCase))
            {
                var stat = game.TeamStatistics.Single(x => x.Key.Equals(parts[1], StringComparison.OrdinalIgnoreCase)); var value = parts[2].Equals("Minden", StringComparison.OrdinalIgnoreCase) ? stat.Minden : stat.Opponent; value.AcceptedValue = correction.CorrectedValue; continue;
            }
            OffensivePerformance row = parts[0].ToLowerInvariant() switch { "rushing" => game.Rushing.Single(x => x.Player.Equals(parts[1], StringComparison.OrdinalIgnoreCase)), "passing" => game.Passing.Single(x => x.Player.Equals(parts[1], StringComparison.OrdinalIgnoreCase)), "receiving" => game.Receiving.Single(x => x.Player.Equals(parts[1], StringComparison.OrdinalIgnoreCase)), _ => throw new InvalidOperationException("Unknown correction section.") };
            var number = int.Parse(correction.CorrectedValue);
            switch (row, parts[2].ToLowerInvariant()) { case (RushingPerformance r, "attempts"): r.Attempts = number; break; case (RushingPerformance r, "yards"): r.Yards = number; break; case (PassingPerformance p, "completions"): p.Completions = number; break; case (PassingPerformance p, "attempts"): p.Attempts = number; break; case (PassingPerformance p, "interceptions"): p.Interceptions = number; break; case (PassingPerformance p, "yards"): p.Yards = number; break; case (ReceivingPerformance r, "receptions"): r.Receptions = number; break; case (ReceivingPerformance r, "yards"): r.Yards = number; break; default: throw new InvalidOperationException("Unknown correction field."); }
        }
    }
    private static List<T> Clone<T>(List<T> source) => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(source)) ?? [];
}

public sealed record PageOneFactualSupply(bool IsAvailable, bool IsNotApplicable, CompletedGame? LookingBackGame, ImportRecord? Provenance, IReadOnlyList<InformationValidationIssue> Issues, string Status);

public static class PageOneInformationSupply
{
    public static PageOneFactualSupply Build(GameNotesProject project)
    {
        var game = project.CompletedGames.FirstOrDefault(x => x.Id == project.CurrentAcceptedGameId && x.IsCurrentAuthority);
        if (game is not null) return new(true, false, game, project.Imports.FirstOrDefault(x => x.Id == game.ImportRecordId), game.AcceptedIssues, "Accepted Looking Back facts available");
        var expected = project.ExpectedDocuments.FirstOrDefault(x => x.IsSingleGameReport);
        var notApplicable = expected is not null && !expected.IsApplicable;
        return new(false, notApplicable, null, null, [], notApplicable ? "Looking Back not applicable" : "No accepted Looking Back report");
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace MindenGameNotes;

public sealed partial class SingleGameStatsParser
{
    public StagedSingleGameReport Parse(string text, GameNotesProject project, ExpectedSourceDocument document, SourceFamilyConfiguration family, ImportRecord import)
    {
        var report = new StagedSingleGameReport
        {
            ProjectId = project.Id, ExpectedDocumentId = document.Id, SourceFamilyId = family.Id, ImportRecordId = import.Id,
            ApplicableSeason = project.Season, ApplicableWeek = project.Week
        };
        var normalized = text.Replace("\r", "");
        var lines = normalized.Split('\n');
        ParseIdentity(lines, report);
        ParsePeriodScores(lines, report);
        ParseScoringPlays(lines, report);
        ParseScoringPlayScores(lines, report);
        ParseTeamStatistics(lines, report);
        ParsePerformances(normalized, report);
        Validate(report, project);
        return report;
    }

    private static void ParseIdentity(string[] lines, StagedSingleGameReport report)
    {
        var header = lines.Select(x => Header().Match(x.Trim())).FirstOrDefault(x => x.Success);
        if (header is null || !header.Success)
        {
            Block(report, "identity", "GameIdentityMissing", "The Automated ScoreBook game identity could not be established."); return;
        }
        report.AwayTeam = CleanRank(header.Groups[1].Value); report.HomeTeam = CleanRank(header.Groups[2].Value);
        report.Opponent = IsMinden(report.AwayTeam) ? report.HomeTeam : report.AwayTeam;
        if (!IsMinden(report.AwayTeam) && !IsMinden(report.HomeTeam)) Block(report, "identity", "MindenMissing", "The report does not identify Minden as either team.");
        if (DateTime.TryParseExact(header.Groups[3].Value.Trim(), "MMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) report.GameDate = date;
        else Block(report, "identity", "GameDateMissing", "The report game date could not be parsed.");
        report.Site = header.Groups[4].Value.Trim();
    }

    private static void ParsePeriodScores(string[] lines, StagedSingleGameReport report)
    {
        var start = Array.FindIndex(lines, x => x.Contains("Score by Quarters", StringComparison.OrdinalIgnoreCase));
        if (start < 0) { Advise(report, "periods", "PeriodScoresMissing", "Score-by-period information was not found."); return; }
        var rows = new List<(string Team, int[] Scores)>();
        for (var i = start + 1; i < Math.Min(lines.Length, start + 6); i++)
        {
            var match = ScoreRow().Match(lines[i]);
            if (match.Success) rows.Add((match.Groups[1].Value.Trim(), Enumerable.Range(2, 5).Select(x => int.Parse(match.Groups[x].Value, CultureInfo.InvariantCulture)).ToArray()));
        }
        if (rows.Count != 2) { Advise(report, "periods", "PeriodScoresAmbiguous", "Two score-by-quarter rows were not found."); return; }
        var minden = rows.SingleOrDefault(x => IsMinden(x.Team)); var opponent = rows.SingleOrDefault(x => !IsMinden(x.Team));
        if (minden.Scores is null || opponent.Scores is null) { Block(report, "identity", "ScoreTeamsAmbiguous", "The score rows could not be associated with Minden and its opponent."); return; }
        report.MindenScore = minden.Scores[^1]; report.OpponentScore = opponent.Scores[^1];
        var periods = Math.Min(minden.Scores.Length, opponent.Scores.Length) - 1;
        for (var i = 0; i < periods; i++) report.PeriodScores.Add(new PeriodScore { Order = i + 1, Label = (i + 1).ToString(CultureInfo.InvariantCulture), MindenPoints = minden.Scores[i], OpponentPoints = opponent.Scores[i] });
    }

    private static void ParseScoringPlays(string[] lines, StagedSingleGameReport report)
    {
        var start = Array.FindIndex(lines, x => x.TrimStart().StartsWith("Qtr Time", StringComparison.OrdinalIgnoreCase));
        if (start < 0) { Advise(report, "scoring", "ScoringPlaysMissing", "The scoring-play summary was not found."); return; }
        var period = 0;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].Contains("FIRST DOWNS", StringComparison.OrdinalIgnoreCase)) break;
            var match = Scoring().Match(lines[i]); if (!match.Success) continue;
            if (match.Groups[1].Success) period = PeriodNumber(match.Groups[1].Value);
            var description = match.Groups[3].Value.Trim();
            report.ScoringPlays.Add(new ScoringPlay { Period = period, Clock = match.Groups[2].Value, Team = description.Split(" - ")[0].Trim(), Description = description });
        }
        if (report.ScoringPlays.Count == 0) Advise(report, "scoring", "ScoringPlaysMissing", "No scoring plays were parsed.");
    }

    private static void ParseScoringPlayScores(string[] lines, StagedSingleGameReport report)
    {
        var header = Array.FindIndex(lines, x => x.Contains("Scoring Play", StringComparison.OrdinalIgnoreCase) && x.Contains("V-H", StringComparison.OrdinalIgnoreCase));
        if (header < 0) return;
        var index = 0;
        for (var i = header + 1; i < lines.Length && index < report.ScoringPlays.Count; i++)
        {
            if (lines[i].Contains("Team Statistics (Final)", StringComparison.OrdinalIgnoreCase)) break;
            var score = TrailingScore().Match(lines[i]); if (!score.Success) continue;
            report.ScoringPlays[index++].ScoreAfterPlay = Regex.Replace(score.Groups[1].Value, @"\s+", "");
        }
    }

    private static void ParseTeamStatistics(string[] lines, StagedSingleGameReport report)
    {
        var firstDownIndex = Array.FindIndex(lines, x => x.TrimStart().StartsWith("FIRST DOWNS", StringComparison.OrdinalIgnoreCase));
        var headerIndex = firstDownIndex - 1; while (headerIndex >= 0 && string.IsNullOrWhiteSpace(lines[headerIndex])) headerIndex--;
        if (headerIndex < 0 || Regex.Split(lines[headerIndex].Trim(), @"\s{2,}").Length != 2) { Advise(report, "team statistics", "TeamStatisticsMissing", "No supported team statistics were parsed."); return; }
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i]; if (line.TrimStart().StartsWith("RUSHING:", StringComparison.OrdinalIgnoreCase)) break;
            var columns = Regex.Split(line.Trim(), @"\s{2,}"); if (columns.Length != 3) continue;
            var label = columns[0]; var away = columns[1]; var home = columns[2];
            var key = label.Equals("RUSHES-YARDS (NET)", StringComparison.OrdinalIgnoreCase) ? "Rushing" : label.Equals("PASSING YDS (NET)", StringComparison.OrdinalIgnoreCase) ? "Passing" : label.Equals("TOTAL OFFENSE PLAYS-YARDS", StringComparison.OrdinalIgnoreCase) ? "TotalOffense" : Regex.Replace(label, "[^A-Za-z0-9]", "");
            report.TeamStatistics.Add(new TeamGameStatistic { Key = key, Label = label, Minden = Value(IsMinden(report.AwayTeam) ? away : home), Opponent = Value(IsMinden(report.AwayTeam) ? home : away) });
        }
        if (report.TeamStatistics.Count == 0) Advise(report, "team statistics", "TeamStatisticsMissing", "No supported team statistics were parsed.");
    }

    private static void ParsePerformances(string text, StagedSingleGameReport report)
    {
        var rushing = Section(text, "RUSHING:", "PASSING:");
        var passing = Section(text, "PASSING:", "RECEIVING:");
        var receiving = Section(text, "RECEIVING:", "INTERCEPTIONS:");
        var mindenRushing = TeamPart(rushing, "Minden High-");
        foreach (var item in mindenRushing.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Rush().Match(item); if (m.Success) report.Rushing.Add(new RushingPerformance { Player = m.Groups[1].Value.Trim(), Attempts = Int(m, 2), Yards = Int(m, 3), Reported = item.Trim().TrimEnd('.') });
        }
        var mindenPassing = TeamPart(passing, "Minden High-");
        foreach (var item in mindenPassing.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Pass().Match(item); if (m.Success) report.Passing.Add(new PassingPerformance { Player = m.Groups[1].Value.Trim(), Completions = Int(m, 2), Attempts = Int(m, 3), Interceptions = Int(m, 4), Yards = Int(m, 5), Reported = item.Trim().TrimEnd('.') });
        }
        var mindenReceiving = TeamPart(receiving, "Minden High-");
        foreach (var item in mindenReceiving.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Receive().Match(item); if (m.Success) report.Receiving.Add(new ReceivingPerformance { Player = m.Groups[1].Value.Trim(), Receptions = Int(m, 2), Yards = m.Groups[3].Value.Equals("minus 1", StringComparison.OrdinalIgnoreCase) ? -1 : Int(m, 3), Reported = item.Trim().TrimEnd('.') });
        }
        if (report.Rushing.Count == 0) Advise(report, "rushing", "RushingMissing", "No Minden rushing rows were parsed.");
        if (report.Passing.Count == 0) Advise(report, "passing", "PassingMissing", "No Minden passing rows were parsed.");
        if (report.Receiving.Count == 0) Advise(report, "receiving", "ReceivingMissing", "No Minden receiving rows were parsed.");
        if (text.Contains("TACKLES", StringComparison.OrdinalIgnoreCase)) report.Issues.Add(new InformationValidationIssue { Severity = InformationIssueSeverity.Informational, Code = "DefensiveIgnored", Section = "unsupported", Message = "Defensive individual material was ignored; its authoritative source is deferred Excel intake." });
    }

    private static void Validate(StagedSingleGameReport report, GameNotesProject project)
    {
        if (report.GameDate is null || report.MindenScore is null || report.OpponentScore is null) Block(report, "summary", "MandatorySummaryMissing", "Game identity and final score are mandatory.");
        if (report.PeriodScores.Count > 0 && report.MindenScore is not null && (report.PeriodScores.Sum(x => x.MindenPoints) != report.MindenScore || report.PeriodScores.Sum(x => x.OpponentPoints) != report.OpponentScore))
            Advise(report, "periods", "QuarterTotalDiscrepancy", "Final score differs from summed period scores; reported values were preserved.");
        var terminal = report.ScoringPlays.LastOrDefault(x => !string.IsNullOrWhiteSpace(x.ScoreAfterPlay))?.ScoreAfterPlay.Split('-');
        if (terminal is { Length: 2 } && int.TryParse(terminal[0], out var away) && int.TryParse(terminal[1], out var home) && report.MindenScore is not null)
        {
            var reportedMinden = IsMinden(report.AwayTeam) ? away : home; var reportedOpponent = IsMinden(report.AwayTeam) ? home : away;
            if (reportedMinden != report.MindenScore || reportedOpponent != report.OpponentScore) Advise(report, "scoring", "TerminalScoreDiscrepancy", "The terminal scoring-play score differs from the reported final score; source values were preserved.");
        }
        var rush = report.TeamStatistics.FirstOrDefault(x => x.Key == "Rushing")?.Minden.Reported.Split('-').LastOrDefault();
        var pass = report.TeamStatistics.FirstOrDefault(x => x.Key == "Passing")?.Minden.Reported;
        var total = report.TeamStatistics.FirstOrDefault(x => x.Key == "TotalOffense")?.Minden.Reported.Split('-').LastOrDefault();
        if (int.TryParse(rush, out var r) && int.TryParse(pass, out var p) && int.TryParse(total, out var t) && r + p != t)
            Advise(report, "team statistics", "TotalOffenseDiscrepancy", "Reported total offense differs from reported rushing plus passing; the report value was preserved.");
    }

    private static ReportedStatValue Value(string value) => new() { Reported = value, Numeric = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : null };
    private static string Section(string text, string start, string end) { var a = text.IndexOf(start, StringComparison.OrdinalIgnoreCase); if (a < 0) return ""; var b = text.IndexOf(end, a + start.Length, StringComparison.OrdinalIgnoreCase); return b < 0 ? text[a..] : text[a..b]; }
    private static string TeamPart(string section, string marker) { var i = section.IndexOf(marker, StringComparison.OrdinalIgnoreCase); return i < 0 ? "" : Regex.Replace(section[(i + marker.Length)..], @"\s+", " ").Trim(); }
    private static string CleanRank(string value) => Regex.Replace(value.Trim(), @"^#\d+\s+", "");
    private static bool IsMinden(string value) => value.Contains("Minden", StringComparison.OrdinalIgnoreCase);
    private static int PeriodNumber(string label) => label.StartsWith("1") ? 1 : label.StartsWith("2") ? 2 : label.StartsWith("3") ? 3 : label.StartsWith("4") ? 4 : 5;
    private static int Int(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
    private static void Block(StagedSingleGameReport report, string section, string code, string message) => report.Issues.Add(new() { Severity = InformationIssueSeverity.Blocking, Section = section, Code = code, Message = message });
    private static void Advise(StagedSingleGameReport report, string section, string code, string message) => report.Issues.Add(new() { Severity = InformationIssueSeverity.Advisory, Section = section, Code = code, Message = message });

    [GeneratedRegex(@"^(#\d+\s+.+?)\s+vs\s+(#\d+\s+.+?)\s+\(([^)]+?)\s+at\s+([^)]+)\)$", RegexOptions.IgnoreCase)] private static partial Regex Header();
    [GeneratedRegex(@"^\s*(.+?)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$")] private static partial Regex ScoreRow();
    [GeneratedRegex(@"^\s*(?:(1st|2nd|3rd|4th|OT)\s+)?(\d{2}:\d{2})\s+(.+)$", RegexOptions.IgnoreCase)] private static partial Regex Scoring();
    [GeneratedRegex(@"(\d+\s*-\s*\d+)\s*$")] private static partial Regex TrailingScore();
    [GeneratedRegex(@"^(.+?)\s+(\d+)-(-?\d+)\.?$")] private static partial Regex Rush();
    [GeneratedRegex(@"^(.+?)\s+(\d+)-(\d+)-(\d+)-(-?\d+)\.?$")] private static partial Regex Pass();
    [GeneratedRegex(@"^(.+?)\s+(\d+)-(minus 1|-?\d+)\.?$", RegexOptions.IgnoreCase)] private static partial Regex Receive();
}

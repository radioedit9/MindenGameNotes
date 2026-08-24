using System.Text.Json;

namespace MindenGameNotes;

public sealed class SupplementalInformationWorkflow
{
    private static readonly JsonSerializerOptions CloneOptions = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public StagedSupplementalSection Stage(GameNotesProject project, SupplementalSectionKind kind, SupplementalPayload payload, IEnumerable<SupplementalEvidence> evidence, int? baselineThroughSeason = null)
    {
        if (project.Season is null || project.Week is null) throw new InvalidOperationException("Complete the weekly project season and week before staging supplemental information.");
        var staged = new StagedSupplementalSection { ProjectId = project.Id, Kind = kind, Season = project.Season.Value, Week = IsSeasonAuthority(kind) ? null : project.Week, BaselineThroughSeason = baselineThroughSeason, Payload = Clone(payload), Evidence = Clone(evidence.ToList()) };
        staged.Issues = SupplementalValidation.Validate(staged, project);
        project.StagedSupplementalSections.Add(staged); return staged;
    }

    public StagedSupplementalSection StageSourceBacked(GameNotesProject project, SupplementalSectionKind kind, SupplementalPayload payload, ExpectedSourceDocument document, SourceFamilyConfiguration family, int? baselineThroughSeason = null)
    {
        if (!SupplementalValidation.RequiresSource(kind) || !project.ExpectedDocuments.Contains(document) || document.SourceFamilyId != family.Id) throw new InvalidOperationException("This typed supplemental section requires a configured source document and family.");
        document.RefreshStatus(family); var locator = document.ResolvePath(family) ?? "";
        var import = new ImportRecord { ProjectId = project.Id, SourceFamilyId = family.Id, ExpectedDocumentId = document.Id, FileName = Path.GetFileName(locator), SourceLocator = locator, SourceModifiedUtc = document.SourceModifiedUtc, ApplicableSeason = project.Season, ApplicableWeek = IsSeasonAuthority(kind) ? null : project.Week, ImportedUtc = DateTime.UtcNow, Kind = "SUPPLEMENTAL", RowCount = SupplementalValidation.ItemCount(payload) };
        project.Imports.Add(import);
        var evidence = new SupplementalEvidence { Kind = SupplementalEvidenceKind.ExpectedSourceDocument, ExpectedDocumentId = document.Id, SourceFamilyId = family.Id, ImportRecordId = import.Id, SourceLocator = locator, SourceAsOfUtc = import.SourceModifiedUtc, ApplicableSeason = import.ApplicableSeason, ApplicableWeek = import.ApplicableWeek };
        SupplementalValidation.AssignEvidence(payload, evidence.Id);
        return Stage(project, kind, payload, [evidence], baselineThroughSeason);
    }

    public StagedSupplementalSection StageEditorial(GameNotesProject project, NerdNotesPayload payload, string authorityName, string evidenceNote)
    {
        var evidence = new SupplementalEvidence { Kind = SupplementalEvidenceKind.EditorialDecision, AuthorityName = authorityName, Note = evidenceNote, ApplicableSeason = project.Season, ApplicableWeek = project.Week };
        SupplementalValidation.AssignEvidence(payload, evidence.Id);
        return Stage(project, SupplementalSectionKind.NerdNotes, payload, [evidence]);
    }

    public static SupplementalPayload ParsePayload(SupplementalSectionKind kind, string json)
    {
        SupplementalPayload? payload = kind switch
        {
            SupplementalSectionKind.Page1WeeklyFacts => JsonSerializer.Deserialize<Page1WeeklyFactsPayload>(json, CloneOptions), SupplementalSectionKind.MindenSchedule or SupplementalSectionKind.OpponentSchedule or SupplementalSectionKind.DistrictSchedule => JsonSerializer.Deserialize<SchedulePayload>(json, CloneOptions),
            SupplementalSectionKind.Class4APoll or SupplementalSectionKind.DivisionIINonSelectRatings => JsonSerializer.Deserialize<RankingSnapshotPayload>(json, CloneOptions), SupplementalSectionKind.IndividualOffenseSpecialTeams => JsonSerializer.Deserialize<IndividualStatsPayload>(json, CloneOptions), SupplementalSectionKind.PlayerOfGame => JsonSerializer.Deserialize<PlayerOfGamePayload>(json, CloneOptions),
            SupplementalSectionKind.CoachingHistoryBaseline => JsonSerializer.Deserialize<CoachingHistoryBaselinePayload>(json, CloneOptions), SupplementalSectionKind.ProgramHistoryBaseline => JsonSerializer.Deserialize<ProgramHistoryBaselinePayload>(json, CloneOptions), SupplementalSectionKind.TeamStatisticsReport => JsonSerializer.Deserialize<TeamStatisticsReportPayload>(json, CloneOptions), SupplementalSectionKind.NerdNotes => JsonSerializer.Deserialize<NerdNotesPayload>(json, CloneOptions),
            SupplementalSectionKind.MindenRoster or SupplementalSectionKind.OpponentRoster => JsonSerializer.Deserialize<RosterPayload>(json, CloneOptions), _ => throw new InvalidOperationException("Unsupported supplemental section kind.")
        };
        return payload ?? throw new InvalidDataException("The typed supplemental payload could not be read.");
    }

    public static SupplementalPayload EmptyPayload(SupplementalSectionKind kind) => kind switch
    {
        SupplementalSectionKind.Page1WeeklyFacts => new Page1WeeklyFactsPayload(), SupplementalSectionKind.MindenSchedule or SupplementalSectionKind.OpponentSchedule or SupplementalSectionKind.DistrictSchedule => new SchedulePayload(),
        SupplementalSectionKind.Class4APoll or SupplementalSectionKind.DivisionIINonSelectRatings => new RankingSnapshotPayload(), SupplementalSectionKind.IndividualOffenseSpecialTeams => new IndividualStatsPayload(), SupplementalSectionKind.PlayerOfGame => new PlayerOfGamePayload(),
        SupplementalSectionKind.CoachingHistoryBaseline => new CoachingHistoryBaselinePayload(), SupplementalSectionKind.ProgramHistoryBaseline => new ProgramHistoryBaselinePayload(), SupplementalSectionKind.TeamStatisticsReport => new TeamStatisticsReportPayload(),
        SupplementalSectionKind.NerdNotes => new NerdNotesPayload(), SupplementalSectionKind.MindenRoster or SupplementalSectionKind.OpponentRoster => new RosterPayload(), _ => throw new InvalidOperationException("Unsupported supplemental section kind.")
    };

    public bool CanAccept(GameNotesProject project, StagedSupplementalSection staged, IReadOnlyCollection<SourceFamilyConfiguration> families, bool replace)
    {
        if (!project.StagedSupplementalSections.Contains(staged) || staged.ProjectId != project.Id || staged.State != ReportReviewState.PendingReview || staged.Payload is null || staged.Season != project.Season || (!IsSeasonAuthority(staged.Kind) && staged.Week != project.Week)) return false;
        if (staged.HasBlockingIssues || SupplementalValidation.Validate(staged, project).Any(x => x.Severity == InformationIssueSeverity.Blocking) || !EvidenceHealthy(project, staged, families, refresh: false)) return false;
        var existing = Current(project, staged);
        return replace ? existing is not null : existing is null;
    }

    public AcceptedSupplementalSection Accept(GameNotesProject project, StagedSupplementalSection staged, IReadOnlyCollection<SourceFamilyConfiguration> families, string note = "", bool replace = false)
    {
        RefreshEvidence(project, staged, families);
        if (!CanAccept(project, staged, families, replace)) throw new InvalidOperationException("The supplemental section is not eligible for this acceptance operation.");
        if ((staged.HasAdvisories || staged.Evidence.Any(x => x.Kind == SupplementalEvidenceKind.EditorialDecision)) && string.IsNullOrWhiteSpace(note)) throw new InvalidOperationException("An operator note is required for advisory or editorial acceptance.");
        var existing = Current(project, staged);
        var accepted = new AcceptedSupplementalSection
        {
            ProjectId = project.Id, StagedSectionId = staged.Id, Kind = staged.Kind, Season = staged.Season, Week = staged.Week, BaselineThroughSeason = staged.BaselineThroughSeason,
            Payload = Clone(staged.Payload), Evidence = Clone(staged.Evidence), AcceptedIssues = Clone(staged.Issues), AcceptanceNote = note, AcceptedUtc = DateTime.UtcNow
        };
        if (existing is not null) existing.IsCurrentAuthority = false;
        staged.State = ReportReviewState.Accepted; staged.ReviewedUtc = staged.AcceptedUtc = accepted.AcceptedUtc; staged.ReviewNote = note; project.AcceptedSupplementalSections.Add(accepted); return accepted;
    }

    public void Reject(StagedSupplementalSection staged, string note)
    {
        if (staged.State != ReportReviewState.PendingReview) throw new InvalidOperationException("Only pending supplemental information can be rejected.");
        staged.State = ReportReviewState.Rejected; staged.ReviewedUtc = DateTime.UtcNow; staged.ReviewNote = note;
    }

    private static AcceptedSupplementalSection? Current(GameNotesProject project, StagedSupplementalSection staged) => project.AcceptedSupplementalSections.FirstOrDefault(x => x.IsCurrentAuthority && x.Kind == staged.Kind && x.Season == staged.Season && x.Week == staged.Week && x.BaselineThroughSeason == staged.BaselineThroughSeason);
    internal static bool IsSeasonAuthority(SupplementalSectionKind kind) => kind is SupplementalSectionKind.PlayerOfGame or SupplementalSectionKind.MindenRoster;

    private static void RefreshEvidence(GameNotesProject project, StagedSupplementalSection staged, IReadOnlyCollection<SourceFamilyConfiguration> families)
    {
        foreach (var evidence in staged.Evidence.Where(x => x.Kind == SupplementalEvidenceKind.ExpectedSourceDocument))
        {
            var document = project.ExpectedDocuments.FirstOrDefault(x => x.Id == evidence.ExpectedDocumentId); var family = families.FirstOrDefault(x => x.Id == evidence.SourceFamilyId);
            document?.RefreshStatus(family);
        }
    }
    private static bool EvidenceHealthy(GameNotesProject project, StagedSupplementalSection staged, IReadOnlyCollection<SourceFamilyConfiguration> families, bool refresh)
    {
        if (staged.Evidence.Count == 0) return false;
        foreach (var evidence in staged.Evidence)
        {
            if (evidence.Id == Guid.Empty || evidence.ApplicableSeason != staged.Season || evidence.ApplicableWeek != staged.Week && !IsSeasonAuthority(staged.Kind)) return false;
            if (evidence.Kind == SupplementalEvidenceKind.EditorialDecision) { if (!SupplementalValidation.AllowsEditorial(staged.Kind) || string.IsNullOrWhiteSpace(evidence.AuthorityName) || string.IsNullOrWhiteSpace(evidence.Note)) return false; continue; }
            var document = project.ExpectedDocuments.FirstOrDefault(x => x.Id == evidence.ExpectedDocumentId); var family = families.FirstOrDefault(x => x.Id == evidence.SourceFamilyId);
            if (document is null || family is null || document.SourceFamilyId != family.Id) return false;
            if (refresh) document.RefreshStatus(family);
            var import = project.Imports.FirstOrDefault(x => x.Id == evidence.ImportRecordId);
            if (!document.HasHealthySource || document.Verification != DocumentVerificationState.Verified || import is null || !SupplementalValidation.ImportMatches(import, evidence, staged, document, family, requireCurrentSource: true)) return false;
        }
        if (SupplementalValidation.RequiresSource(staged.Kind) && !staged.Evidence.Any(x => x.Kind == SupplementalEvidenceKind.ExpectedSourceDocument)) return false;
        if (staged.Kind == SupplementalSectionKind.NerdNotes && !staged.Evidence.Any(x => x.Kind == SupplementalEvidenceKind.EditorialDecision)) return false;
        return true;
    }
    private static T Clone<T>(T source) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source, CloneOptions), CloneOptions)!;
}

internal static class SupplementalValidation
{
    public static List<InformationValidationIssue> Validate(StagedSupplementalSection section, GameNotesProject project)
    {
        var issues = new List<InformationValidationIssue>();
        void Block(string code, string message) => issues.Add(new() { Severity = InformationIssueSeverity.Blocking, Code = code, Section = section.Kind.ToString(), Message = message });
        if (section.ProjectId != project.Id || section.Season != project.Season) Block("SupplementalProjectIdentity", "Supplemental information does not match the active project season.");
        if (!SupplementalInformationWorkflow.IsSeasonAuthority(section.Kind) && section.Week != project.Week) Block("SupplementalWeekIdentity", "Weekly supplemental information does not match the active project week.");
        if (!Matches(section.Kind, section.Payload)) Block("SupplementalPayloadType", "The payload type does not match its closed supplemental section kind.");
        if (IsEmpty(section.Payload)) Block("SupplementalPayloadEmpty", "The supplemental section contains no authoritative information.");
        var evidenceIds = section.Evidence.Select(x => x.Id).ToHashSet(); if (section.Evidence.Count == 0 || section.Evidence.Any(x => x.Id == Guid.Empty) || evidenceIds.Count != section.Evidence.Count) Block("SupplementalEvidenceMissing", "Supplemental evidence is missing or duplicated.");
        if (section.Evidence.Any(x => x.Kind == SupplementalEvidenceKind.EditorialDecision) && !AllowsEditorial(section.Kind)) Block("EditorialEvidenceNotAllowed", "Editorial evidence cannot establish this source-backed factual authority.");
        if (RequiresSource(section.Kind) && !section.Evidence.Any(x => x.Kind == SupplementalEvidenceKind.ExpectedSourceDocument)) Block("SourceEvidenceRequired", "This factual section requires expected-document/import evidence.");
        if (section.Kind == SupplementalSectionKind.NerdNotes && !section.Evidence.Any(x => x.Kind == SupplementalEvidenceKind.EditorialDecision)) Block("EditorialEvidenceRequired", "Page 7 requires an editorial disposition authority.");
        foreach (var id in ReferencedEvidence(section.Payload).Where(x => !evidenceIds.Contains(x))) Block("SupplementalEvidenceOrphan", $"Payload evidence {id} is not present in the section evidence.");
        if (section.Payload is Page1WeeklyFactsPayload page1)
        {
            foreach (var id in Page1SourceEvidenceIds(page1).Where(id => section.Evidence.FirstOrDefault(x => x.Id == id)?.Kind != SupplementalEvidenceKind.ExpectedSourceDocument))
                Block("Page1FactualEvidenceRequired", $"Page 1 factual field evidence {id} is not valid source-backed evidence.");
        }
        if (section.Payload is IndividualStatsPayload individual && individual.StatisticalSeason != (project.Week == 1 ? project.Season - 1 : project.Season)) Block("StatisticalSeasonMismatch", "Individual production does not match the required statistical season.");
        if (section.Payload is TeamStatisticsReportPayload team && team.StatisticalSeason != (project.Week == 1 ? project.Season - 1 : project.Season)) Block("StatisticalSeasonMismatch", "Team statistics do not match the required statistical season.");
        if (section.Payload is CoachingHistoryBaselinePayload coaching && coaching.BaselineThroughSeason != section.BaselineThroughSeason) Block("BaselineSeasonMismatch", "Coaching baseline season is inconsistent.");
        if (section.Payload is ProgramHistoryBaselinePayload program && program.BaselineThroughSeason != section.BaselineThroughSeason) Block("BaselineSeasonMismatch", "Program baseline season is inconsistent.");
        if (section.Kind is SupplementalSectionKind.CoachingHistoryBaseline or SupplementalSectionKind.ProgramHistoryBaseline && section.BaselineThroughSeason != project.Season - 1) Block("BaselineSeasonMismatch", "The historical baseline must be established through the season preceding the active season.");
        if (section.Payload is RosterPayload roster && roster.Season != project.Season) Block("RosterSeasonMismatch", "The roster does not match the active season.");
        return issues;
    }

    internal static bool AllowsEditorial(SupplementalSectionKind kind) => kind is SupplementalSectionKind.Page1WeeklyFacts or SupplementalSectionKind.NerdNotes;
    internal static bool RequiresSource(SupplementalSectionKind kind) => kind != SupplementalSectionKind.NerdNotes;
    internal static bool ImportMatches(ImportRecord import, SupplementalEvidence evidence, StagedSupplementalSection staged, ExpectedSourceDocument document, SourceFamilyConfiguration family, bool requireCurrentSource = false)
    {
        if (import.ProjectId != staged.ProjectId || import.ExpectedDocumentId != document.Id || import.SourceFamilyId != family.Id || import.Kind != "SUPPLEMENTAL" || import.ApplicableSeason != evidence.ApplicableSeason || import.ApplicableWeek != evidence.ApplicableWeek || import.ApplicableSeason != staged.Season || import.ApplicableWeek != (SupplementalInformationWorkflow.IsSeasonAuthority(staged.Kind) ? null : staged.Week)) return false;
        if (string.IsNullOrWhiteSpace(evidence.SourceLocator) || string.IsNullOrWhiteSpace(import.SourceLocator) || !string.Equals(Path.GetFullPath(evidence.SourceLocator), Path.GetFullPath(import.SourceLocator), StringComparison.OrdinalIgnoreCase)) return false;
        if (evidence.SourceAsOfUtc != import.SourceModifiedUtc) return false;
        if (requireCurrentSource && (document.SourceModifiedUtc != import.SourceModifiedUtc || !string.Equals(Path.GetFullPath(document.ResolvePath(family) ?? ""), Path.GetFullPath(import.SourceLocator), StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }
    internal static int ItemCount(SupplementalPayload payload) => payload switch { SchedulePayload p => p.Games.Count, RankingSnapshotPayload p => p.Entries.Count, IndividualStatsPayload p => p.Tables.Sum(x => x.Rows.Count), PlayerOfGamePayload p => p.Entries.Count, CoachingHistoryBaselinePayload p => p.Sections.Sum(x => x.Rows.Count), ProgramHistoryBaselinePayload p => p.Sections.Sum(x => x.Rows.Count), TeamStatisticsReportPayload p => p.Rows.Count, NerdNotesPayload p => Math.Max(1, p.Items.Count), RosterPayload p => p.Players.Count, _ => 1 };
    internal static void AssignEvidence(SupplementalPayload payload, Guid id)
    {
        void Sourced(SourcedText x) { if (x.EvidenceId == Guid.Empty) x.EvidenceId = id; }
        void Rows(IEnumerable<InformationRow> rows) { foreach (var row in rows) if (row.EvidenceId == Guid.Empty) row.EvidenceId = id; }
        switch (payload)
        {
            case Page1WeeklyFactsPayload p: Sourced(p.MindenRecord); Sourced(p.OpponentRecord); if (p.Weather.EvidenceId == Guid.Empty) p.Weather.EvidenceId = id; Rows(p.OpponentFacts.Rows); foreach (var x in p.SeriesHistory.Concat(p.WinImplications).Concat(p.StatsOfWeek).Concat(p.ByTheNumbers)) Sourced(x); Sourced(p.PriorSeasonSummary); Sourced(p.SeriesExtremes); Sourced(p.Storyline); break;
            case SchedulePayload p: foreach (var x in p.Games) if (x.EvidenceId == Guid.Empty) x.EvidenceId = id; break; case RankingSnapshotPayload p: foreach (var x in p.Entries) if (x.EvidenceId == Guid.Empty) x.EvidenceId = id; break;
            case IndividualStatsPayload p: foreach (var table in p.Tables) Rows(table.Rows); break; case PlayerOfGamePayload p: foreach (var x in p.Entries) if (x.EvidenceId == Guid.Empty) x.EvidenceId = id; break;
            case CoachingHistoryBaselinePayload p: foreach (var section in p.Sections) Rows(section.Rows); break; case ProgramHistoryBaselinePayload p: foreach (var section in p.Sections) Rows(section.Rows); break;
            case TeamStatisticsReportPayload p: foreach (var x in p.Rows) if (x.EvidenceId == Guid.Empty) x.EvidenceId = id; break; case NerdNotesPayload p: foreach (var x in p.Items) if (x.EvidenceIds.Count == 0) x.EvidenceIds.Add(id); break;
            case RosterPayload p: foreach (var x in p.Players) if (x.EvidenceId == Guid.Empty) x.EvidenceId = id; break;
        }
    }

    internal static bool Matches(SupplementalSectionKind kind, SupplementalPayload payload) => kind switch
    {
        SupplementalSectionKind.Page1WeeklyFacts => payload is Page1WeeklyFactsPayload,
        SupplementalSectionKind.MindenSchedule or SupplementalSectionKind.OpponentSchedule or SupplementalSectionKind.DistrictSchedule => payload is SchedulePayload,
        SupplementalSectionKind.Class4APoll or SupplementalSectionKind.DivisionIINonSelectRatings => payload is RankingSnapshotPayload,
        SupplementalSectionKind.IndividualOffenseSpecialTeams => payload is IndividualStatsPayload,
        SupplementalSectionKind.PlayerOfGame => payload is PlayerOfGamePayload,
        SupplementalSectionKind.CoachingHistoryBaseline => payload is CoachingHistoryBaselinePayload,
        SupplementalSectionKind.ProgramHistoryBaseline => payload is ProgramHistoryBaselinePayload,
        SupplementalSectionKind.TeamStatisticsReport => payload is TeamStatisticsReportPayload,
        SupplementalSectionKind.NerdNotes => payload is NerdNotesPayload,
        SupplementalSectionKind.MindenRoster or SupplementalSectionKind.OpponentRoster => payload is RosterPayload,
        _ => false
    };
    internal static bool IsEmpty(SupplementalPayload payload) => payload switch
    {
        Page1WeeklyFactsPayload p => Placeholder(p.MindenRecord.Value) || Placeholder(p.OpponentRecord.Value) || Placeholder(p.Weather.Temperature) || Placeholder(p.Weather.Sky) || Placeholder(p.Weather.Wind) || p.OpponentFacts.Rows.Count == 0 || p.SeriesHistory.Count == 0 || p.WinImplications.Count == 0 || p.StatsOfWeek.Count == 0 || p.ByTheNumbers.Count == 0 || Placeholder(p.PriorSeasonSummary.Value) || Placeholder(p.SeriesExtremes.Value) || Placeholder(p.Storyline.Value),
        SchedulePayload p => Placeholder(p.TeamOrGroup) || p.Games.Count == 0 || p.Games.Any(x => x.Date == default || Placeholder(x.Opponent) || Placeholder(x.Site) || Placeholder(x.ResultOrTime)),
        RankingSnapshotPayload p => Placeholder(p.Title) || p.SourceDate == default || p.Entries.Count == 0 || p.Entries.Any(x => x.Rank <= 0 || Placeholder(x.Team)),
        IndividualStatsPayload p => Placeholder(p.ProductionLabel) || p.StatisticalSeason is < 1900 or > 2200 || p.Tables.Count == 0 || p.Tables.Any(x => Placeholder(x.Title) || x.Columns.Count == 0 || x.Rows.Count == 0),
        PlayerOfGamePayload p => p.Entries.Count == 0 || p.Entries.Any(x => x.Week <= 0 || Placeholder(x.Player) || Placeholder(x.Description)),
        CoachingHistoryBaselinePayload p => p.BaselineThroughSeason is < 1900 or > 2200 || p.Sections.Count == 0 || p.Sections.Any(x => Placeholder(x.Title) || x.Rows.Count == 0),
        ProgramHistoryBaselinePayload p => p.BaselineThroughSeason is < 1900 or > 2200 || p.Sections.Count == 0 || p.Sections.Any(x => Placeholder(x.Title) || x.Rows.Count == 0),
        TeamStatisticsReportPayload p => p.StatisticalSeason is < 1900 or > 2200 || Placeholder(p.ReportLabel) || p.Rows.Count == 0 || p.Rows.Any(x => Placeholder(x.Label) || Placeholder(x.Minden) || Placeholder(x.Opponent)),
        NerdNotesPayload p => Placeholder(p.EditorialDirection),
        RosterPayload p => Placeholder(p.Team) || p.Season is < 1900 or > 2200 || p.Players.Count == 0 || p.Players.Any(x => Placeholder(x.SourceName) || Placeholder(x.DisplayName) || Placeholder(x.Number) || Placeholder(x.Position) || Placeholder(x.Grade)),
        _ => true
    };
    private static bool Placeholder(string value) => string.IsNullOrWhiteSpace(value) || value.Contains("TBD", StringComparison.OrdinalIgnoreCase) || value.Contains("VERIFY", StringComparison.OrdinalIgnoreCase) || value.Contains("Add this", StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<Guid> ReferencedEvidence(SupplementalPayload payload) => payload switch
    {
        Page1WeeklyFactsPayload p => new[] { p.MindenRecord.EvidenceId, p.OpponentRecord.EvidenceId, p.Weather.EvidenceId, p.PriorSeasonSummary.EvidenceId, p.SeriesExtremes.EvidenceId, p.Storyline.EvidenceId }.Concat(p.OpponentFacts.Rows.Select(x => x.EvidenceId)).Concat(p.SeriesHistory.Select(x => x.EvidenceId)).Concat(p.WinImplications.Select(x => x.EvidenceId)).Concat(p.StatsOfWeek.Select(x => x.EvidenceId)).Concat(p.ByTheNumbers.Select(x => x.EvidenceId)),
        SchedulePayload p => p.Games.Select(x => x.EvidenceId), RankingSnapshotPayload p => p.Entries.Select(x => x.EvidenceId),
        IndividualStatsPayload p => p.Tables.SelectMany(x => x.Rows).Select(x => x.EvidenceId), PlayerOfGamePayload p => p.Entries.Select(x => x.EvidenceId),
        CoachingHistoryBaselinePayload p => p.Sections.SelectMany(x => x.Rows).Select(x => x.EvidenceId), ProgramHistoryBaselinePayload p => p.Sections.SelectMany(x => x.Rows).Select(x => x.EvidenceId),
        TeamStatisticsReportPayload p => p.Rows.Select(x => x.EvidenceId), NerdNotesPayload p => p.Items.SelectMany(x => x.EvidenceIds), RosterPayload p => p.Players.Select(x => x.EvidenceId), _ => []
    };
    internal static IEnumerable<Guid> Page1SourceEvidenceIds(Page1WeeklyFactsPayload p) =>
        new[] { p.MindenRecord.EvidenceId, p.OpponentRecord.EvidenceId, p.Weather.EvidenceId, p.PriorSeasonSummary.EvidenceId, p.SeriesExtremes.EvidenceId }
            .Concat(p.OpponentFacts.Rows.Select(x => x.EvidenceId))
            .Concat(p.SeriesHistory.Select(x => x.EvidenceId))
            .Concat(p.StatsOfWeek.Select(x => x.EvidenceId))
            .Concat(p.ByTheNumbers.Select(x => x.EvidenceId));
}

public static class WeeklyGameNotesInformationAssembler
{
    public static WeeklyGameNotesInformationPackage Build(BuilderWorkspace workspace, GameNotesProject project)
    {
        if (project.Season is null || project.Week is null) return Empty(project);
        var statuses = new List<InformationRequirementStatus>();
        var resolved = Enum.GetValues<SupplementalSectionKind>().ToDictionary(kind => kind, kind => Current(project, kind));
        void Add(int page, string key, string label, AuthorityReference? authority, string missing, ReadinessSeverity acceptedSeverity = ReadinessSeverity.Ready)
        {
            var available = authority is not null; statuses.Add(new(key, label, RequirementDisposition.Required, available ? InformationAvailability.Accepted : InformationAvailability.Missing, available ? acceptedSeverity : ReadinessSeverity.Blocking, available ? "Accepted authority available." : missing, authority is null ? [] : [authority], []));
        }
        var identityReady = project.IsIdentityComplete;
        statuses.Add(new("P1.ProjectIdentity", "Weekly project identity", RequirementDisposition.Required, identityReady ? InformationAvailability.Accepted : InformationAvailability.Missing, identityReady ? ReadinessSeverity.Ready : ReadinessSeverity.Blocking, identityReady ? "WP 1 project identity complete." : "Complete season, week, opponent, date, kickoff and venue.", identityReady ? [new(AuthorityDomain.WeeklyProject, project.Id, null, null, null, null, null)] : [], []));
        var lookingBack = project.CompletedGames.FirstOrDefault(x => x.IsCurrentAuthority && x.Id == project.CurrentAcceptedGameId); Add(1, "P1.LookingBack", "Looking Back game", lookingBack is null ? null : Ref(lookingBack), "Accept the prior-game WP 2 report.");
        AddSupplement(1, SupplementalSectionKind.Page1WeeklyFacts, "Page 1 weekly facts");
        AddSupplement(2, SupplementalSectionKind.MindenSchedule, "Minden schedule"); AddSupplement(2, SupplementalSectionKind.OpponentSchedule, "Opponent schedule"); AddSupplement(2, SupplementalSectionKind.DistrictSchedule, "District schedule"); AddSupplement(2, SupplementalSectionKind.Class4APoll, "LSWA Class 4A poll"); AddSupplement(2, SupplementalSectionKind.DivisionIINonSelectRatings, "Division II Non-Select ratings");
        AddSupplement(3, SupplementalSectionKind.IndividualOffenseSpecialTeams, project.Week == 1 ? "Final prior-season offense/special teams" : "Current cumulative offense/special teams");
        var statSeason = project.Week == 1 ? project.Season.Value - 1 : project.Season.Value; var candidates = workspace.Projects.SelectMany(x => x.AcceptedDefensiveSeasonTotals).Where(x => x.IsCurrentAuthority && x.Season == statSeason).ToList();
        AcceptedDefensiveSeasonTotals? totals = null; var totalsConflict = false;
        if (project.DefensiveSeasonTotalsAuthorityId is Guid selectedId) { totals = candidates.SingleOrDefault(x => x.Id == selectedId); totalsConflict = totals is null; }
        else if (candidates.Count == 1) totals = candidates[0]; else if (candidates.Count > 1) totalsConflict = true;
        if (totalsConflict) statuses.Add(new("P3.Defense", "Defensive season totals", RequirementDisposition.Required, InformationAvailability.Available, ReadinessSeverity.Blocking, project.DefensiveSeasonTotalsAuthorityId is null ? $"Multiple accepted WP 3 TOTALS authorities exist for {statSeason}; select one explicitly." : "The explicitly selected WP 3 TOTALS authority is unavailable or ineligible.", candidates.Select(Ref).ToList(), candidates.Select(x => x.ExpectedDocumentId).Distinct().ToList()));
        else Add(3, "P3.Defense", "Defensive season totals", totals is null ? null : Ref(totals), $"Accept WP 3 TOTALS for {statSeason}.");
        AddSupplement(3, SupplementalSectionKind.PlayerOfGame, "Player of the Game"); AddSupplement(3, SupplementalSectionKind.MindenRoster, "Minden roster membership");
        AddSupplement(4, SupplementalSectionKind.CoachingHistoryBaseline, "Coaching history baseline"); AddSupplement(5, SupplementalSectionKind.ProgramHistoryBaseline, "Program history baseline"); AddSupplement(6, SupplementalSectionKind.TeamStatisticsReport, project.Week == 1 ? "Final prior-season Team Statistics report" : "Current cumulative Team Statistics report");
        AddSupplement(7, SupplementalSectionKind.NerdNotes, "Editorial disposition"); AddSupplement(8, SupplementalSectionKind.MindenRoster, "Minden roster"); AddSupplement(9, SupplementalSectionKind.OpponentRoster, "Opponent roster");
        statuses.Add(new("P10.Information", "Notes-page factual intake", RequirementDisposition.NotApplicable, InformationAvailability.Accepted, ReadinessSeverity.Ready, "Page 10 requires no factual authority.", [], []));
        IReadOnlyList<InformationRequirementStatus> PageRequirements(int page) => statuses.Where(x => x.RequirementKey.StartsWith($"P{page}.")).ToList();
        var page1 = new Page1InformationPackage(project, lookingBack, resolved[SupplementalSectionKind.Page1WeeklyFacts], PageRequirements(1));
        var page2 = new Page2InformationPackage(resolved[SupplementalSectionKind.MindenSchedule], resolved[SupplementalSectionKind.OpponentSchedule], resolved[SupplementalSectionKind.DistrictSchedule], resolved[SupplementalSectionKind.Class4APoll], resolved[SupplementalSectionKind.DivisionIINonSelectRatings], PageRequirements(2));
        var roster = resolved[SupplementalSectionKind.MindenRoster];
        var page3 = new Page3InformationPackage(resolved[SupplementalSectionKind.IndividualOffenseSpecialTeams], new(totals, candidates, totalsConflict), resolved[SupplementalSectionKind.PlayerOfGame], roster, PageRequirements(3));
        var page4 = new Page4InformationPackage(resolved[SupplementalSectionKind.CoachingHistoryBaseline], PageRequirements(4)); var page5 = new Page5InformationPackage(resolved[SupplementalSectionKind.ProgramHistoryBaseline], PageRequirements(5)); var page6 = new Page6InformationPackage(resolved[SupplementalSectionKind.TeamStatisticsReport], PageRequirements(6));
        var page7 = new Page7InformationPackage(resolved[SupplementalSectionKind.NerdNotes], PageRequirements(7)); var page8 = new Page8InformationPackage(roster, PageRequirements(8)); var page9 = new Page9InformationPackage(resolved[SupplementalSectionKind.OpponentRoster], PageRequirements(9)); var page10 = new Page10InformationPackage(PageRequirements(10));
        IReadOnlyList<IPageInformationPackage> pages = [page1, page2, page3, page4, page5, page6, page7, page8, page9, page10];
        var overall = statuses.Any(x => x.Severity == ReadinessSeverity.Blocking) ? ReadinessSeverity.Blocking : statuses.Any(x => x.Severity == ReadinessSeverity.Advisory) ? ReadinessSeverity.Advisory : ReadinessSeverity.Ready;
        return new(project.Id, project.Season.Value, project.Week.Value, DateTime.UtcNow, page1, page2, page3, page4, page5, page6, page7, page8, page9, page10, pages, statuses, overall);

        void AddSupplement(int page, SupplementalSectionKind kind, string label)
        {
            var accepted = resolved[kind]; var staged = project.StagedSupplementalSections.LastOrDefault(x => x.Kind == kind && x.Season == project.Season && (SupplementalInformationWorkflow.IsSeasonAuthority(kind) || x.Week == project.Week));
            if (accepted is null)
            {
                var documents = staged?.Evidence.Where(x => x.ExpectedDocumentId is not null).Select(x => project.ExpectedDocuments.FirstOrDefault(d => d.Id == x.ExpectedDocumentId)).Where(x => x is not null).Cast<ExpectedSourceDocument>().ToList() ?? [];
                Refresh(documents);
                var availability = documents.Any(x => x.Status == SourceDocumentStatus.Stale) ? InformationAvailability.Stale : documents.Any(x => !x.HasHealthySource) ? InformationAvailability.Missing : documents.Any(x => x.Verification != DocumentVerificationState.Verified) ? InformationAvailability.Unverified : staged is null ? InformationAvailability.Missing : InformationAvailability.Available;
                statuses.Add(new($"P{page}.{kind}", label, RequirementDisposition.Required, availability, ReadinessSeverity.Blocking, staged is null ? $"Accept {label} supplemental information." : $"{label} is staged but has not established authority.", [], documents.Select(x => x.Id).ToList())); return;
            }
            var sourceDocuments = accepted.Evidence.Where(x => x.ExpectedDocumentId is not null).Select(x => project.ExpectedDocuments.FirstOrDefault(d => d.Id == x.ExpectedDocumentId)).Where(x => x is not null).Cast<ExpectedSourceDocument>().ToList();
            Refresh(sourceDocuments);
            var sourceChanged = accepted.Evidence.Where(x => x.Kind == SupplementalEvidenceKind.ExpectedSourceDocument).Any(e => project.ExpectedDocuments.FirstOrDefault(d => d.Id == e.ExpectedDocumentId)?.SourceModifiedUtc != e.SourceAsOfUtc);
            var healthAvailability = sourceDocuments.Any(x => x.Status == SourceDocumentStatus.Missing) ? InformationAvailability.Missing : sourceChanged || sourceDocuments.Any(x => x.Status == SourceDocumentStatus.Stale) ? InformationAvailability.Stale : sourceDocuments.Any(x => !x.HasHealthySource) ? InformationAvailability.Missing : sourceDocuments.Any(x => x.Verification != DocumentVerificationState.Verified) ? InformationAvailability.Unverified : InformationAvailability.Accepted;
            var severity = accepted.AcceptedIssues.Any(x => x.Severity == InformationIssueSeverity.Blocking) || healthAvailability is InformationAvailability.Stale or InformationAvailability.Missing or InformationAvailability.Unverified ? ReadinessSeverity.Blocking : accepted.AcceptedIssues.Any(x => x.Severity == InformationIssueSeverity.Advisory) ? ReadinessSeverity.Advisory : ReadinessSeverity.Ready;
            var authorities = accepted.Evidence.Count == 0 ? [Ref(accepted)] : accepted.Evidence.Select(x => new AuthorityReference(AuthorityDomain.AcceptedSupplementalSection, accepted.Id, accepted.StagedSectionId, x.ImportRecordId, x.ExpectedDocumentId, x.SourceFamilyId, accepted.AcceptedUtc)).ToList();
            statuses.Add(new($"P{page}.{kind}", label, RequirementDisposition.Required, healthAvailability, severity, healthAvailability == InformationAvailability.Accepted ? "Accepted authority available." : "Accepted authority is retained, but current source health/verification blocks weekly readiness.", authorities, sourceDocuments.Select(x => x.Id).ToList()));
        }
        void Refresh(IEnumerable<ExpectedSourceDocument> documents)
        {
            foreach (var document in documents.DistinctBy(x => x.Id)) document.RefreshStatus(workspace.SourceFamilies.FirstOrDefault(x => x.Id == document.SourceFamilyId));
        }
    }
    private static AcceptedSupplementalSection? Current(GameNotesProject p, SupplementalSectionKind kind) => p.AcceptedSupplementalSections.FirstOrDefault(x => x.IsCurrentAuthority && x.Kind == kind && x.Season == p.Season && (SupplementalInformationWorkflow.IsSeasonAuthority(kind) || x.Week == p.Week));
    private static WeeklyGameNotesInformationPackage Empty(GameNotesProject p)
    {
        var status = new InformationRequirementStatus("P1.ProjectIdentity", "Weekly project identity", RequirementDisposition.Required, InformationAvailability.Missing, ReadinessSeverity.Blocking, "Complete project season and week.", [], []); IReadOnlyList<InformationRequirementStatus> none = [];
        var p1 = new Page1InformationPackage(p, null, null, [status]); var p2 = new Page2InformationPackage(null, null, null, null, null, none); var p3 = new Page3InformationPackage(null, new(null, [], false), null, null, none); var p4 = new Page4InformationPackage(null, none); var p5 = new Page5InformationPackage(null, none); var p6 = new Page6InformationPackage(null, none); var p7 = new Page7InformationPackage(null, none); var p8 = new Page8InformationPackage(null, none); var p9 = new Page9InformationPackage(null, none); var p10 = new Page10InformationPackage(none); IReadOnlyList<IPageInformationPackage> pages = [p1, p2, p3, p4, p5, p6, p7, p8, p9, p10];
        return new(p.Id, p.Season ?? 0, p.Week ?? 0, DateTime.UtcNow, p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, pages, [status], ReadinessSeverity.Blocking);
    }
    private static AuthorityReference Ref(CompletedGame x) => new(AuthorityDomain.AcceptedCompletedGame, x.Id, x.StagedReportId, x.ImportRecordId, x.ExpectedDocumentId, x.SourceFamilyId, x.AcceptedUtc);
    private static AuthorityReference Ref(AcceptedDefensiveSeasonTotals x) => new(AuthorityDomain.AcceptedDefensiveTotals, x.Id, x.StagedSectionId, x.ImportRecordId, x.ExpectedDocumentId, x.SourceFamilyId, x.AcceptedUtc);
    private static AuthorityReference Ref(AcceptedSupplementalSection x) => new(AuthorityDomain.AcceptedSupplementalSection, x.Id, x.StagedSectionId, null, null, null, x.AcceptedUtc);
}

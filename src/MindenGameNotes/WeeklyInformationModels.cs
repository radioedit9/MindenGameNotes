using System.Text.Json;
using System.Text.Json.Serialization;

namespace MindenGameNotes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthorityDomain { WeeklyProject, AcceptedCompletedGame, AcceptedDefensiveGame, AcceptedDefensiveTotals, AcceptedSupplementalSection }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequirementDisposition { Required, Optional, NotApplicable }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InformationAvailability { Missing, Stale, Unverified, Available, Accepted }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReadinessSeverity { Ready, Advisory, Blocking }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupplementalEvidenceKind { ExpectedSourceDocument, EditorialDecision }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NerdNoteDisposition { Page7, WebSocial, BroadcastResearch, Multiple, Hold, NoPublication }
[JsonConverter(typeof(StrictStatOfWeekDispositionConverter))]
public enum StatOfWeekDisposition { Selected, NoSelection }
public sealed class StrictStatOfWeekDispositionConverter : JsonConverter<StatOfWeekDisposition>
{
    public override StatOfWeekDisposition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !Enum.TryParse<StatOfWeekDisposition>(reader.GetString(), false, out var value) || !Enum.IsDefined(value)) throw new JsonException("Stat of the Week disposition must be Selected or NoSelection.");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, StatOfWeekDisposition value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Stat of the Week disposition must be Selected or NoSelection.");
        writer.WriteStringValue(value.ToString());
    }
}
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupplementalSectionKind
{
    Page1WeeklyFacts, MindenSchedule, OpponentSchedule, DistrictSchedule, Class4APoll, DivisionIINonSelectRatings,
    IndividualOffenseSpecialTeams, PlayerOfGame, CoachingHistoryBaseline, ProgramHistoryBaseline, TeamStatisticsReport,
    NerdNotes, MindenRoster, OpponentRoster
}

public sealed record AuthorityReference(AuthorityDomain Domain, Guid AuthorityId, Guid? StagedAuthorityId, Guid? ImportRecordId, Guid? ExpectedDocumentId, Guid? SourceFamilyId, DateTime? AcceptedUtc);
public sealed record InformationRequirementStatus(string RequirementKey, string Label, RequirementDisposition Disposition, InformationAvailability Availability, ReadinessSeverity Severity, string Message, IReadOnlyList<AuthorityReference> Authorities, IReadOnlyList<Guid> ExpectedDocumentIds);

public sealed class SupplementalEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SupplementalEvidenceKind Kind { get; set; }
    public Guid? ExpectedDocumentId { get; set; }
    public Guid? SourceFamilyId { get; set; }
    public Guid? ImportRecordId { get; set; }
    public string AuthorityName { get; set; } = "";
    public string SourceLocator { get; set; } = "";
    public DateTime? SourceAsOfUtc { get; set; }
    public int? ApplicableSeason { get; set; }
    public int? ApplicableWeek { get; set; }
    public string Note { get; set; } = "";
}

public sealed class SourcedText { public string Value { get; set; } = ""; public Guid EvidenceId { get; set; } }
public sealed class StatOfWeekSelection
{
    public StatOfWeekDisposition Disposition { get; set; }
    public string Headline { get; set; } = "";
    public string DisplayText { get; set; } = "";
    public List<Guid> SupportingFactEvidenceIds { get; set; } = [];
    public Guid EditorialEvidenceId { get; set; }
}
public sealed class InformationRow { public string Label { get; set; } = ""; public List<string> Values { get; set; } = []; public Guid EvidenceId { get; set; } }
public sealed class WeatherSnapshot { public string Temperature { get; set; } = ""; public string Sky { get; set; } = ""; public string Wind { get; set; } = ""; public Guid EvidenceId { get; set; } }
public sealed class OpponentQuickFacts { public List<InformationRow> Rows { get; set; } = []; }
public sealed class ScheduleEntry { public DateTime Date { get; set; } public string Opponent { get; set; } = ""; public string Site { get; set; } = ""; public string ResultOrTime { get; set; } = ""; public bool IsDistrictGame { get; set; } public Guid EvidenceId { get; set; } }
public sealed class RankingEntry { public int Rank { get; set; } public string Team { get; set; } = ""; public string Record { get; set; } = ""; public string Value { get; set; } = ""; public Guid EvidenceId { get; set; } }
public sealed class SourceStatTable { public string Title { get; set; } = ""; public List<string> Columns { get; set; } = []; public List<InformationRow> Rows { get; set; } = []; }
public sealed class PlayerOfGameEntry { public int Week { get; set; } public string Player { get; set; } = ""; public string Description { get; set; } = ""; public Guid EvidenceId { get; set; } }
public sealed class BaselineSection { public string Title { get; set; } = ""; public List<InformationRow> Rows { get; set; } = []; }
public sealed class ReportedTeamStatisticRow { public string Label { get; set; } = ""; public string Minden { get; set; } = ""; public string Opponent { get; set; } = ""; public Guid EvidenceId { get; set; } }
public sealed class NerdNoteItem { public string Title { get; set; } = ""; public string Content { get; set; } = ""; public NerdNoteDisposition Disposition { get; set; } public List<Guid> EvidenceIds { get; set; } = []; public bool Verified { get; set; } public string Note { get; set; } = ""; }
public sealed class RosterEntry { public string SourceName { get; set; } = ""; public string DisplayName { get; set; } = ""; public string Number { get; set; } = ""; public string Position { get; set; } = ""; public string Grade { get; set; } = ""; public Guid EvidenceId { get; set; } }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Page1WeeklyFactsPayload), "page1")]
[JsonDerivedType(typeof(SchedulePayload), "schedule")]
[JsonDerivedType(typeof(RankingSnapshotPayload), "ranking")]
[JsonDerivedType(typeof(IndividualStatsPayload), "individual")]
[JsonDerivedType(typeof(PlayerOfGamePayload), "playerOfGame")]
[JsonDerivedType(typeof(CoachingHistoryBaselinePayload), "coaching")]
[JsonDerivedType(typeof(ProgramHistoryBaselinePayload), "program")]
[JsonDerivedType(typeof(TeamStatisticsReportPayload), "teamStats")]
[JsonDerivedType(typeof(NerdNotesPayload), "nerdNotes")]
[JsonDerivedType(typeof(RosterPayload), "roster")]
public abstract class SupplementalPayload { }

public sealed class Page1WeeklyFactsPayload : SupplementalPayload
{
    public SourcedText MindenRecord { get; set; } = new(); public SourcedText OpponentRecord { get; set; } = new(); public WeatherSnapshot Weather { get; set; } = new(); public OpponentQuickFacts OpponentFacts { get; set; } = new();
    public List<SourcedText> SeriesHistory { get; set; } = []; public List<SourcedText> WinImplications { get; set; } = []; public List<SourcedText> StatsOfWeek { get; set; } = []; public StatOfWeekSelection? StatOfWeekSelection { get; set; } public List<SourcedText> ByTheNumbers { get; set; } = [];
    public SourcedText PriorSeasonSummary { get; set; } = new(); public SourcedText SeriesExtremes { get; set; } = new(); public SourcedText Storyline { get; set; } = new();
}
public sealed class SchedulePayload : SupplementalPayload { public string TeamOrGroup { get; set; } = ""; public List<ScheduleEntry> Games { get; set; } = []; }
public sealed class RankingSnapshotPayload : SupplementalPayload { public string Title { get; set; } = ""; public DateTime SourceDate { get; set; } public List<RankingEntry> Entries { get; set; } = []; public string SourceFooter { get; set; } = ""; }
public sealed class IndividualStatsPayload : SupplementalPayload { public string ProductionLabel { get; set; } = ""; public int StatisticalSeason { get; set; } public List<SourceStatTable> Tables { get; set; } = []; }
public sealed class PlayerOfGamePayload : SupplementalPayload { public List<PlayerOfGameEntry> Entries { get; set; } = []; }
public sealed class CoachingHistoryBaselinePayload : SupplementalPayload { public int BaselineThroughSeason { get; set; } public List<BaselineSection> Sections { get; set; } = []; }
public sealed class ProgramHistoryBaselinePayload : SupplementalPayload { public int BaselineThroughSeason { get; set; } public List<BaselineSection> Sections { get; set; } = []; }
public sealed class TeamStatisticsReportPayload : SupplementalPayload { public int StatisticalSeason { get; set; } public string ReportLabel { get; set; } = ""; public List<ReportedTeamStatisticRow> Rows { get; set; } = []; }
public sealed class NerdNotesPayload : SupplementalPayload { public string EditorialDirection { get; set; } = ""; public List<NerdNoteItem> Items { get; set; } = []; }
public sealed class RosterPayload : SupplementalPayload { public string Team { get; set; } = ""; public int Season { get; set; } public List<RosterEntry> Players { get; set; } = []; }

public sealed class StagedSupplementalSection
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid ProjectId { get; set; } public SupplementalSectionKind Kind { get; set; } public int Season { get; set; } public int? Week { get; set; } public int? BaselineThroughSeason { get; set; }
    public SupplementalPayload Payload { get; set; } = null!; public List<SupplementalEvidence> Evidence { get; set; } = []; public List<InformationValidationIssue> Issues { get; set; } = [];
    public ReportReviewState State { get; set; } = ReportReviewState.PendingReview; public string ReviewNote { get; set; } = ""; public DateTime ParsedUtc { get; set; } = DateTime.UtcNow; public DateTime? ReviewedUtc { get; set; } public DateTime? AcceptedUtc { get; set; }
    [JsonIgnore] public bool HasBlockingIssues => Issues.Any(x => x.Severity == InformationIssueSeverity.Blocking);
    [JsonIgnore] public bool HasAdvisories => Issues.Any(x => x.Severity == InformationIssueSeverity.Advisory);
}

public sealed class AcceptedSupplementalSection
{
    public Guid Id { get; set; } = Guid.NewGuid(); public Guid ProjectId { get; set; } public Guid StagedSectionId { get; set; } public SupplementalSectionKind Kind { get; set; } public int Season { get; set; } public int? Week { get; set; } public int? BaselineThroughSeason { get; set; }
    public SupplementalPayload Payload { get; set; } = null!; public List<SupplementalEvidence> Evidence { get; set; } = []; public List<InformationValidationIssue> AcceptedIssues { get; set; } = [];
    public string AcceptanceNote { get; set; } = ""; public DateTime AcceptedUtc { get; set; } public bool IsCurrentAuthority { get; set; } = true;
}

public interface IPageInformationPackage { int PageNumber { get; } string Purpose { get; } IReadOnlyList<InformationRequirementStatus> Requirements { get; } }
public sealed record Page1InformationPackage(GameNotesProject WeeklyProject, CompletedGame? LookingBack, AcceptedSupplementalSection? WeeklyFacts, IReadOnlyList<InformationRequirementStatus> Requirements) : IPageInformationPackage { public int PageNumber => 1; public string Purpose => "Game Dashboard"; }
public sealed record Page2InformationPackage(AcceptedSupplementalSection? MindenSchedule, AcceptedSupplementalSection? OpponentSchedule, AcceptedSupplementalSection? DistrictSchedule, AcceptedSupplementalSection? Class4APoll, AcceptedSupplementalSection? PowerRatings, IReadOnlyList<InformationRequirementStatus> Requirements) : IPageInformationPackage { public int PageNumber => 2; public string Purpose => "Schedules & Weekly Landscape"; }
public sealed record DefensiveTotalsResolution(AcceptedDefensiveSeasonTotals? Authority, IReadOnlyList<AcceptedDefensiveSeasonTotals> Candidates, bool HasConflict);
public sealed record Page3InformationPackage(AcceptedSupplementalSection? OffenseSpecialTeams, DefensiveTotalsResolution Defense, AcceptedSupplementalSection? PlayerOfGame, AcceptedSupplementalSection? MindenRoster, IReadOnlyList<InformationRequirementStatus> Requirements) : IPageInformationPackage { public int PageNumber => 3; public string Purpose => "Individual Stats"; }
public sealed record Page4InformationPackage(AcceptedSupplementalSection? CoachingHistory, IReadOnlyList<InformationRequirementStatus> Requirements) : IPageInformationPackage { public int PageNumber => 4; public string Purpose => "Coaching History / Heard Era"; }
public sealed record Page5InformationPackage(AcceptedSupplementalSection? ProgramHistory, IReadOnlyList<InformationRequirementStatus> Requirements) : IPageInformationPackage { public int PageNumber => 5; public string Purpose => "Program History"; }
public sealed record Page6InformationPackage(AcceptedSupplementalSection? TeamStatistics, IReadOnlyList<InformationRequirementStatus> Requirements) : IPageInformationPackage { public int PageNumber => 6; public string Purpose => "Team Statistics"; }
public sealed record Page7InformationPackage(AcceptedSupplementalSection? EditorialDisposition, IReadOnlyList<InformationRequirementStatus> Requirements) : IPageInformationPackage { public int PageNumber => 7; public string Purpose => "Mark's Nerd Notes"; }
public sealed record Page8InformationPackage(AcceptedSupplementalSection? MindenRoster, IReadOnlyList<InformationRequirementStatus> Requirements) : IPageInformationPackage { public int PageNumber => 8; public string Purpose => "Minden Roster"; }
public sealed record Page9InformationPackage(AcceptedSupplementalSection? OpponentRoster, IReadOnlyList<InformationRequirementStatus> Requirements) : IPageInformationPackage { public int PageNumber => 9; public string Purpose => "Opponent Roster"; }
public sealed record Page10InformationPackage(IReadOnlyList<InformationRequirementStatus> Requirements) : IPageInformationPackage { public int PageNumber => 10; public string Purpose => "Notes"; }

public sealed record WeeklyGameNotesInformationPackage(Guid ProjectId, int Season, int Week, DateTime GeneratedUtc, Page1InformationPackage Page1, Page2InformationPackage Page2, Page3InformationPackage Page3, Page4InformationPackage Page4, Page5InformationPackage Page5, Page6InformationPackage Page6, Page7InformationPackage Page7, Page8InformationPackage Page8, Page9InformationPackage Page9, Page10InformationPackage Page10, IReadOnlyList<IPageInformationPackage> Pages, IReadOnlyList<InformationRequirementStatus> Requirements, ReadinessSeverity OverallSeverity)
{
    public bool IsReady => OverallSeverity != ReadinessSeverity.Blocking;
}

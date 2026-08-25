namespace MindenGameNotes;

public enum PublicationStatOfWeekState { Unresolved, Selected, NoSelection }

public sealed record PublicationEvidenceReference(Guid EvidenceId, SupplementalEvidenceKind Kind, Guid? ExpectedDocumentId, Guid? ImportRecordId, Guid? SourceFamilyId);
public sealed record PublicationPageContext(int PageNumber, string Purpose, PageProductionState ProductionState, bool IsPublicationReady, IReadOnlyList<InformationRequirementStatus> Blockers, IReadOnlyList<InformationRequirementStatus> Advisories, IReadOnlyList<AuthorityReference> Authorities, IReadOnlyList<PublicationEvidenceReference> Evidence, IReadOnlyList<string> ChangedRequirementKeys);
public sealed record AcceptedPublicationSection<TPayload>(string RequirementKey, AcceptedSupplementalSection Authority, TPayload Content, IReadOnlyList<PublicationEvidenceReference> Evidence) where TPayload : SupplementalPayload;
public sealed record WeeklyGameIdentity(Guid ProjectId, int Season, int Week, string Opponent, DateTime? GameDate, TimeOnly? KickoffTime, string Venue, string School, string TeamName);
public sealed record StatOfWeekPublicationSupply(PublicationStatOfWeekState State, StatOfWeekSelection? Selection, IReadOnlyList<SourcedText> SupportingFacts, PublicationEvidenceReference? EditorialEvidence);

public interface IWeeklyPublicationPage { PublicationPageContext Context { get; } }
public sealed record Page1PublicationSupply(PublicationPageContext Context, WeeklyGameIdentity Identity, CompletedGame? LookingBack, AcceptedPublicationSection<Page1WeeklyFactsPayload>? WeeklyFacts, StatOfWeekPublicationSupply StatOfWeek) : IWeeklyPublicationPage;
public sealed record Page2PublicationSupply(PublicationPageContext Context, AcceptedPublicationSection<SchedulePayload>? MindenSchedule, AcceptedPublicationSection<SchedulePayload>? OpponentSchedule, AcceptedPublicationSection<SchedulePayload>? DistrictSchedule, AcceptedPublicationSection<RankingSnapshotPayload>? Class4APoll, AcceptedPublicationSection<RankingSnapshotPayload>? DivisionIINonSelectRatings) : IWeeklyPublicationPage;
public sealed record Page3PublicationSupply(PublicationPageContext Context, AcceptedPublicationSection<IndividualStatsPayload>? OffenseSpecialTeams, AcceptedDefensiveSeasonTotals? DefensiveTotals, bool DefensiveTotalsConflict, AcceptedPublicationSection<PlayerOfGamePayload>? PlayerOfGame, AcceptedPublicationSection<RosterPayload>? MindenRoster) : IWeeklyPublicationPage;
public sealed record Page4PublicationSupply(PublicationPageContext Context, AcceptedPublicationSection<CoachingHistoryBaselinePayload>? CoachingHistoryBaseline) : IWeeklyPublicationPage;
public sealed record Page5PublicationSupply(PublicationPageContext Context, AcceptedPublicationSection<ProgramHistoryBaselinePayload>? ProgramHistoryBaseline) : IWeeklyPublicationPage;
public sealed record Page6PublicationSupply(PublicationPageContext Context, AcceptedPublicationSection<TeamStatisticsReportPayload>? TeamStatistics) : IWeeklyPublicationPage;
public sealed record Page7PublicationSupply(PublicationPageContext Context, AcceptedPublicationSection<NerdNotesPayload>? EditorialDisposition) : IWeeklyPublicationPage;
public sealed record Page8PublicationSupply(PublicationPageContext Context, AcceptedPublicationSection<RosterPayload>? MindenRoster) : IWeeklyPublicationPage;
public sealed record Page9PublicationSupply(PublicationPageContext Context, AcceptedPublicationSection<RosterPayload>? OpponentRoster) : IWeeklyPublicationPage;
public sealed record Page10PublicationSupply(PublicationPageContext Context, RequirementDisposition WeeklyContentDisposition) : IWeeklyPublicationPage;

public sealed record WeeklyPublicationSupply(Guid ProjectId, int Season, int Week, DateTime GeneratedUtc, bool IsClearedForFinalPublication, string SemanticIdentity, Page1PublicationSupply Page1, Page2PublicationSupply Page2, Page3PublicationSupply Page3, Page4PublicationSupply Page4, Page5PublicationSupply Page5, Page6PublicationSupply Page6, Page7PublicationSupply Page7, Page8PublicationSupply Page8, Page9PublicationSupply Page9, Page10PublicationSupply Page10, IReadOnlyList<IWeeklyPublicationPage> Pages);

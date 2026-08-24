namespace MindenGameNotes;

public enum PageProductionState { Waiting, ProductionUsable, PublicationReady }

public sealed record PageProductionStatus(
    int PageNumber,
    string Purpose,
    IPageInformationPackage Information,
    PageProductionState State,
    IReadOnlyList<InformationRequirementStatus> WorkBlockingRequirements,
    IReadOnlyList<InformationRequirementStatus> PublicationBlockingRequirements,
    IReadOnlyList<InformationRequirementStatus> Advisories,
    IReadOnlyDictionary<string, string> RequirementFingerprints,
    string Fingerprint);

public sealed record WeeklyProductionHandoff(
    WeeklyGameNotesInformationPackage InformationPackage,
    IReadOnlyList<PageProductionStatus> Pages,
    bool IsClearedForFinalPublication,
    IReadOnlyList<InformationRequirementStatus> RemainingBlockers,
    IReadOnlyList<InformationRequirementStatus> Advisories,
    DateTime EvaluatedUtc);

public sealed record PageProductionChange(
    int PageNumber,
    string PreviousFingerprint,
    string CurrentFingerprint,
    IReadOnlyList<string> ChangedRequirementKeys);

public sealed record WeeklyProductionComparison(
    WeeklyProductionHandoff Previous,
    WeeklyProductionHandoff Current,
    IReadOnlyList<PageProductionChange> ChangedPages);

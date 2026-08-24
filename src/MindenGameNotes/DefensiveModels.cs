using System.Text.Json.Serialization;

namespace MindenGameNotes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DefensiveCellState { Absent, PresentBlank, Numeric, Invalid }

public sealed class DefensiveSourceValue
{
    public DefensiveCellState State { get; set; }
    public string CellReference { get; set; } = "";
    public string Raw { get; set; } = "";
    public decimal? Numeric { get; set; }
    public string? Formula { get; set; }
    [JsonIgnore] public bool IsExplicitZero => State == DefensiveCellState.Numeric && Numeric == 0m;
}

public sealed class DefensiveStatLine
{
    public string PlayerName { get; set; } = "";
    public string JerseyNumber { get; set; } = "";
    public string WorksheetName { get; set; } = "";
    public int SourceRow { get; set; }
    public DefensiveSourceValue Solo { get; set; } = new();
    public DefensiveSourceValue Assisted { get; set; } = new();
    public DefensiveSourceValue Total { get; set; } = new();
    public DefensiveSourceValue TacklesForLoss { get; set; } = new();
    public DefensiveSourceValue Sacks { get; set; } = new();
    public DefensiveSourceValue QuarterbackHurries { get; set; } = new();
    public DefensiveSourceValue PassBreakups { get; set; } = new();
    public DefensiveSourceValue Interceptions { get; set; } = new();
    public DefensiveSourceValue ForcedFumbles { get; set; } = new();
    public DefensiveSourceValue FumbleRecoveries { get; set; } = new();
    public DefensiveSourceValue BlockedExtraPoints { get; set; } = new();
    public DefensiveSourceValue BlockedKicks { get; set; } = new();
}

public sealed class StagedDefensiveGame
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int? Season { get; set; }
    public int? Week { get; set; }
    public string Opponent { get; set; } = "";
    public string SiteIndicator { get; set; } = "";
    public string WorksheetName { get; set; } = "";
    public string IdentityText { get; set; } = "";
    public ReportReviewState State { get; set; } = ReportReviewState.PendingReview;
    public DateTime? ReviewedUtc { get; set; }
    public DateTime? AcceptedUtc { get; set; }
    public string ReviewNote { get; set; } = "";
    public List<DefensiveStatLine> Players { get; set; } = [];
    public List<InformationValidationIssue> Issues { get; set; } = [];
    [JsonIgnore] public bool HasBlockingIssues => Issues.Any(x => x.Severity == InformationIssueSeverity.Blocking);
    [JsonIgnore] public bool HasAdvisories => Issues.Any(x => x.Severity == InformationIssueSeverity.Advisory);
}

public sealed class StagedDefensiveSeasonTotals
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int? Season { get; set; }
    public string WorksheetName { get; set; } = "";
    public string IdentityText { get; set; } = "";
    public ReportReviewState State { get; set; } = ReportReviewState.PendingReview;
    public DateTime? ReviewedUtc { get; set; }
    public DateTime? AcceptedUtc { get; set; }
    public string ReviewNote { get; set; } = "";
    public List<DefensiveStatLine> Players { get; set; } = [];
    public List<InformationValidationIssue> Issues { get; set; } = [];
    [JsonIgnore] public bool HasBlockingIssues => Issues.Any(x => x.Severity == InformationIssueSeverity.Blocking);
    [JsonIgnore] public bool HasAdvisories => Issues.Any(x => x.Severity == InformationIssueSeverity.Advisory);
}

public sealed class StagedDefensiveWorkbook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid ExpectedDocumentId { get; set; }
    public Guid SourceFamilyId { get; set; }
    public Guid ImportRecordId { get; set; }
    public DateTime ParsedUtc { get; set; } = DateTime.UtcNow;
    public List<StagedDefensiveGame> Games { get; set; } = [];
    public StagedDefensiveSeasonTotals? SeasonTotals { get; set; }
    public List<InformationValidationIssue> Issues { get; set; } = [];
}

public sealed class AcceptedDefensiveGame
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid StagedWorkbookId { get; set; }
    public Guid StagedSectionId { get; set; }
    public Guid ExpectedDocumentId { get; set; }
    public Guid SourceFamilyId { get; set; }
    public Guid ImportRecordId { get; set; }
    public int Season { get; set; }
    public int Week { get; set; }
    public string Opponent { get; set; } = "";
    public string SiteIndicator { get; set; } = "";
    public List<DefensiveStatLine> Players { get; set; } = [];
    public List<InformationValidationIssue> AcceptedIssues { get; set; } = [];
    public string AcceptanceNote { get; set; } = "";
    public DateTime AcceptedUtc { get; set; }
    public bool IsCurrentAuthority { get; set; } = true;
}

public sealed class AcceptedDefensiveSeasonTotals
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid StagedWorkbookId { get; set; }
    public Guid StagedSectionId { get; set; }
    public Guid ExpectedDocumentId { get; set; }
    public Guid SourceFamilyId { get; set; }
    public Guid ImportRecordId { get; set; }
    public int Season { get; set; }
    public List<DefensiveStatLine> Players { get; set; } = [];
    public List<InformationValidationIssue> AcceptedIssues { get; set; } = [];
    public string AcceptanceNote { get; set; } = "";
    public DateTime AcceptedUtc { get; set; }
    public bool IsCurrentAuthority { get; set; } = true;
}

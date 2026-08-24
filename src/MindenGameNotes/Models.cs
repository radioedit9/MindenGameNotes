using System.Text.Json.Serialization;

namespace MindenGameNotes;

public sealed class BuilderWorkspace
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid? ActiveProjectId { get; set; }
    public List<SourceFamilyConfiguration> SourceFamilies { get; set; } = [];
    public List<GameNotesProject> Projects { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    [JsonIgnore] public GameNotesProject? ActiveProject => Projects.FirstOrDefault(x => x.Id == ActiveProjectId) ?? Projects.FirstOrDefault();

    public void Normalize()
    {
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException($"Unsupported workspace schema version {SchemaVersion}; expected {CurrentSchemaVersion}.");
        SourceFamilies ??= [];
        Projects ??= [];
        foreach (var family in SourceFamilies) if (family.Id == Guid.Empty) family.Id = Guid.NewGuid();
        var familyIds = SourceFamilies.Select(x => x.Id).ToHashSet();
        foreach (var project in Projects) project.Normalize(familyIds);
        if (ActiveProjectId is null || Projects.All(x => x.Id != ActiveProjectId)) ActiveProjectId = Projects.FirstOrDefault()?.Id;
    }
}

public sealed class GameNotesProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int? Season { get; set; }
    public int? Week { get; set; }
    public string Opponent { get; set; } = "";
    public DateTime? GameDate { get; set; }
    public TimeOnly? KickoffTime { get; set; }
    public string Venue { get; set; } = "";
    public string School { get; set; } = "Minden High School";
    public string TeamName { get; set; } = "Crimson Tide";
    public string Headline { get; set; } = "GAME NOTES";
    public string CoachQuote { get; set; } = "Add this week's coach quote.";
    public string Storyline { get; set; } = "Add the weekly matchup storyline and editorial notes here.";
    public string MediaContact { get; set; } = "Minden High School Athletics";
    public PageOneData PageOne { get; set; } = new();
    public List<PlayerStat> Players { get; set; } = [];
    public List<GameResult> Schedule { get; set; } = [];
    public List<ExpectedSourceDocument> ExpectedDocuments { get; set; } = [];
    public List<ImportRecord> Imports { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] private HashSet<Guid> sourceFamilyIds = [];
    [JsonIgnore] public bool IsIdentityComplete => Season is >= 1900 and <= 2200 && Week is > 0 && !string.IsNullOrWhiteSpace(Opponent) && GameDate is not null && KickoffTime is not null && !string.IsNullOrWhiteSpace(Venue);
    [JsonIgnore] public IReadOnlyList<string> ReadinessIssues => BuildReadinessIssues();
    [JsonIgnore] public bool IsReady => ReadinessIssues.Count == 0;
    [JsonIgnore] public string DisplayName => Season is null && Week is null ? "New weekly project" : $"{Season?.ToString() ?? "Unknown season"} • Week {Week?.ToString() ?? "?"} • {(string.IsNullOrWhiteSpace(Opponent) ? "Opponent TBD" : Opponent)}";
    [JsonIgnore] public string KickoffDisplay => KickoffTime?.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture) ?? "TBD";
    [JsonIgnore]
    public string KickoffText
    {
        get => KickoffTime?.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        set => KickoffTime = TimeOnly.TryParse(value, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || TimeOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out parsed) ? parsed : null;
    }

    internal void Normalize(HashSet<Guid> knownSourceFamilyIds)
    {
        if (Id == Guid.Empty) Id = Guid.NewGuid();
        PageOne ??= new(); Players ??= []; Schedule ??= []; ExpectedDocuments ??= []; Imports ??= [];
        sourceFamilyIds = knownSourceFamilyIds;
        foreach (var document in ExpectedDocuments) if (document.Id == Guid.Empty) document.Id = Guid.NewGuid();
        foreach (var import in Imports) { if (import.Id == Guid.Empty) import.Id = Guid.NewGuid(); if (import.ProjectId == Guid.Empty) import.ProjectId = Id; }
    }

    private IReadOnlyList<string> BuildReadinessIssues()
    {
        var issues = new List<string>();
        if (Season is not (>= 1900 and <= 2200)) issues.Add("Season is unknown or invalid.");
        if (Week is not > 0) issues.Add("Week is unknown or invalid.");
        if (string.IsNullOrWhiteSpace(Opponent)) issues.Add("Opponent is required.");
        if (GameDate is null) issues.Add("Game date is required.");
        if (KickoffTime is null) issues.Add("Kickoff time is required.");
        if (string.IsNullOrWhiteSpace(Venue)) issues.Add("Venue is required.");
        foreach (var document in ExpectedDocuments.Where(x => x.IsApplicable))
        {
            if (!sourceFamilyIds.Contains(document.SourceFamilyId)) issues.Add($"{document.Name}: source family is missing.");
            if (!document.HasHealthySource) issues.Add($"{document.Name}: source status is {document.Status}.");
            if (document.Verification != DocumentVerificationState.Verified) issues.Add($"{document.Name}: document is unverified.");
        }
        return issues;
    }
}

public sealed class SourceFamilyConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string RootPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceDocumentStatus { Present, Missing, Current, Stale, Pending, NotApplicable }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentVerificationState { Unverified, Verified }

public sealed class ExpectedSourceDocument : IJsonOnDeserializing, IJsonOnDeserialized
{
    private Guid sourceFamilyId;
    private string expectedLocator = "";
    private bool isDeserializing;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceFamilyId
    {
        get => sourceFamilyId;
        set { if (sourceFamilyId == value) return; sourceFamilyId = value; if (!isDeserializing) InvalidateSourceObservation(); }
    }
    public string Name { get; set; } = "";
    public bool IsApplicable { get; set; } = true;
    public bool IsPending { get; set; }
    public string ExpectedLocator
    {
        get => expectedLocator;
        set { value ??= ""; if (expectedLocator == value) return; expectedLocator = value; if (!isDeserializing) InvalidateSourceObservation(); }
    }
    public string? ResolvedPath { get; set; }
    public DateTime? ExpectedAsOfUtc { get; set; }
    public DateTime? SourceModifiedUtc { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public SourceDocumentStatus Status { get; set; } = SourceDocumentStatus.Missing;
    public DocumentVerificationState Verification { get; set; } = DocumentVerificationState.Unverified;
    public DateTime? VerifiedUtc { get; set; }
    public string VerificationNote { get; set; } = "";
    [JsonIgnore] public bool HasHealthySource => Status is SourceDocumentStatus.Present or SourceDocumentStatus.Current;

    public string? ResolvePath(SourceFamilyConfiguration? family)
    {
        if (!string.IsNullOrWhiteSpace(ResolvedPath)) return Path.GetFullPath(ResolvedPath);
        if (family is null || string.IsNullOrWhiteSpace(family.RootPath) || string.IsNullOrWhiteSpace(ExpectedLocator)) return null;
        return Path.GetFullPath(Path.Combine(family.RootPath, ExpectedLocator));
    }

    public void RefreshStatus(SourceFamilyConfiguration? family, DateTime? observedUtc = null)
    {
        LastCheckedUtc = DateTime.UtcNow;
        if (!IsApplicable) { Status = SourceDocumentStatus.NotApplicable; return; }
        if (IsPending) { Status = SourceDocumentStatus.Pending; return; }
        var path = ResolvePath(family);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { Status = SourceDocumentStatus.Missing; SourceModifiedUtc = null; return; }
        ResolvedPath = path;
        SourceModifiedUtc = observedUtc ?? File.GetLastWriteTimeUtc(path);
        if (ExpectedAsOfUtc is null) { Status = SourceDocumentStatus.Present; return; }
        Status = SourceModifiedUtc < ExpectedAsOfUtc ? SourceDocumentStatus.Stale : SourceDocumentStatus.Current;
    }

    public void SetVerified(bool verified)
    {
        Verification = verified ? DocumentVerificationState.Verified : DocumentVerificationState.Unverified;
        VerifiedUtc = verified ? DateTime.UtcNow : null;
    }

    void IJsonOnDeserializing.OnDeserializing() => isDeserializing = true;
    void IJsonOnDeserialized.OnDeserialized() => isDeserializing = false;

    private void InvalidateSourceObservation()
    {
        ResolvedPath = null;
        SourceModifiedUtc = null;
        LastCheckedUtc = null;
        Status = IsApplicable ? SourceDocumentStatus.Missing : SourceDocumentStatus.NotApplicable;
        Verification = DocumentVerificationState.Unverified;
        VerifiedUtc = null;
    }
}

public sealed class PageOneData
{
    public string MindenRecord { get; set; } = "0-0";
    public string OpponentRecord { get; set; } = "0-0";
    public string Weather { get; set; } = "[GAME-WEEK INPUT]";
    public string Radio { get; set; } = "KBEF 104.5 FM";
    public string Internet { get; set; } = "KBEF.com";
    public string Enrollment { get; set; } = "744 [VERIFY]";
    public bool EnrollmentVerified { get; set; }
    public string CoachMindenRecord { get; set; } = "77-66 (.538)";
    public string OpponentLocation { get; set; } = "Springhill, LA";
    public string OpponentClassDistrict { get; set; } = "3A / 1-3A";
    public string OpponentCoach { get; set; } = "Christopher Wilson";
    public string OpponentCoachTenure { get; set; } = "3rd Season";
    public string OpponentPriorRecord { get; set; } = "4-7";
    public string OpponentPostseason { get; set; } = "Bi-District";
    public string OpponentLastMeeting { get; set; } = "North Webster 21, Minden 20|November 7, 2025";
    public List<string> SeriesHistory { get; set; } = ["All-Time:|Minden leads 8-3", "At North Webster:|Minden 5-0", "At Minden:|3-3", "Under Coach Heard:|8-3", "Current Series Streak:|North Webster W1", "First Meeting:|2015 – Minden 27-20", "Last Meeting:|2025 – North Webster 21-20"];
    public List<string> WinTonightWould { get; set; } = ["Give Minden its 570th all-time victory.", "Give Spencer Heard his 78th victory at Minden.", "Improve Minden to 9-3 against North Webster.", "Improve Minden to 6-0 at North Webster.", "Open the 2026 season 1-0."];
    public List<string> StatsOfWeek { get; set; } = ["19,994|documented Minden points entering the 2026 season.", "Minden’s sixth point of the season will be No. 20,000."];
    public string LookingBackTitle { get; set; } = "WOSSMAN 35, MINDEN 14";
    public string LookingBackSubhead { get; set; } = "NOVEMBER 14, 2025 • BI-DISTRICT";
    public List<string> LookingBackScores { get; set; } = ["WOSSMAN|14|14|0|7|35", "MINDEN|7|7|0|0|14"];
    public string LookingBackSummary { get; set; } = "Minden tied the game at 14-14 in the second quarter, but Wossman scored twice before halftime and added the only second-half touchdown.";
    public List<string> MindenLeaders { get; set; } = ["RUSHING|Jardon Carey|11-46", "|Kaiden Shine|4-29", "PASSING|Hudson Brown|9-22-1, 84, TD", "|Jaden Johnson|4-7-1, 29", "RECEIVING|Jaden Johnson|6-54, TD", "|Kameron Harris|3-26", "DEFENSE|Kennedy Burns|INT"];
    public string PriorSeasonRecord { get; set; } = "5-6";
    public string PriorSeasonPostseason { get; set; } = "Bi-District Qualifier";
    public List<string> ByTheNumbers { get; set; } = ["316.5|POINTS PER GAME", "347.9|YARDS PER GAME", "174.7|RUSHING YARDS PER GAME", "173.2|PASSING YARDS PER GAME", "21|TURNOVERS FORCED", "18|TURNOVERS COMMITTED", "31:24|TIME OF POSSESSION (AVG)"];
    public string LargestMindenWin { get; set; } = "26-0|(2016)";
    public string LargestOpponentWin { get; set; } = "28-21|(2023)";
}

public sealed class PlayerStat
{
    public string Name { get; set; } = ""; public string Number { get; set; } = ""; public string Position { get; set; } = "";
    public int Games { get; set; } public int PassingYards { get; set; } public int RushingYards { get; set; } public int ReceivingYards { get; set; } public int Tackles { get; set; } public int Touchdowns { get; set; }
    public bool Verified { get; set; }
    [JsonIgnore] public string TotalYards => (PassingYards + RushingYards + ReceivingYards).ToString("N0");
}

public sealed class GameResult
{
    public DateTime Date { get; set; } public string Opponent { get; set; } = ""; public string Site { get; set; } = ""; public int? MindenScore { get; set; } public int? OpponentScore { get; set; } public bool Verified { get; set; }
    [JsonIgnore] public string Result => MindenScore is null ? "—" : $"{(MindenScore >= OpponentScore ? "W" : "L")} {MindenScore}-{OpponentScore}";
}

public sealed class ImportRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid SourceFamilyId { get; set; }
    public Guid ExpectedDocumentId { get; set; }
    public string FileName { get; set; } = "";
    public string SourceLocator { get; set; } = "";
    public DateTime? SourceModifiedUtc { get; set; }
    public int? ApplicableSeason { get; set; }
    public int? ApplicableWeek { get; set; }
    public DateTime ImportedUtc { get; set; }
    public string Kind { get; set; } = "";
    public int RowCount { get; set; }
}

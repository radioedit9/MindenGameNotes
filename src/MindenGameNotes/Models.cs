using System.Text.Json.Serialization;

namespace MindenGameNotes;

public sealed class GameNotesProject
{
    public string School { get; set; } = "Minden High School";
    public string TeamName { get; set; } = "Crimson Tide";
    public string Opponent { get; set; } = "Opponent";
    public DateTime GameDate { get; set; } = DateTime.Today;
    public string Venue { get; set; } = "North Webster High School\nBaucum-Farrar Stadium — Springhill, LA";
    public string Headline { get; set; } = "GAME NOTES";
    public string CoachQuote { get; set; } = "Add this week's coach quote.";
    public string Storyline { get; set; } = "Add the weekly matchup storyline and editorial notes here.";
    public string MediaContact { get; set; } = "Minden High School Athletics";
    public PageOneData PageOne { get; set; } = new();
    public List<PlayerStat> Players { get; set; } = [];
    public List<GameResult> Schedule { get; set; } = [];
    public List<ImportRecord> Imports { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PageOneData
{
    public int Week { get; set; } = 1;
    public string MindenRecord { get; set; } = "0-0";
    public string OpponentTeam { get; set; } = "NORTH WEBSTER KNIGHTS";
    public string OpponentRecord { get; set; } = "0-0";
    public string Kickoff { get; set; } = "7:00 p.m.";
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
    public string Name { get; set; } = "";
    public string Number { get; set; } = "";
    public string Position { get; set; } = "";
    public int Games { get; set; }
    public int PassingYards { get; set; }
    public int RushingYards { get; set; }
    public int ReceivingYards { get; set; }
    public int Tackles { get; set; }
    public int Touchdowns { get; set; }
    public bool Verified { get; set; }
    [JsonIgnore] public string TotalYards => (PassingYards + RushingYards + ReceivingYards).ToString("N0");
}

public sealed class GameResult
{
    public DateTime Date { get; set; }
    public string Opponent { get; set; } = "";
    public string Site { get; set; } = "";
    public int? MindenScore { get; set; }
    public int? OpponentScore { get; set; }
    public bool Verified { get; set; }
    [JsonIgnore] public string Result => MindenScore is null ? "—" : $"{(MindenScore >= OpponentScore ? "W" : "L")} {MindenScore}-{OpponentScore}";
}

public sealed class ImportRecord
{
    public string FileName { get; set; } = "";
    public DateTime ImportedUtc { get; set; }
    public string Kind { get; set; } = "";
    public int RowCount { get; set; }
    public string Status { get; set; } = "Needs review";
}

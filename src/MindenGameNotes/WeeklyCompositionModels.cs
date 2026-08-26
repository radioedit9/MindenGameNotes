namespace MindenGameNotes;

public enum CompositionFlexibility { Rigid, Constrained, Elastic }
public enum CompositionDensity { Open, Standard, Dense }
public enum CompositionAlignment { Left, Center, Right }
public enum CompositionSeverity { Advisory, Blocking }
public enum CompositionDiagnosticCode { ContentOverflow, MinimumTypographyExceeded, RosterNameCannotFit, CellOverflow, RequiredRegionCannotFit, UnsupportedCompositionState }
public enum TypographyRole { PublicationMasthead, PageTitle, SectionHeading, Feature, Body, Data, DataLabel, Footer, Roster, RosterName }
public enum SpacingRole { Tight, Normal, Relaxed }
public enum RuleRole { Light, Normal, Strong }

public sealed record CompositionRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width; public double Bottom => Y + Height;
    public bool Contains(CompositionRect other) => other.X >= X && other.Y >= Y && other.Right <= Right + .001 && other.Bottom <= Bottom + .001;
}
public sealed record TypographyRange(TypographyRole Role, string Family, double PreferredPoints, double MinimumPoints, double MaximumPoints, bool Bold = false);
public sealed record PublicationDesignContract(double PageWidth, double PageHeight, double Margin, double HeaderHeight, double FooterHeight, IReadOnlyList<TypographyRange> Typography, IReadOnlyDictionary<SpacingRole, double> Spacing, IReadOnlyDictionary<RuleRole, double> Rules, double MinimumRosterNamePoints, double TextLeading = 1.2, double LocalFitStepPoints = .25);
public sealed record PublicationHeader(string Title, string School, string TeamName, CompositionRect Bounds, TypographyRole Typography, CompositionFlexibility Flexibility);
public sealed record PublicationFooter(int PageNumber, string Label, CompositionRect Bounds, TypographyRole Typography, CompositionFlexibility Flexibility);
public sealed record PageShell(PublicationHeader Header, PublicationFooter Footer, CompositionRect BodyBounds);
public sealed record CompositionRegion(string Key, string Heading, CompositionRect Bounds, CompositionFlexibility Flexibility, CompositionDensity Density, TypographyRole Typography, CompositionAlignment Alignment = CompositionAlignment.Left);
public sealed record CompositionDiagnostic(int PageNumber, string Region, CompositionSeverity Severity, CompositionDiagnosticCode Code, string Message);
public sealed record SectionHeading(string Text, CompositionRect Bounds, TypographyRole Typography, CompositionFlexibility Flexibility);
public sealed record NarrativeBlock(string Key, IReadOnlyList<SourcedText> Content, CompositionRegion Region);
public sealed record FactGrid(string Key, OpponentQuickFacts Content, CompositionRegion Region);
public sealed record PublicationTable<T>(string Key, string Title, T? Content, CompositionRegion Region);
public sealed record StatisticalTable(string Key, SourceStatTable Content, CompositionRegion Region);
public sealed record ComparativeStatTable(string Key, IReadOnlyList<string> Columns, IReadOnlyList<ReportedTeamStatisticRow> Rows, CompositionRegion Region);
public sealed record HistoricalReferenceBlock(string Key, BaselineSection Content, CompositionRegion Region);
public sealed record EditorialFeature(int SourceIndex, NerdNoteItem Content, CompositionRegion Region);
public sealed record CompositionVisibleRow(IReadOnlyList<string> Cells);
public sealed record Page2ScheduleRow(IReadOnlyList<string> Cells, bool IsDistrictGame);
public sealed record Page2ScheduleComposition(string Key, string Title, bool HasAuthority, IReadOnlyList<string> Columns, IReadOnlyList<Page2ScheduleRow> Rows, IReadOnlyList<string> Details, CompositionRegion Region);
public sealed record Page2RankingComposition(string Key, string Title, bool HasAuthority, IReadOnlyList<string> Columns, IReadOnlyList<CompositionVisibleRow> Rows, IReadOnlyList<string> Details, CompositionRegion Region);
public enum Page3IndividualStatisticsRole { Unknown, Offense, SpecialTeams }
public sealed record Page3IndividualStatisticsComposition(string Key, string Heading, Page3IndividualStatisticsRole Role, IReadOnlyList<string> Columns, IReadOnlyList<CompositionVisibleRow> Rows, CompositionRegion Region);
public sealed record Page3DefenseComposition(string Key, string Heading, bool HasAuthority, bool HasConflict, IReadOnlyList<string> Columns, IReadOnlyList<CompositionVisibleRow> Rows, CompositionRegion Region);
public sealed record Page3PlayerOfGameComposition(string Key, string Heading, bool HasAuthority, IReadOnlyList<string> Columns, IReadOnlyList<CompositionVisibleRow> Rows, CompositionRegion Region);
public sealed record Page3RosterReferenceComposition(string Key, string Heading, bool HasAuthority, IReadOnlyList<string> Columns, IReadOnlyList<CompositionVisibleRow> Rows, CompositionRegion Region);
public sealed record CompositionPageContext(int PageNumber, string Purpose, PageProductionState ProductionState, bool PublicationReady, PageShell Shell, IReadOnlyList<CompositionRegion> Regions, IReadOnlyList<CompositionDiagnostic> Diagnostics, IReadOnlyList<string> ChangedRequirementKeys)
{
    public bool IsRenderReady => PublicationReady && Diagnostics.All(x => x.Severity != CompositionSeverity.Blocking);
}

public interface IGameNotesPageComposition { CompositionPageContext Context { get; } }
public sealed record Page1Traceability(AuthorityReference ProjectIdentity, AuthorityReference? LookingBack, AuthorityReference? WeeklyFacts, IReadOnlyList<PublicationEvidenceReference> WeeklyFactsEvidence);
public sealed record Page1GameIdentityComposition(string Opponent, string GameDate, string KickoffTime, string Venue, CompositionRegion Region);
public sealed record Page1PeriodScoreComposition(int Order, string Label, int MindenPoints, int OpponentPoints);
public sealed record Page1ScoringPlayComposition(int Period, string Clock, string Team, string Description, string ScoreAfterPlay);
public sealed record Page1LookingBackComposition(string PreviousOpponent, string PreviousGameDate, string PreviousGameSite, string FinalScoreResult, IReadOnlyList<Page1PeriodScoreComposition> PeriodScores, IReadOnlyList<Page1ScoringPlayComposition> ScoringPlays, CompositionRegion Region);
public sealed record Page1InformationRowComposition(string Label, IReadOnlyList<string> Values, Guid EvidenceId);
public sealed record Page1OpponentInformationComposition(IReadOnlyList<Page1InformationRowComposition> Rows, CompositionRegion Region);
public sealed record Page1QuickFactsComposition(string MindenRecord, string OpponentRecord, string Temperature, string Sky, string Wind, IReadOnlyList<string> ByTheNumbers, string PriorSeasonSummary, CompositionRegion Region);
public sealed record Page1StatOfWeekComposition(PublicationStatOfWeekState State, string Headline, string DisplayText, IReadOnlyList<string> SupportingFacts, PublicationEvidenceReference? EditorialEvidence, CompositionRegion Region);
public sealed record Page1WeeklyReferencesComposition(IReadOnlyList<string> SeriesHistory, IReadOnlyList<string> WinImplications, string SeriesExtremes, string Storyline, CompositionRegion Region);
public sealed record Page1Composition(CompositionPageContext Context, Page1Traceability Traceability, Page1GameIdentityComposition GameIdentity, Page1LookingBackComposition? LookingBack, Page1OpponentInformationComposition OpponentInformation, Page1QuickFactsComposition MindenQuickFacts, Page1StatOfWeekComposition StatOfWeek, Page1WeeklyReferencesComposition WeeklyReferences) : IGameNotesPageComposition;
public sealed record Page2Composition(Page2PublicationSupply Source, CompositionPageContext Context, IReadOnlyList<PublicationTable<SchedulePayload>> Schedules, IReadOnlyList<PublicationTable<RankingSnapshotPayload>> Rankings, IReadOnlyList<Page2ScheduleComposition> VisibleSchedules, IReadOnlyList<Page2RankingComposition> VisibleRankings) : IGameNotesPageComposition;
public sealed record Page3Composition(Page3PublicationSupply Source, CompositionPageContext Context, IReadOnlyList<StatisticalTable> OffenseSpecialTeams, CompositionRegion Defense, CompositionRegion PlayerOfGame, CompositionRegion RosterIdentity, bool HasIndividualStatisticsAuthority, string ProductionLabel, int? StatisticalSeason, IReadOnlyList<Page3IndividualStatisticsComposition> VisibleIndividualStatistics, Page3DefenseComposition VisibleDefense, Page3PlayerOfGameComposition VisiblePlayerOfGame, Page3RosterReferenceComposition VisibleRosterReference) : IGameNotesPageComposition;
public sealed record Page4Composition(Page4PublicationSupply Source, CompositionPageContext Context, IReadOnlyList<HistoricalReferenceBlock> HistoricalBlocks) : IGameNotesPageComposition;
public sealed record Page5Composition(Page5PublicationSupply Source, CompositionPageContext Context, IReadOnlyList<HistoricalReferenceBlock> HistoricalBlocks) : IGameNotesPageComposition;
public sealed record Page6Composition(Page6PublicationSupply Source, CompositionPageContext Context, ComparativeStatTable TeamStatistics) : IGameNotesPageComposition;
public sealed record Page7Composition(Page7PublicationSupply Source, CompositionPageContext Context, string PermanentTitle, string PermanentDescriptor, IReadOnlyList<EditorialFeature> Features) : IGameNotesPageComposition;

public sealed record RosterCell(string Column, string Value, CompositionRect Bounds, double FontPoints, string FontFamily, double Leading, double HorizontalInset, double VerticalInset, TypographyRole Typography, CompositionAlignment Alignment)
{
    public double ConservativeTextWidth => CompositionTextMetrics.MeasureWidth(Value, FontPoints, FontFamily);
    public double ConservativeLineHeight => CompositionTextMetrics.LineHeight(FontPoints, Leading);
    public bool IsContained => ConservativeTextWidth <= Bounds.Width - HorizontalInset + .001 && ConservativeLineHeight <= Bounds.Height - VerticalInset + .001;
}

/// <summary>Repository-owned composition upper bounds for the governed Impact and Arial Narrow
/// families. Values are em-square advance ceilings, rounded upward from the fonts' documented
/// design-unit classes; the unknown-glyph ceiling is 1.50 em. They intentionally exceed measured
/// advances and prove composition capacity without claiming final-renderer pixel identity.</summary>
public static class CompositionTextMetrics
{
    public const string MetricBasis = "Conservative em-square advance ceilings for governed Impact and Arial Narrow; unknown glyphs use 1.50 em.";
    public static double MeasureWidth(string value, double points, string family)
    {
        if (family is not ("Arial Narrow" or "Impact")) throw new InvalidOperationException($"No governed composition metrics exist for '{family}'.");
        return value.Sum(GlyphEm) * points;
    }
    public static double LineHeight(double points, double leading) => points * leading;
    public static int WrappedLines(string value, double width, double points, string family)
    {
        var explicitLines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'); var total = 0;
        foreach (var explicitLine in explicitLines)
        {
            if (explicitLine.Length == 0) { total++; continue; }
            var words = explicitLine.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (words.Length == 0) { total++; continue; }
            var wrapped = 1; var used = 0d;
            foreach (var word in words) { var wordWidth = MeasureWidth(word, points, family); if (wordWidth > width + .001) return int.MaxValue; var gap = used == 0 ? 0 : MeasureWidth(" ", points, family); if (used + gap + wordWidth > width + .001) { wrapped++; used = wordWidth; } else used += gap + wordWidth; }
            total += wrapped;
        }
        return Math.Max(1, total);
    }
    private static double GlyphEm(char c) => c switch
    {
        'W' or 'M' or 'w' or 'm' or '@' => 1.25,
        'I' or 'i' or 'l' or '1' or '|' => .55,
        ' ' => .50,
        >= 'A' and <= 'Z' => .95,
        >= 'a' and <= 'z' => .90,
        >= '0' and <= '9' => .90,
        '-' or '.' or ',' or '\'' or '’' or ':' or ';' or '!' or '?' or '/' => .65,
        _ => 1.50
    };
}
public sealed record RosterRow(int SourceIndex, RosterEntry Source, string PublishedName, CompositionRect Bounds, IReadOnlyList<RosterCell> Cells);
public sealed record RosterBank(int BankNumber, CompositionRect Bounds, IReadOnlyList<RosterRow> Rows);
public sealed record RosterPageComposition(CompositionPageContext Context, AcceptedPublicationSection<RosterPayload>? Source, IReadOnlyList<string> PublishedColumns, IReadOnlyList<RosterBank> Banks, double CommonFontPoints) : IGameNotesPageComposition;
public sealed record Page8Composition(RosterPageComposition Roster) : IGameNotesPageComposition { public CompositionPageContext Context => Roster.Context; }
public sealed record Page9Composition(RosterPageComposition Roster) : IGameNotesPageComposition { public CompositionPageContext Context => Roster.Context; }
public sealed record Page10Composition(CompositionPageContext Context, Page10PublicationSupply Source, IReadOnlyList<CompositionRegion> BodyPrimitives) : IGameNotesPageComposition;

public sealed record PublicationIdentity(Guid ProjectId, int Season, int Week, string School, string TeamName, string Opponent);
public sealed record WeeklyGameNotesComposition(PublicationIdentity PublicationIdentity, PublicationDesignContract Design, Page1Composition Page1, Page2Composition Page2, Page3Composition Page3, Page4Composition Page4, Page5Composition Page5, Page6Composition Page6, Page7Composition Page7, Page8Composition Page8, Page9Composition Page9, Page10Composition Page10, IReadOnlyList<IGameNotesPageComposition> Pages, IReadOnlyList<CompositionDiagnostic> CompositionDiagnostics, string SemanticIdentity)
{
    public bool IsRenderReady => Pages.Count == 10 && Pages.All(x => x.Context.IsRenderReady) && CompositionDiagnostics.All(x => x.Severity != CompositionSeverity.Blocking);
}

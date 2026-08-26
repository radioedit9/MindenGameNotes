using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MindenGameNotes;

public static class WeeklyResolvedLayoutBuilder
{
    private const double Tolerance = .001;
    private static readonly double[] DefenseWidths = [226, 22, 31, 27, 33, 28, 33, 28, 31, 28, 24, 24, 31, 24];

    public static ResolvedWeeklyGameNotesLayout Build(WeeklyGameNotesComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        GovernedTypographyMetrics metrics;
        try { metrics = GovernedTypographyMetrics.Create(composition.Design, RequiredTypographyRoles(composition)); }
        catch (Exception ex)
        {
            var code = ex is GovernedTypographyPreflightException preflight ? preflight.DiagnosticCode : ResolvedLayoutDiagnosticCode.RequiredFontUnavailable; var diagnostic = new ResolvedLayoutDiagnostic(0, "Fonts", ResolvedLayoutSeverity.Blocking, code, ex.Message);
            var blockedPages = composition.Pages.OrderBy(x => x.Context.PageNumber).Select(x => new ResolvedPageLayout(x.Context.PageNumber, new(0, 0, composition.Design.PageWidth, composition.Design.PageHeight), x.Context.Shell.BodyBounds, [], new[] { diagnostic with { PageNumber = x.Context.PageNumber } })).ToList(); var failed = new ResolvedWeeklyGameNotesLayout(composition.SemanticIdentity, GovernedTypographyMetrics.AlgorithmVersion, [], blockedPages, blockedPages.SelectMany(x => x.Diagnostics).ToList(), ""); return failed with { SemanticIdentity = Identity(failed) };
        }

        var pages = composition.Pages.OrderBy(x => x.Context.PageNumber).Select(x => ResolvePage(composition, x, metrics)).ToList();
        var diagnostics = pages.SelectMany(x => x.Diagnostics).ToList();
        ValidateDocument(composition, pages, diagnostics);
        var provisional = new ResolvedWeeklyGameNotesLayout(composition.SemanticIdentity, GovernedTypographyMetrics.AlgorithmVersion, metrics.Identities, pages.AsReadOnly(), diagnostics.AsReadOnly(), "");
        return provisional with { SemanticIdentity = Identity(provisional) };
    }

    private static IReadOnlySet<TypographyRole> RequiredTypographyRoles(WeeklyGameNotesComposition composition)
    {
        var roles = new HashSet<TypographyRole> { TypographyRole.PublicationMasthead, TypographyRole.Body, TypographyRole.Footer, TypographyRole.SectionHeading, TypographyRole.Data, TypographyRole.Feature };
        foreach (var roster in new[] { composition.Page8.Roster, composition.Page9.Roster })
            foreach (var cell in roster.Banks.SelectMany(x => x.Rows).SelectMany(x => x.Cells)) roles.Add(cell.Typography);
        return roles;
    }

    private static ResolvedPageLayout ResolvePage(WeeklyGameNotesComposition document, IGameNotesPageComposition page, GovernedTypographyMetrics metrics)
    {
        var p = new PageResolver(page.Context.PageNumber, document.Design, metrics, page.Context.Shell);
        p.Shell();
        if (!page.Context.IsRenderReady) p.Diagnostic("Composition", ResolvedLayoutDiagnosticCode.ParentCompositionNotRenderReady, "The accepted WP 7 page composition is not render-ready.");
        switch (page)
        {
            case Page1Composition x: Page1(p, x); break;
            case Page2Composition x: Page2(p, x); break;
            case Page3Composition x: Page3(p, x); break;
            case Page4Composition x: Historical(p, x.HistoricalBlocks); break;
            case Page5Composition x: Historical(p, x.HistoricalBlocks); break;
            case Page6Composition x: p.Table(x.TeamStatistics.Key, x.TeamStatistics.Region.Heading, x.TeamStatistics.Region, x.TeamStatistics.Columns, x.TeamStatistics.Rows.Select(r => new[] { r.Label, r.Minden, r.Opponent }).ToList(), [.60, .20, .20]); break;
            case Page7Composition x: Page7(p, x); break;
            case Page8Composition x: Roster(p, x.Roster); break;
            case Page9Composition x: Roster(p, x.Roster); break;
            case Page10Composition: break;
            default: p.Diagnostic("Page", ResolvedLayoutDiagnosticCode.UnsupportedSemanticRole, $"Unsupported page composition '{page.GetType().Name}'."); break;
        }
        return p.Finish();
    }

    private static void Page1(PageResolver p, Page1Composition x)
    {
        p.Heading(x.GameIdentity.Region, "GAME INFORMATION");
        p.Grid("P1.GameIdentity", p.BelowHeading(x.GameIdentity.Region),
            ["OPPONENT", "DATE", "KICKOFF", "VENUE"],
            [[x.GameIdentity.Opponent, x.GameIdentity.GameDate, x.GameIdentity.KickoffTime, x.GameIdentity.Venue]], [.34, .22, .16, .28], verticalRules: false);
        if (x.LookingBack is not null)
        {
            var r = x.LookingBack.Region; p.Heading(r, "LOOKING BACK"); var body = p.BelowHeading(r);
            var meta = $"{x.LookingBack.PreviousOpponent} | {x.LookingBack.PreviousGameDate} | {x.LookingBack.PreviousGameSite} | {x.LookingBack.FinalScoreResult}";
            p.Text("P1.LookingBack.Metadata", meta, new(body.X, body.Y, body.Width, 14), TypographyRole.Body);
            var scoreY = body.Y + 15; var scoreRows = x.LookingBack.PeriodScores.Select(s => new[] { s.Label, s.MindenPoints.ToString(CultureInfo.InvariantCulture), s.OpponentPoints.ToString(CultureInfo.InvariantCulture) }).ToList();
            var scoreH = Math.Min(64, Math.Max(18, (scoreRows.Count + 1) * 12)); p.Grid("P1.LookingBack.Periods", new(body.X, scoreY, body.Width, scoreH), ["Period", "Minden", "Opponent"], scoreRows, [.40, .30, .30]);
            var playY = scoreY + scoreH + 3; var plays = x.LookingBack.ScoringPlays.Select(s => new[] { s.Period.ToString(CultureInfo.InvariantCulture), s.Clock, s.Team, s.Description, s.ScoreAfterPlay }).ToList();
            p.Grid("P1.LookingBack.Plays", new(body.X, playY, body.Width, Math.Max(0, body.Bottom - playY)), ["Q", "Clock", "Team", "Play", "Score"], plays, [.08, .12, .14, .48, .18]);
        }
        Rows(p, "P1.OpponentInformation", x.OpponentInformation.Region, "OPPONENT INFORMATION", x.OpponentInformation.Rows.Select(r => new[] { r.Label, string.Join(" | ", r.Values) }).ToList());
        var quick = new List<string[]> { new[] { "Minden Record", x.MindenQuickFacts.MindenRecord }, new[] { "Opponent Record", x.MindenQuickFacts.OpponentRecord }, new[] { "Temperature", x.MindenQuickFacts.Temperature }, new[] { "Sky", x.MindenQuickFacts.Sky }, new[] { "Wind", x.MindenQuickFacts.Wind } };
        quick.AddRange(x.MindenQuickFacts.ByTheNumbers.Select((v, i) => new[] { $"By the Numbers {i + 1}", v })); quick.Add(["Prior Season", x.MindenQuickFacts.PriorSeasonSummary]);
        Rows(p, "P1.QuickFacts", x.MindenQuickFacts.Region, "QUICK FACTS", quick);
        p.Heading(x.StatOfWeek.Region, "STAT OF THE WEEK"); var stat = p.BelowHeading(x.StatOfWeek.Region); var statText = x.StatOfWeek.State == PublicationStatOfWeekState.Selected ? string.Join("\n", new[] { x.StatOfWeek.Headline, x.StatOfWeek.DisplayText }.Concat(x.StatOfWeek.SupportingFacts)) : ""; p.Text("P1.StatOfWeek.Content", statText, stat, TypographyRole.Body);
        var refs = new List<string[]>(); refs.AddRange(x.WeeklyReferences.SeriesHistory.Select(v => new[] { "Series History", v })); refs.AddRange(x.WeeklyReferences.WinImplications.Select(v => new[] { "Win Implications", v })); refs.Add(["Series Extremes", x.WeeklyReferences.SeriesExtremes]); refs.Add(["Storyline", x.WeeklyReferences.Storyline]);
        Rows(p, "P1.WeeklyReferences", x.WeeklyReferences.Region, "WEEKLY REFERENCES", refs);
    }

    private static void Rows(PageResolver p, string key, CompositionRegion region, string heading, IReadOnlyList<string[]> rows)
    { p.Heading(region, heading); p.Grid(key, p.BelowHeading(region), ["Label", "Value"], rows, [.35, .65], showHeader: false); }

    private static void Page2(PageResolver p, Page2Composition x)
    {
        foreach (var t in x.VisibleSchedules) p.Table(t.Key, t.Title, t.Region, t.Columns, t.Rows.Select(r => new[] { r.Cells[0], r.Cells[1] + (r.IsDistrictGame ? " *" : ""), r.Cells[2], r.Cells[3] }).ToList(), [.15, .43, .15, .27], t.Details);
        foreach (var t in x.VisibleRankings) p.Table(t.Key, t.Title, t.Region, t.Columns, t.Rows.Select(r => r.Cells.ToArray()).ToList(), [.10, .48, .18, .24], t.Details);
    }

    private static void Page3(PageResolver p, Page3Composition x)
    {
        foreach (var t in x.VisibleIndividualStatistics)
        {
            if (t.Role == Page3IndividualStatisticsRole.Unknown) p.Diagnostic(t.Key, ResolvedLayoutDiagnosticCode.UnsupportedSemanticRole, $"Unknown individual-statistics role for '{t.Heading}'.");
            var widths = t.Columns.Count <= 1 ? new[] { 1d } : new[] { .42 }.Concat(Enumerable.Repeat(.58 / (t.Columns.Count - 1), t.Columns.Count - 1)).ToArray();
            var details = ReferenceEquals(t, x.VisibleIndividualStatistics.FirstOrDefault()) ? new[] { x.ProductionLabel, x.StatisticalSeason?.ToString(CultureInfo.InvariantCulture) ?? "" } : [];
            p.Table(t.Key, t.Heading, t.Region, t.Columns, t.Rows.Select(r => r.Cells.ToArray()).ToList(), widths, details);
        }
        p.Table(x.VisibleDefense.Key, x.VisibleDefense.Heading, x.VisibleDefense.Region, x.VisibleDefense.Columns, x.VisibleDefense.Rows.Select(r => r.Cells.ToArray()).ToList(), [], absoluteWidths: DefenseWidths);
        p.Table(x.VisiblePlayerOfGame.Key, x.VisiblePlayerOfGame.Heading, x.VisiblePlayerOfGame.Region, x.VisiblePlayerOfGame.Columns, x.VisiblePlayerOfGame.Rows.Select(r => r.Cells.ToArray()).ToList(), [.10, .27, .63]);
        p.Table(x.VisibleRosterReference.Key, x.VisibleRosterReference.Heading, x.VisibleRosterReference.Region, x.VisibleRosterReference.Columns, x.VisibleRosterReference.Rows.Select(r => r.Cells.ToArray()).ToList(), [.12, .52, .12, .24]);
    }

    private static void Historical(PageResolver p, IReadOnlyList<HistoricalReferenceBlock> blocks)
    {
        foreach (var b in blocks)
        {
            p.Heading(b.Region, b.Content.Title); var valueCount = Math.Max(1, b.Content.Rows.Select(r => r.Values.Count).DefaultIfEmpty().Max()); var columns = new[] { "Label" }.Concat(Enumerable.Range(1, valueCount).Select(i => $"Value{i}")).ToArray(); var widths = new[] { .32 }.Concat(Enumerable.Repeat(.68 / valueCount, valueCount)).ToArray(); var rows = b.Content.Rows.Select(r => new[] { r.Label }.Concat(r.Values).Concat(Enumerable.Repeat("", valueCount - r.Values.Count)).ToArray()).ToList(); p.Grid(b.Key, p.BelowHeading(b.Region), columns, rows, widths, showHeader: false, alignments: Enumerable.Repeat(CompositionAlignment.Left, columns.Length).ToArray());
        }
    }

    private static void Page7(PageResolver p, Page7Composition x)
    {
        var titleRegion = x.Context.Regions.Single(r => r.Key == "P7.PermanentTitle"); var descriptorRegion = x.Context.Regions.Single(r => r.Key == "P7.PermanentDescriptor");
        p.Text("P7.PermanentTitle", x.PermanentTitle, titleRegion.Bounds, TypographyRole.Feature);
        p.Text("P7.PermanentDescriptor", x.PermanentDescriptor, descriptorRegion.Bounds, TypographyRole.Body);
        foreach (var f in x.Features)
        {
            var headingHeight = p.LineHeight(TypographyRole.SectionHeading) + 2; p.Text($"P7.Feature.{f.SourceIndex}.Title", f.Content.Title, new(f.Region.Bounds.X, f.Region.Bounds.Y, f.Region.Bounds.Width, headingHeight), TypographyRole.SectionHeading);
            var y = f.Region.Bounds.Y + headingHeight + 3; p.Text($"P7.Feature.{f.SourceIndex}.Body", f.Content.Content, new(f.Region.Bounds.X, y, f.Region.Bounds.Width, Math.Max(0, f.Region.Bounds.Bottom - y)), TypographyRole.Body);
        }
    }

    private static void Roster(PageResolver p, RosterPageComposition roster)
    {
        var body = roster.Context.Shell.BodyBounds; p.Heading(new CompositionRegion("Roster", "ROSTER", body, CompositionFlexibility.Rigid, CompositionDensity.Standard, TypographyRole.SectionHeading), "ROSTER");
        foreach (var bank in roster.Banks)
        {
            if (bank.Rows.Count == 0) continue; var first = bank.Rows[0];
            for (var i = 0; i < first.Cells.Count; i++) p.Text($"P{roster.Context.PageNumber}.Roster.Bank{bank.BankNumber}.Heading.{i}", first.Cells[i].Column, new(first.Cells[i].Bounds.X, bank.Bounds.Y - 14, first.Cells[i].Bounds.Width, 14), TypographyRole.Roster, first.Cells[i].Alignment, fixedPoints: roster.CommonFontPoints);
            foreach (var row in bank.Rows) for (var i = 0; i < row.Cells.Count; i++) { var cell = row.Cells[i]; p.Text($"P{roster.Context.PageNumber}.Roster.{row.SourceIndex}.{cell.Column}", cell.Value, cell.Bounds, cell.Typography, cell.Alignment, fixedPoints: cell.FontPoints, horizontalInset: cell.HorizontalInset, verticalInset: cell.VerticalInset, verticalCenter: true, kind: ResolvedPrimitiveKind.TableCell, tableKey: $"P{roster.Context.PageNumber}.Roster", rowIndex: row.SourceIndex, columnIndex: i, columnIdentity: cell.Column); }
        }
    }

    private static IEnumerable<string> VisibleText(WeeklyGameNotesComposition c)
    {
        foreach (var page in c.Pages)
        {
            yield return page.Context.Shell.Header.Title; yield return page.Context.Shell.Header.School; yield return page.Context.Shell.Header.TeamName; yield return page.Context.Shell.Footer.Label; yield return page.Context.PageNumber.ToString(CultureInfo.InvariantCulture);
            switch (page)
            {
                case Page1Composition x:
                    foreach (var v in new[] { x.GameIdentity.Opponent, x.GameIdentity.GameDate, x.GameIdentity.KickoffTime, x.GameIdentity.Venue, x.StatOfWeek.Headline, x.StatOfWeek.DisplayText, x.MindenQuickFacts.MindenRecord, x.MindenQuickFacts.OpponentRecord, x.MindenQuickFacts.Temperature, x.MindenQuickFacts.Sky, x.MindenQuickFacts.Wind, x.MindenQuickFacts.PriorSeasonSummary, x.WeeklyReferences.SeriesExtremes, x.WeeklyReferences.Storyline }) yield return v;
                    if (x.LookingBack is not null) { foreach (var v in new[] { x.LookingBack.PreviousOpponent, x.LookingBack.PreviousGameDate, x.LookingBack.PreviousGameSite, x.LookingBack.FinalScoreResult }) yield return v; foreach (var s in x.LookingBack.PeriodScores) yield return s.Label; foreach (var s in x.LookingBack.ScoringPlays) foreach (var v in new[] { s.Clock, s.Team, s.Description, s.ScoreAfterPlay }) yield return v; }
                    foreach (var r in x.OpponentInformation.Rows) { yield return r.Label; foreach (var v in r.Values) yield return v; } foreach (var v in x.MindenQuickFacts.ByTheNumbers.Concat(x.StatOfWeek.SupportingFacts).Concat(x.WeeklyReferences.SeriesHistory).Concat(x.WeeklyReferences.WinImplications)) yield return v; break;
                case Page2Composition x: foreach (var t in x.VisibleSchedules.Cast<object>().Concat(x.VisibleRankings)) foreach (var v in TableText(t)) yield return v; break;
                case Page3Composition x: yield return x.ProductionLabel; foreach (var t in x.VisibleIndividualStatistics.Cast<object>().Concat([x.VisibleDefense, x.VisiblePlayerOfGame, x.VisibleRosterReference])) foreach (var v in TableText(t)) yield return v; break;
                case Page4Composition x: foreach (var b in x.HistoricalBlocks) { yield return b.Content.Title; foreach (var r in b.Content.Rows) { yield return r.Label; foreach (var v in r.Values) yield return v; } } break;
                case Page5Composition x: foreach (var b in x.HistoricalBlocks) { yield return b.Content.Title; foreach (var r in b.Content.Rows) { yield return r.Label; foreach (var v in r.Values) yield return v; } } break;
                case Page6Composition x: foreach (var v in x.TeamStatistics.Columns.Concat(x.TeamStatistics.Rows.SelectMany(r => new[] { r.Label, r.Minden, r.Opponent }))) yield return v; break;
                case Page7Composition x: yield return x.PermanentTitle; yield return x.PermanentDescriptor; foreach (var f in x.Features) { yield return f.Content.Title; yield return f.Content.Content; } break;
                case Page8Composition x: foreach (var v in x.Roster.Banks.SelectMany(b => b.Rows).SelectMany(r => r.Cells).SelectMany(c => new[] { c.Column, c.Value })) yield return v; break;
                case Page9Composition x: foreach (var v in x.Roster.Banks.SelectMany(b => b.Rows).SelectMany(r => r.Cells).SelectMany(c => new[] { c.Column, c.Value })) yield return v; break;
            }
        }
        foreach (var fixedText in new[] { "GAME INFORMATION", "LOOKING BACK", "OPPONENT INFORMATION", "QUICK FACTS", "STAT OF THE WEEK", "WEEKLY REFERENCES", "ROSTER", "Label", "Value", "Period", "Minden", "Opponent", "Q", "Clock", "Team", "Play", "Score", " *" }) yield return fixedText;
        static IEnumerable<string> TableText(object value) => value switch
        {
            Page2ScheduleComposition x => x.Columns.Concat(x.Details).Concat(x.Rows.SelectMany(r => r.Cells)), Page2RankingComposition x => x.Columns.Concat(x.Details).Concat(x.Rows.SelectMany(r => r.Cells)),
            Page3IndividualStatisticsComposition x => x.Columns.Prepend(x.Heading).Concat(x.Rows.SelectMany(r => r.Cells)), Page3DefenseComposition x => x.Columns.Prepend(x.Heading).Concat(x.Rows.SelectMany(r => r.Cells)),
            Page3PlayerOfGameComposition x => x.Columns.Prepend(x.Heading).Concat(x.Rows.SelectMany(r => r.Cells)), Page3RosterReferenceComposition x => x.Columns.Prepend(x.Heading).Concat(x.Rows.SelectMany(r => r.Cells)), _ => []
        };
    }

    private static void ValidateDocument(WeeklyGameNotesComposition composition, IReadOnlyList<ResolvedPageLayout> pages, List<ResolvedLayoutDiagnostic> diagnostics)
    {
        if (pages.Count != 10 || !pages.Select(x => x.PageNumber).SequenceEqual(Enumerable.Range(1, 10))) diagnostics.Add(new(0, "Document", ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.ProjectionCompleteness, "Resolved layout must contain exactly pages 1 through 10."));
        if (pages.SelectMany(x => x.Primitives).GroupBy(x => x.Key, StringComparer.Ordinal).Any(x => x.Count() != 1)) diagnostics.Add(new(0, "Document", ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.DuplicatePrimitiveKey, "Resolved primitive keys must be globally unique."));
    }

    public static string ComputeSemanticIdentityForVerification(ResolvedWeeklyGameNotesLayout layout) => Identity(layout with { SemanticIdentity = "" });

    private static string Identity(ResolvedWeeklyGameNotesLayout layout)
    {
        var c = new StringBuilder(); void Token(string kind, string path, string value) { T(kind); T(path); T(value); } void T(string s) => c.Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s).Append('|'); void V(string path, object? v) => Token("scalar", path, v switch { null => "<null>", double d => d.ToString("R", CultureInfo.InvariantCulture), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "", _ => v.ToString() ?? "" }); void Count(string path, int value) => Token("collection", path, value.ToString(CultureInfo.InvariantCulture)); void Rect(string path, CompositionRect r) { V(path + ".X", r.X); V(path + ".Y", r.Y); V(path + ".Width", r.Width); V(path + ".Height", r.Height); }
        V("ParentCompositionIdentity", layout.ParentCompositionIdentity); V("MetricAlgorithmVersion", layout.MetricAlgorithmVersion); Count("Fonts", layout.Fonts.Count); for (var fi = 0; fi < layout.Fonts.Count; fi++) { var f = layout.Fonts[fi]; var fp = $"Fonts[{fi}]"; V(fp + ".Family", f.Family); V(fp + ".Subfamily", f.Subfamily); V(fp + ".Style", f.Style); V(fp + ".Weight", f.Weight); V(fp + ".Stretch", f.Stretch); V(fp + ".FaceIndex", f.FaceIndex); V(fp + ".FileSha256", f.FileSha256); V(fp + ".MetricIdentity", f.MetricIdentity); Count(fp + ".RequiredCodePoints", f.RequiredCodePoints.Count); for (var i = 0; i < f.RequiredCodePoints.Count; i++) V($"{fp}.RequiredCodePoints[{i}]", f.RequiredCodePoints[i]); }
        Count("Pages", layout.Pages.Count); for (var pi = 0; pi < layout.Pages.Count; pi++) { var page = layout.Pages[pi]; var pp = $"Pages[{pi}]"; V(pp + ".PageNumber", page.PageNumber); Rect(pp + ".PageBounds", page.PageBounds); Rect(pp + ".BodyBounds", page.BodyBounds); Count(pp + ".Primitives", page.Primitives.Count); for (var pri = 0; pri < page.Primitives.Count; pri++) { var primitive = page.Primitives[pri]; var p = $"{pp}.Primitives[{pri}]"; V(p + ".Kind", primitive.Kind); V(p + ".Key", primitive.Key); V(p + ".Layer", primitive.Layer); V(p + ".Sequence", primitive.Sequence); Rect(p + ".Bounds", primitive.Bounds); Rect(p + ".ClipBounds", primitive.ClipBounds); V(p + ".GovernedRegionKey", primitive.GovernedRegionKey); Rect(p + ".GovernedRegionBounds", primitive.GovernedRegionBounds); switch (primitive) { case ResolvedTextPrimitive x: Text(p + ".Text", x); break; case ResolvedTableCellPrimitive x: V(p + ".TableKey", x.TableKey); V(p + ".RowIndex", x.RowIndex); V(p + ".ColumnIndex", x.ColumnIndex); V(p + ".ColumnIdentity", x.ColumnIdentity); V(p + ".HorizontalInset", x.HorizontalInset); V(p + ".VerticalInset", x.VerticalInset); Text(p + ".Text", x.Text); break; case ResolvedRulePrimitive x: V(p + ".Role", x.Role); V(p + ".Thickness", x.Thickness); V(p + ".X1", x.X1); V(p + ".Y1", x.Y1); V(p + ".X2", x.X2); V(p + ".Y2", x.Y2); Rect(p + ".PaintedBounds", x.PaintedBounds); break; } } } Count("Diagnostics", layout.Diagnostics.Count); for (var di = 0; di < layout.Diagnostics.Count; di++) Diagnostic($"Diagnostics[{di}]", layout.Diagnostics[di]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.ToString())));
        void Text(string path, ResolvedTextPrimitive x) { V(path + ".SourceText", x.SourceText); V(path + ".FontFamily", x.FontFamily); V(path + ".FontFileSha256", x.FontFileSha256); V(path + ".FontPoints", x.FontPoints); V(path + ".Leading", x.Leading); V(path + ".Bold", x.Bold); V(path + ".Typography", x.Typography); V(path + ".Alignment", x.Alignment); V(path + ".HorizontalInset", x.HorizontalInset); V(path + ".VerticalInset", x.VerticalInset); Count(path + ".Lines", x.Lines.Count); for (var i = 0; i < x.Lines.Count; i++) { var l = x.Lines[i]; var lp = $"{path}.Lines[{i}]"; V(lp + ".Text", l.Text); V(lp + ".SourceSpan.Start", l.SourceSpan.Start); V(lp + ".SourceSpan.Length", l.SourceSpan.Length); V(lp + ".MeasuredAdvance", l.MeasuredAdvance); V(lp + ".BaselineX", l.BaselineX); V(lp + ".BaselineY", l.BaselineY); } }
        void Diagnostic(string path, ResolvedLayoutDiagnostic x) { V(path + ".PageNumber", x.PageNumber); V(path + ".Region", x.Region); V(path + ".Severity", x.Severity); V(path + ".Code", x.Code); V(path + ".Message", x.Message); }
    }

    public static IReadOnlyList<ResolvedLayoutDiagnostic> ValidateResolvedLayout(ResolvedWeeklyGameNotesLayout layout)
    {
        var result = new List<ResolvedLayoutDiagnostic>();
        foreach (var page in layout.Pages)
        {
            foreach (var primitive in page.Primitives)
            {
                if (!page.PageBounds.Contains(primitive.Bounds) || !primitive.GovernedRegionBounds.Contains(primitive.Bounds)) result.Add(new(page.PageNumber, primitive.GovernedRegionKey, ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.PrimitiveOutsideRegion, $"Primitive '{primitive.Key}' escapes its governed region."));
                if (!page.PageBounds.Contains(primitive.ClipBounds) || !primitive.GovernedRegionBounds.Contains(primitive.ClipBounds) || primitive is not ResolvedRulePrimitive && !primitive.Bounds.Contains(primitive.ClipBounds)) result.Add(new(page.PageNumber, primitive.GovernedRegionKey, ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.InvalidClipping, $"Primitive '{primitive.Key}' has an invalid governed clip."));
                foreach (var text in primitive switch { ResolvedTextPrimitive t => new[] { t }, ResolvedTableCellPrimitive c => [c.Text], _ => [] })
                {
                    try
                    {
                        var physical = GovernedTypographyMetrics.PhysicalExtents(text.FontPoints, text.FontFamily, text.Bold);
                        if (!string.Equals(physical.FileSha256, text.FontFileSha256, StringComparison.Ordinal)) result.Add(new(page.PageNumber, text.GovernedRegionKey, ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.FontIdentityMismatch, $"Primitive '{text.Key}' does not match its required physical font binary."));
                        foreach (var line in text.Lines) if (double.IsNaN(line.BaselineY) || line.BaselineY - physical.Ascent < text.ClipBounds.Y - Tolerance || line.BaselineY + physical.Descent > text.ClipBounds.Bottom + Tolerance) { result.Add(new(page.PageNumber, text.GovernedRegionKey, ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.InvalidBaseline, $"Primitive '{text.Key}' has physical glyph extents outside its clip.")); break; }
                    }
                    catch (Exception ex) { result.Add(new(page.PageNumber, text.GovernedRegionKey, ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.FontIdentityMismatch, ex.Message)); }
                    var cursor = 0; foreach (var line in text.Lines) { if (line.SourceSpan.Start < cursor) { result.Add(new(page.PageNumber, text.GovernedRegionKey, ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.ContentDuplication, $"Primitive '{text.Key}' duplicates authoritative source characters.")); break; } if (line.SourceSpan.Start > cursor || line.SourceSpan.Length < 0 || line.SourceSpan.Start + line.SourceSpan.Length > text.SourceText.Length) { result.Add(new(page.PageNumber, text.GovernedRegionKey, ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.ContentLoss, $"Primitive '{text.Key}' omits authoritative source characters.")); break; } cursor += line.SourceSpan.Length; var represented = text.SourceText.Substring(line.SourceSpan.Start, line.SourceSpan.Length).TrimEnd('\r', '\n'); if (!string.Equals(represented, line.Text, StringComparison.Ordinal)) { result.Add(new(page.PageNumber, text.GovernedRegionKey, ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.ContentLoss, $"Primitive '{text.Key}' rewrites authoritative source characters.")); break; } } if (cursor != text.SourceText.Length) result.Add(new(page.PageNumber, text.GovernedRegionKey, ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.ContentLoss, $"Primitive '{text.Key}' does not cover all authoritative source text."));
                }
            }
            if (page.Primitives.Select(x => x.Sequence).Distinct().Count() != page.Primitives.Count || page.Primitives.OrderBy(x => x.Sequence).Select(x => x.Sequence).Where((x, i) => x != i).Any() || !page.Primitives.SequenceEqual(page.Primitives.OrderBy(x => x.Layer).ThenBy(x => x.Sequence))) result.Add(new(page.PageNumber, "Page", ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.NondeterministicPrimitiveOrder, "Primitive order and sequence must be deterministic, unique and contiguous."));
        }
        if (layout.Pages.SelectMany(x => x.Primitives).GroupBy(x => x.Key, StringComparer.Ordinal).Any(x => x.Count() != 1)) result.Add(new(0, "Document", ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.DuplicatePrimitiveKey, "Resolved primitive keys must be globally unique."));
        return result.AsReadOnly();
    }

    private sealed class PageResolver
    {
        private readonly int page; private readonly PublicationDesignContract design; private readonly GovernedTypographyMetrics metrics; private readonly PageShell shell; private readonly List<IResolvedLayoutPrimitive> primitives = []; private readonly List<ResolvedLayoutDiagnostic> diagnostics = []; private int sequence;
        public PageResolver(int page, PublicationDesignContract design, GovernedTypographyMetrics metrics, PageShell shell) { this.page = page; this.design = design; this.metrics = metrics; this.shell = shell; }
        public double LineHeight(TypographyRole role) { var t = Type(role); return GovernedTypographyMetrics.Round(t.PreferredPoints * design.TextLeading); }
        public CompositionRect BelowHeading(CompositionRegion region) { var h = LineHeight(TypographyRole.SectionHeading) + design.Spacing[SpacingRole.Tight]; return new(region.Bounds.X, region.Bounds.Y + h, region.Bounds.Width, Math.Max(0, region.Bounds.Height - h)); }
        public void Shell()
        {
            var h = shell.Header.Bounds; var school = h.Width * .25; var title = h.Width * .50; var headerRule = design.Rules[RuleRole.Strong]; var headerRegion = new CompositionRect(h.X, h.Y, h.Width, h.Height + headerRule / 2);
            Text($"P{page}.Shell.School", shell.Header.School, new(h.X, h.Y, school, h.Height), TypographyRole.Body, CompositionAlignment.Left, verticalCenter: true, governedRegionKey: "Shell.Header", governedRegionBounds: headerRegion);
            Text($"P{page}.Shell.Masthead", shell.Header.Title, new(h.X + school, h.Y, title, h.Height), TypographyRole.PublicationMasthead, CompositionAlignment.Center, verticalCenter: true, governedRegionKey: "Shell.Header", governedRegionBounds: headerRegion);
            Text($"P{page}.Shell.Team", shell.Header.TeamName, new(h.Right - school, h.Y, school, h.Height), TypographyRole.Body, CompositionAlignment.Right, verticalCenter: true, governedRegionKey: "Shell.Header", governedRegionBounds: headerRegion);
            Rule($"P{page}.Shell.HeaderRule", RuleRole.Strong, h.X, h.Bottom, h.Right, h.Bottom, "Shell.Header", headerRegion);
            var f = shell.Footer.Bounds; var footerRule = design.Rules[RuleRole.Normal]; var footerRegion = new CompositionRect(f.X, f.Y - footerRule / 2, f.Width, f.Height + footerRule / 2); Rule($"P{page}.Shell.FooterRule", RuleRole.Normal, f.X, f.Y, f.Right, f.Y, "Shell.Footer", footerRegion); Text($"P{page}.Shell.FooterLabel", shell.Footer.Label, new(f.X, f.Y, f.Width / 2, f.Height), TypographyRole.Footer, verticalCenter: true, governedRegionKey: "Shell.Footer", governedRegionBounds: footerRegion); Text($"P{page}.Shell.PageNumber", page.ToString(CultureInfo.InvariantCulture), new(f.X + f.Width / 2, f.Y, f.Width / 2, f.Height), TypographyRole.Footer, CompositionAlignment.Right, verticalCenter: true, governedRegionKey: "Shell.Footer", governedRegionBounds: footerRegion);
        }
        public void Heading(CompositionRegion region, string text) { var height = LineHeight(TypographyRole.SectionHeading) + design.Spacing[SpacingRole.Tight]; Text($"P{page}.{region.Key}.Heading", text, new(region.Bounds.X, region.Bounds.Y, region.Bounds.Width, height), TypographyRole.SectionHeading, verticalCenter: true, governedRegionKey: region.Key, governedRegionBounds: region.Bounds); Rule($"P{page}.{region.Key}.HeadingRule", RuleRole.Normal, region.Bounds.X, region.Bounds.Y + height, region.Bounds.Right, region.Bounds.Y + height, region.Key, region.Bounds); }
        public void Table(string key, string typedHeading, CompositionRegion region, IReadOnlyList<string> columns, IReadOnlyList<string[]> rows, IReadOnlyList<double> widths, IReadOnlyList<string>? details = null, IReadOnlyList<CompositionAlignment>? alignments = null, IReadOnlyList<double>? absoluteWidths = null)
        {
            if (!string.Equals(typedHeading, region.Heading, StringComparison.Ordinal)) Diagnostic(key, ResolvedLayoutDiagnosticCode.ProjectionCompleteness, $"Typed heading '{typedHeading}' conflicts with governed region heading '{region.Heading}'."); Heading(region, typedHeading); var body = BelowHeading(region); var detailList = details?.Where(x => !string.IsNullOrEmpty(x)).ToList() ?? []; for (var i = 0; i < detailList.Count; i++) { var h = LineHeight(TypographyRole.Body) + 2; Text($"P{page}.{key}.Detail.{i}", detailList[i], new(body.X, body.Y, body.Width, h), TypographyRole.Body, governedRegionKey: region.Key, governedRegionBounds: region.Bounds); body = new(body.X, body.Y + h, body.Width, Math.Max(0, body.Height - h)); } Grid(key, body, columns, rows, widths, alignments: alignments, absoluteWidths: absoluteWidths);
        }
        public void Grid(string key, CompositionRect bounds, IReadOnlyList<string> columns, IReadOnlyList<string[]> rows, IReadOnlyList<double> widths, bool showHeader = true, bool verticalRules = true, IReadOnlyList<CompositionAlignment>? alignments = null, IReadOnlyList<double>? absoluteWidths = null)
        {
            if (columns.Count == 0 || rows.Any(r => r.Length != columns.Count) || absoluteWidths is null && (widths.Count != columns.Count || Math.Abs(widths.Sum() - 1) > Tolerance) || absoluteWidths is not null && absoluteWidths.Count != columns.Count) { Diagnostic(key, ResolvedLayoutDiagnosticCode.ProjectionCompleteness, "Table projection does not match its governed column grammar."); return; }
            var count = rows.Count + (showHeader ? 1 : 0); if (count == 0) return; var xPositions = new double[columns.Count + 1]; xPositions[0] = bounds.X;
            if (absoluteWidths is not null) { if (Math.Abs(absoluteWidths.Sum() - bounds.Width) > Tolerance) Diagnostic(key, ResolvedLayoutDiagnosticCode.TableOverflow, $"Absolute table width {absoluteWidths.Sum():0.###} does not match governed region width {bounds.Width:0.###}."); for (var i = 0; i < columns.Count; i++) xPositions[i + 1] = GovernedTypographyMetrics.Round(xPositions[i] + absoluteWidths[i]); }
            else for (var i = 0; i < columns.Count; i++) xPositions[i + 1] = i == columns.Count - 1 ? bounds.Right : GovernedTypographyMetrics.Round(xPositions[i] + bounds.Width * widths[i]);
            var all = showHeader ? new[] { columns.ToArray() }.Concat(rows).ToList() : rows.ToList(); var dataType = Type(TypographyRole.Data); var weights = new List<int>(); foreach (var row in all) { var weight = 1; for (var c = 0; c < columns.Count; c++) { try { weight = Math.Max(weight, Break(row[c], Math.Max(0, xPositions[c + 1] - xPositions[c] - 6), dataType.MinimumPoints, dataType.Family, dataType.Bold).Count); } catch { } } weights.Add(weight); }
            var unitHeight = bounds.Height / weights.Sum(); if (unitHeight <= 0) { Diagnostic(key, ResolvedLayoutDiagnosticCode.TableOverflow, "Table has no available vertical space."); return; } var currentY = bounds.Y;
            for (var r = 0; r < all.Count; r++) { var y = GovernedTypographyMetrics.Round(currentY); var bottom = r == all.Count - 1 ? bounds.Bottom : GovernedTypographyMetrics.Round(currentY + unitHeight * weights[r]); currentY = bottom; for (var c = 0; c < columns.Count; c++) { var cell = new CompositionRect(xPositions[c], y, xPositions[c + 1] - xPositions[c], bottom - y); var align = alignments is not null ? alignments[c] : c == 0 ? CompositionAlignment.Left : CompositionAlignment.Center; Text($"P{page}.{key}.R{r}.C{c}", all[r][c], cell, TypographyRole.Data, align, horizontalInset: 3, verticalInset: 1, verticalCenter: true, kind: ResolvedPrimitiveKind.TableCell, tableKey: key, rowIndex: showHeader ? r - 1 : r, columnIndex: c, columnIdentity: columns[c], governedRegionKey: key, governedRegionBounds: bounds); } if (r == 0 && showHeader) Rule($"P{page}.{key}.HeaderRule", RuleRole.Normal, bounds.X, bottom, xPositions[^1], bottom, key, bounds); else if (r < all.Count - 1) Rule($"P{page}.{key}.RowRule.{r}", RuleRole.Light, bounds.X, bottom, xPositions[^1], bottom, key, bounds); }
            if (verticalRules) for (var c = 1; c < columns.Count; c++) Rule($"P{page}.{key}.ColumnRule.{c}", RuleRole.Light, xPositions[c], bounds.Y, xPositions[c], bounds.Bottom, key, bounds);
        }
        public void Text(string key, string source, CompositionRect bounds, TypographyRole role, CompositionAlignment alignment = CompositionAlignment.Left, double? fixedPoints = null, double horizontalInset = 2, double verticalInset = 1, bool verticalCenter = false, ResolvedPrimitiveKind kind = ResolvedPrimitiveKind.Text, string? tableKey = null, int rowIndex = -1, int columnIndex = -1, string columnIdentity = "", string? governedRegionKey = null, CompositionRect? governedRegionBounds = null)
        {
            source ??= ""; governedRegionKey ??= key; governedRegionBounds ??= bounds; var type = Type(role); var bold = type.Bold; var usable = new CompositionRect(bounds.X + horizontalInset, bounds.Y + verticalInset, Math.Max(0, bounds.Width - 2 * horizontalInset), Math.Max(0, bounds.Height - 2 * verticalInset)); var points = fixedPoints ?? type.PreferredPoints; IReadOnlyList<LineSeed>? seeds = null; var failure = kind == ResolvedPrimitiveKind.TableCell ? ResolvedLayoutDiagnosticCode.CellOverflow : ResolvedLayoutDiagnosticCode.VerticalOverflow; GovernedTypographyPreflightException? glyphFailure = null;
            while (points + Tolerance >= (fixedPoints ?? type.MinimumPoints)) { try { seeds = Break(source, usable.Width, points, type.Family, bold); } catch (UnbreakableException) { failure = ResolvedLayoutDiagnosticCode.UnbreakableValue; seeds = null; } catch (GovernedTypographyPreflightException ex) { glyphFailure = ex; seeds = [new(source, new(0, source.Length), 0)]; break; } if (seeds is not null && PhysicalHeight(seeds.Count, points, type.Family, bold) <= usable.Height + Tolerance) break; if (fixedPoints is not null) { seeds = null; break; } points = GovernedTypographyMetrics.Round(points - design.LocalFitStepPoints); }
            if (glyphFailure is not null) Diagnostic(key, glyphFailure.DiagnosticCode, glyphFailure.Message); else if (seeds is null || points < (fixedPoints ?? type.MinimumPoints) - Tolerance) { Diagnostic(key, failure, $"Visible text cannot physically fit '{key}' at governed minimum typography without content loss."); Diagnostic(key, ResolvedLayoutDiagnosticCode.MinimumSizeFittingFailure, $"Visible text cannot fit '{key}' within governed typography."); points = fixedPoints ?? type.MinimumPoints; try { seeds = Break(source, usable.Width, points, type.Family, bold); } catch (GovernedTypographyPreflightException ex) { Diagnostic(key, ex.DiagnosticCode, ex.Message); seeds = [new(source, new(0, source.Length), 0)]; } catch { seeds = [new(source, new(0, source.Length), 0)]; } }
            seeds ??= [new("", new(0, 0), 0)]; var lineAdvance = GovernedTypographyMetrics.Round(points * design.TextLeading); var ascent = metrics.Ascent(points, type.Family, bold); var descent = metrics.Descent(points, type.Family, bold); var physicalHeight = ascent + descent + Math.Max(0, seeds.Count - 1) * lineAdvance; var top = verticalCenter ? usable.Y + (usable.Height - physicalHeight) / 2 : usable.Y; var lines = new List<ResolvedTextLine>();
            for (var i = 0; i < seeds.Count; i++) { var s = seeds[i]; var bx = alignment switch { CompositionAlignment.Center => usable.X + (usable.Width - s.Advance) / 2, CompositionAlignment.Right => usable.Right - s.Advance, _ => usable.X }; var by = top + ascent + i * lineAdvance; bx = GovernedTypographyMetrics.Round(bx); by = GovernedTypographyMetrics.Round(by); var glyphTop = by - ascent; var glyphBottom = by + descent; if (double.IsNaN(by) || glyphTop < usable.Y - Tolerance || glyphBottom > usable.Bottom + Tolerance) Diagnostic(key, ResolvedLayoutDiagnosticCode.InvalidBaseline, "Resolved physical glyph extents exceed the governed clip."); lines.Add(new(s.Text, s.Span, s.Advance, bx, by)); }
            var identity = metrics.Identity(type.Family, bold); var text = new ResolvedTextPrimitive(key, source, lines.AsReadOnly(), type.Family, identity.FileSha256, GovernedTypographyMetrics.Round(points), design.TextLeading, bold, role, alignment, bounds, bounds, horizontalInset, verticalInset, governedRegionKey, governedRegionBounds, ResolvedPrimitiveLayer.Content, sequence++);
            if (kind == ResolvedPrimitiveKind.TableCell) primitives.Add(new ResolvedTableCellPrimitive(key, tableKey!, rowIndex, columnIndex, columnIdentity, text, horizontalInset, verticalInset, bounds, bounds, governedRegionKey, governedRegionBounds, ResolvedPrimitiveLayer.Content, text.Sequence)); else primitives.Add(text);
        }
        public void Rule(string key, RuleRole role, double x1, double y1, double x2, double y2, string governedRegionKey, CompositionRect governedRegionBounds)
        {
            var thickness = design.Rules[role]; var horizontal = Math.Abs(y2 - y1) <= Tolerance; var vertical = Math.Abs(x2 - x1) <= Tolerance; if (!horizontal && !vertical) throw new InvalidOperationException("Only governed horizontal or vertical rules are supported."); if (horizontal) { if (Math.Abs(y1 - governedRegionBounds.Y) <= Tolerance) y1 = y2 = governedRegionBounds.Y + thickness / 2; else if (Math.Abs(y1 - governedRegionBounds.Bottom) <= Tolerance) y1 = y2 = governedRegionBounds.Bottom - thickness / 2; } else { if (Math.Abs(x1 - governedRegionBounds.X) <= Tolerance) x1 = x2 = governedRegionBounds.X + thickness / 2; else if (Math.Abs(x1 - governedRegionBounds.Right) <= Tolerance) x1 = x2 = governedRegionBounds.Right - thickness / 2; } x1 = GovernedTypographyMetrics.Round(x1); y1 = GovernedTypographyMetrics.Round(y1); x2 = GovernedTypographyMetrics.Round(x2); y2 = GovernedTypographyMetrics.Round(y2); var painted = horizontal ? new CompositionRect(Math.Min(x1, x2), y1 - thickness / 2, Math.Abs(x2 - x1), thickness) : new CompositionRect(x1 - thickness / 2, Math.Min(y1, y2), thickness, Math.Abs(y2 - y1)); primitives.Add(new ResolvedRulePrimitive(key, role, thickness, x1, y1, x2, y2, painted, governedRegionBounds, governedRegionKey, governedRegionBounds, ResolvedPrimitiveLayer.Rules, sequence++));
        }
        public void Diagnostic(string region, ResolvedLayoutDiagnosticCode code, string message) => diagnostics.Add(new(page, region, ResolvedLayoutSeverity.Blocking, code, message));
        public ResolvedPageLayout Finish()
        {
            var pageBounds = new CompositionRect(0, 0, design.PageWidth, design.PageHeight); var provisional = new ResolvedWeeklyGameNotesLayout("", GovernedTypographyMetrics.AlgorithmVersion, [], [new(page, pageBounds, shell.BodyBounds, primitives.OrderBy(x => x.Layer).ThenBy(x => x.Sequence).ToList().AsReadOnly(), [])], [], ""); if (diagnostics.Count == 0) diagnostics.AddRange(ValidateResolvedLayout(provisional)); return new(page, pageBounds, shell.BodyBounds, primitives.OrderBy(x => x.Layer).ThenBy(x => x.Sequence).ToList().AsReadOnly(), diagnostics.AsReadOnly());
        }
        private double PhysicalHeight(int lines, double points, string family, bool bold) => metrics.Ascent(points, family, bold) + metrics.Descent(points, family, bold) + Math.Max(0, lines - 1) * GovernedTypographyMetrics.Round(points * design.TextLeading);
        private TypographyRange Type(TypographyRole role) => design.Typography.Single(x => x.Role == role);
        private IReadOnlyList<LineSeed> Break(string source, double width, double points, string family, bool bold)
        {
            var result = new List<LineSeed>(); var start = 0;
            while (start <= source.Length)
            {
                var nl = source.IndexOfAny(['\r', '\n'], start); var end = nl < 0 ? source.Length : nl; Wrap(start, end); if (nl < 0) break; var newlineLength = source[nl] == '\r' && nl + 1 < source.Length && source[nl + 1] == '\n' ? 2 : 1; if (end == start) result.Add(new("", new(start, newlineLength), 0)); else { var last = result[^1]; result[^1] = last with { Span = new(last.Span.Start, last.Span.Length + newlineLength) }; } start = nl + newlineLength; if (start == source.Length) { result.Add(new("", new(start, 0), 0)); break; }
            }
            if (source.Length == 0 && result.Count == 0) result.Add(new("", new(0, 0), 0)); return result;
            void Wrap(int begin, int end)
            {
                if (begin == end) return; var lineStart = begin;
                while (lineStart < end)
                {
                    var best = -1; var cursor = lineStart;
                    while (cursor <= end)
                    {
                        var next = cursor == end ? end : source.IndexOf(' ', cursor, end - cursor); if (next < 0) next = end; var candidateEnd = next == end ? end : next + 1; var text = source[lineStart..candidateEnd]; var advance = metrics.Measure(text, points, family, bold); if (advance <= width + Tolerance) { best = candidateEnd; if (candidateEnd == end) break; cursor = candidateEnd; } else break;
                    }
                    if (best <= lineStart) { var tokenEnd = source.IndexOf(' ', lineStart, end - lineStart); if (tokenEnd < 0) tokenEnd = end; throw new UnbreakableException(source[lineStart..tokenEnd]); }
                    var value = source[lineStart..best]; result.Add(new(value, new(lineStart, best - lineStart), metrics.Measure(value, points, family, bold))); lineStart = best;
                }
            }
        }
        private sealed record LineSeed(string Text, ResolvedSourceSpan Span, double Advance);
        private sealed class UnbreakableException(string token) : Exception(token);
    }
}

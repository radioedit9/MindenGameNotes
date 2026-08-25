using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MindenGameNotes;

public static class WeeklyPublicationSupplyBuilder
{
    public static WeeklyPublicationSupply Build(WeeklyProductionHandoff handoff, WeeklyProductionComparison? comparison = null, Func<DateTime>? utcNow = null)
    {
        if (comparison is not null) ValidateComparison(handoff, comparison);
        var package = handoff.InformationPackage;
        var changes = comparison?.ChangedPages.ToDictionary(x => x.PageNumber, x => x.ChangedRequirementKeys) ?? [];
        var statuses = handoff.Pages.ToDictionary(x => x.PageNumber);
        PublicationPageContext Context(int page)
        {
            var status = statuses[page]; var requirements = status.Information.Requirements;
            return new(page, status.Purpose, status.State, status.State == PageProductionState.PublicationReady, status.PublicationBlockingRequirements, status.Advisories, requirements.SelectMany(x => x.Authorities).Distinct().ToList(), Evidence(status.Information), changes.TryGetValue(page, out var keys) ? keys : []);
        }
        var page1Facts = Section<Page1WeeklyFactsPayload>("P1.Page1WeeklyFacts", package.Page1.WeeklyFacts); var stat = Stat(page1Facts);
        var page1 = new Page1PublicationSupply(Context(1), new(package.ProjectId, package.Season, package.Week, package.Page1.WeeklyProject.Opponent, package.Page1.WeeklyProject.GameDate, package.Page1.WeeklyProject.KickoffTime, package.Page1.WeeklyProject.Venue, package.Page1.WeeklyProject.School, package.Page1.WeeklyProject.TeamName), package.Page1.LookingBack, page1Facts, stat);
        var page2 = new Page2PublicationSupply(Context(2), Section<SchedulePayload>("P2.MindenSchedule", package.Page2.MindenSchedule), Section<SchedulePayload>("P2.OpponentSchedule", package.Page2.OpponentSchedule), Section<SchedulePayload>("P2.DistrictSchedule", package.Page2.DistrictSchedule), Section<RankingSnapshotPayload>("P2.Class4APoll", package.Page2.Class4APoll), Section<RankingSnapshotPayload>("P2.DivisionIINonSelectRatings", package.Page2.PowerRatings));
        var sharedRoster = Section<RosterPayload>("P3.MindenRoster", package.Page3.MindenRoster);
        var page3 = new Page3PublicationSupply(Context(3), Section<IndividualStatsPayload>("P3.IndividualOffenseSpecialTeams", package.Page3.OffenseSpecialTeams), package.Page3.Defense.Authority, package.Page3.Defense.HasConflict, Section<PlayerOfGamePayload>("P3.PlayerOfGame", package.Page3.PlayerOfGame), sharedRoster);
        var page4 = new Page4PublicationSupply(Context(4), Section<CoachingHistoryBaselinePayload>("P4.CoachingHistoryBaseline", package.Page4.CoachingHistory));
        var page5 = new Page5PublicationSupply(Context(5), Section<ProgramHistoryBaselinePayload>("P5.ProgramHistoryBaseline", package.Page5.ProgramHistory));
        var page6 = new Page6PublicationSupply(Context(6), Section<TeamStatisticsReportPayload>("P6.TeamStatisticsReport", package.Page6.TeamStatistics));
        var page7 = new Page7PublicationSupply(Context(7), Section<NerdNotesPayload>("P7.NerdNotes", package.Page7.EditorialDisposition));
        var page8 = new Page8PublicationSupply(Context(8), sharedRoster is null ? null : sharedRoster with { RequirementKey = "P8.MindenRoster" });
        var page9 = new Page9PublicationSupply(Context(9), Section<RosterPayload>("P9.OpponentRoster", package.Page9.OpponentRoster));
        var page10 = new Page10PublicationSupply(Context(10), package.Page10.Requirements.Single().Disposition);
        IReadOnlyList<IWeeklyPublicationPage> pages = [page1, page2, page3, page4, page5, page6, page7, page8, page9, page10];
        var semanticIdentity = SemanticIdentity(handoff);
        return new(package.ProjectId, package.Season, package.Week, utcNow?.Invoke() ?? DateTime.UtcNow, handoff.IsClearedForFinalPublication, semanticIdentity, page1, page2, page3, page4, page5, page6, page7, page8, page9, page10, pages);
    }

    private static void ValidateComparison(WeeklyProductionHandoff handoff, WeeklyProductionComparison comparison)
    {
        var package = handoff.InformationPackage; var current = comparison.Current;
        if (current.InformationPackage.ProjectId != package.ProjectId || current.InformationPackage.Season != package.Season || current.InformationPackage.Week != package.Week || current.Pages.Count != 10 || handoff.Pages.Count != 10)
            throw new InvalidOperationException("Publication change information must represent the supplied weekly handoff.");
        var currentPages = current.Pages.GroupBy(x => x.PageNumber).ToDictionary(x => x.Key, x => x.ToList());
        var suppliedPages = handoff.Pages.GroupBy(x => x.PageNumber).ToDictionary(x => x.Key, x => x.ToList());
        if (!Enumerable.Range(1, 10).All(page => currentPages.TryGetValue(page, out var expected) && expected.Count == 1 && suppliedPages.TryGetValue(page, out var actual) && actual.Count == 1 && expected[0].Fingerprint == actual[0].Fingerprint))
            throw new InvalidOperationException("Publication change information must represent the supplied weekly handoff.");
        WeeklyProductionComparison expectedComparison;
        try { expectedComparison = WeeklyProductionHandoffBuilder.Compare(comparison.Previous, comparison.Current); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { throw new InvalidOperationException("Publication change information is not a valid WP 5 comparison.", ex); }
        var actualChanges = comparison.ChangedPages.GroupBy(x => x.PageNumber).ToDictionary(x => x.Key, x => x.ToList());
        var expectedChanges = expectedComparison.ChangedPages.ToDictionary(x => x.PageNumber);
        if (comparison.ChangedPages.Count != expectedComparison.ChangedPages.Count || actualChanges.Any(x => x.Value.Count != 1) || actualChanges.Count != expectedChanges.Count)
            throw new InvalidOperationException("Publication changed pages must exactly match the WP 5 comparison.");
        foreach (var expectedChange in expectedChanges)
        {
            if (!actualChanges.TryGetValue(expectedChange.Key, out var matches)) throw new InvalidOperationException("Publication changed pages must exactly match the WP 5 comparison.");
            var actual = matches[0]; var expected = expectedChange.Value;
            if (actual.PreviousFingerprint != expected.PreviousFingerprint || actual.CurrentFingerprint != expected.CurrentFingerprint || actual.ChangedRequirementKeys.Count != actual.ChangedRequirementKeys.Distinct(StringComparer.Ordinal).Count() || !actual.ChangedRequirementKeys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected.ChangedRequirementKeys.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal))
                throw new InvalidOperationException("Publication changed pages must exactly match the WP 5 comparison.");
        }
    }

    private static string SemanticIdentity(WeeklyProductionHandoff handoff)
    {
        var package = handoff.InformationPackage; var project = package.Page1.WeeklyProject; var canonical = new StringBuilder();
        Append(package.ProjectId.ToString("D", CultureInfo.InvariantCulture)); Append(project.School); Append(project.TeamName); Append(package.Season.ToString(CultureInfo.InvariantCulture)); Append(package.Week.ToString(CultureInfo.InvariantCulture)); Append(project.Opponent); Append(project.GameDate?.ToString("O", CultureInfo.InvariantCulture)); Append(project.KickoffTime?.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)); Append(project.Venue);
        foreach (var page in handoff.Pages.OrderBy(x => x.PageNumber)) { Append(page.PageNumber.ToString(CultureInfo.InvariantCulture)); Append(page.Fingerprint); }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        void Append(string? value) { value ??= ""; canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|'); }
    }

    private static AcceptedPublicationSection<TPayload>? Section<TPayload>(string key, AcceptedSupplementalSection? authority) where TPayload : SupplementalPayload => authority?.Payload is TPayload payload ? new(key, authority, payload, Evidence(authority)) : null;
    private static IReadOnlyList<PublicationEvidenceReference> Evidence(IPageInformationPackage page) => page switch
    {
        Page1InformationPackage x => Evidence(x.WeeklyFacts), Page2InformationPackage x => Many(x.MindenSchedule, x.OpponentSchedule, x.DistrictSchedule, x.Class4APoll, x.PowerRatings), Page3InformationPackage x => Many(x.OffenseSpecialTeams, x.PlayerOfGame, x.MindenRoster), Page4InformationPackage x => Evidence(x.CoachingHistory), Page5InformationPackage x => Evidence(x.ProgramHistory), Page6InformationPackage x => Evidence(x.TeamStatistics), Page7InformationPackage x => Evidence(x.EditorialDisposition), Page8InformationPackage x => Evidence(x.MindenRoster), Page9InformationPackage x => Evidence(x.OpponentRoster), _ => []
    };
    private static IReadOnlyList<PublicationEvidenceReference> Many(params AcceptedSupplementalSection?[] sections) => sections.Where(x => x is not null).SelectMany(x => Evidence(x)).Distinct().ToList();
    private static IReadOnlyList<PublicationEvidenceReference> Evidence(AcceptedSupplementalSection? authority) => authority?.Evidence.Select(x => new PublicationEvidenceReference(x.Id, x.Kind, x.ExpectedDocumentId, x.ImportRecordId, x.SourceFamilyId)).ToList() ?? [];
    private static StatOfWeekPublicationSupply Stat(AcceptedPublicationSection<Page1WeeklyFactsPayload>? section)
    {
        var selection = section?.Content.StatOfWeekSelection;
        if (selection is null) return new(PublicationStatOfWeekState.Unresolved, null, [], null);
        var state = selection.Disposition == StatOfWeekDisposition.Selected ? PublicationStatOfWeekState.Selected : PublicationStatOfWeekState.NoSelection;
        var facts = state == PublicationStatOfWeekState.Selected ? section!.Content.StatsOfWeek.Where(x => selection.SupportingFactEvidenceIds.Contains(x.EvidenceId)).ToList() : [];
        var editorial = section!.Evidence.SingleOrDefault(x => x.EvidenceId == selection.EditorialEvidenceId && x.Kind == SupplementalEvidenceKind.EditorialDecision);
        return new(state, selection, facts, editorial);
    }
}

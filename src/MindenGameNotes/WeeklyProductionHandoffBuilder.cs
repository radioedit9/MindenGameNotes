using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MindenGameNotes;

public static class WeeklyProductionHandoffBuilder
{
    private const string Page1LateRequirement = "P1.Page1WeeklyFacts";

    public static WeeklyProductionHandoff Build(WeeklyGameNotesInformationPackage package)
    {
        var pages = package.Pages.Select(page => BuildPage(package, page)).ToList();
        var blockers = package.Requirements.Where(IsPublicationBlocking).ToList();
        var advisories = package.Requirements.Where(x => x.Severity == ReadinessSeverity.Advisory).ToList();
        return new(package, pages, pages.All(x => x.State == PageProductionState.PublicationReady) && blockers.Count == 0, blockers, advisories, DateTime.UtcNow);
    }

    public static WeeklyProductionComparison Compare(WeeklyProductionHandoff previous, WeeklyProductionHandoff current)
    {
        if (previous.InformationPackage.ProjectId != current.InformationPackage.ProjectId) throw new InvalidOperationException("Production handoffs must belong to the same weekly project.");
        var changes = new List<PageProductionChange>();
        foreach (var currentPage in current.Pages.OrderBy(x => x.PageNumber))
        {
            var previousPage = previous.Pages.Single(x => x.PageNumber == currentPage.PageNumber);
            if (previousPage.Fingerprint == currentPage.Fingerprint) continue;
            var keys = previousPage.RequirementFingerprints.Keys.Concat(currentPage.RequirementFingerprints.Keys).Distinct(StringComparer.Ordinal).Where(key => !previousPage.RequirementFingerprints.TryGetValue(key, out var before) || !currentPage.RequirementFingerprints.TryGetValue(key, out var after) || before != after).OrderBy(x => x, StringComparer.Ordinal).ToList();
            changes.Add(new(currentPage.PageNumber, previousPage.Fingerprint, currentPage.Fingerprint, keys));
        }
        return new(previous, current, changes);
    }

    private static PageProductionStatus BuildPage(WeeklyGameNotesInformationPackage package, IPageInformationPackage page)
    {
        var workBlockers = page.Requirements.Where(x => IsPublicationBlocking(x) && IsWorkBlocking(x)).ToList();
        var publicationBlockers = page.Requirements.Where(IsPublicationBlocking).ToList();
        var advisories = page.Requirements.Where(x => x.Severity == ReadinessSeverity.Advisory).ToList();
        var state = workBlockers.Count != 0 ? PageProductionState.Waiting : publicationBlockers.Count != 0 ? PageProductionState.ProductionUsable : PageProductionState.PublicationReady;
        var requirementFingerprints = page.Requirements.ToDictionary(x => x.RequirementKey, RequirementFingerprint, StringComparer.Ordinal);
        if (page.PageNumber == 1)
        {
            var p = package.Page1.WeeklyProject;
            var identity = new StringBuilder().Append(p.Id).Append('|').Append(p.Season).Append('|').Append(p.Week).Append('|').Append(Clean(p.Opponent)).Append('|').Append(p.GameDate?.ToString("O", CultureInfo.InvariantCulture)).Append('|').Append(p.KickoffTime?.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)).Append('|').Append(Clean(p.Venue));
            requirementFingerprints["P1.ProjectIdentity"] = Hash(requirementFingerprints["P1.ProjectIdentity"] + "|identity:" + identity);
        }
        var canonical = new StringBuilder().Append("page=").Append(page.PageNumber).Append('\n');
        foreach (var item in requirementFingerprints.OrderBy(x => x.Key, StringComparer.Ordinal)) canonical.Append(item.Key).Append('=').Append(item.Value).Append('\n');
        return new(page.PageNumber, page.Purpose, page, state, workBlockers, publicationBlockers, advisories, requirementFingerprints, Hash(canonical.ToString()));
    }

    private static bool IsPublicationBlocking(InformationRequirementStatus requirement) => requirement.Disposition == RequirementDisposition.Required && requirement.Severity == ReadinessSeverity.Blocking;
    private static bool IsWorkBlocking(InformationRequirementStatus requirement) => requirement.RequirementKey != Page1LateRequirement;

    private static string RequirementFingerprint(InformationRequirementStatus requirement)
    {
        if (requirement.Authorities.Count == 0 && IsPublicationBlocking(requirement))
        {
            return Hash($"{requirement.RequirementKey}|{requirement.Disposition}|unresolved|{requirement.Severity}");
        }
        var canonical = new StringBuilder().Append(requirement.RequirementKey).Append('|').Append(requirement.Disposition).Append('|').Append(requirement.Availability).Append('|').Append(requirement.Severity).Append('|').Append(Clean(requirement.Message));
        foreach (var authority in requirement.Authorities.OrderBy(x => x.Domain).ThenBy(x => x.AuthorityId)) canonical.Append("|a:").Append(authority.Domain).Append(':').Append(authority.AuthorityId).Append(':').Append(authority.StagedAuthorityId).Append(':').Append(authority.ImportRecordId).Append(':').Append(authority.ExpectedDocumentId).Append(':').Append(authority.SourceFamilyId).Append(':').Append(authority.AcceptedUtc?.ToString("O", CultureInfo.InvariantCulture));
        foreach (var document in requirement.ExpectedDocumentIds.Order()) canonical.Append("|d:").Append(document);
        return Hash(canonical.ToString());
    }

    private static string Clean(string? value) => (value ?? "").Replace("\r", "", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

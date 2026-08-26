using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace MindenGameNotes;

public sealed class GovernedTypographyPreflightException(string message, ResolvedLayoutDiagnosticCode diagnosticCode) : InvalidOperationException(message)
{
    public ResolvedLayoutDiagnosticCode DiagnosticCode { get; } = diagnosticCode;
}

public sealed class GovernedTypographyMetrics
{
    public const string AlgorithmVersion = "WP7.1-PHYSICAL-GLYPH-METRICS-V1";
    private readonly Dictionary<(string Family, bool Bold), Face> faces = [];

    public static GovernedTypographyMetrics Create(PublicationDesignContract design, IEnumerable<TypographyRole> requiredRoles)
    {
        var result = new GovernedTypographyMetrics();
        var roles = requiredRoles.ToHashSet();
        foreach (var type in design.Typography.Where(x => roles.Contains(x.Role)).GroupBy(x => (x.Family, x.Bold)).Select(x => x.First()))
            result.faces[(type.Family, type.Bold)] = Face.Load(type.Family, type.Bold);
        return result;
    }

    public IReadOnlyList<ResolvedFontIdentity> Identities => faces.Values.Select(x => x.Identity).OrderBy(x => x.Family, StringComparer.Ordinal).ThenBy(x => x.Weight, StringComparer.Ordinal).ToList().AsReadOnly();
    public ResolvedFontIdentity Identity(string family, bool bold) => Get(family, bold).Identity;
    public bool Supports(string family, bool bold, char value) => Get(family, bold).GlyphTypeface.CharacterToGlyphMap.ContainsKey(value);
    public double Measure(string value, double points, string family, bool bold)
    {
        var face = Get(family, bold); var em = 0d;
        foreach (var rune in value.EnumerateRunes())
        {
            if (!face.GlyphTypeface.CharacterToGlyphMap.TryGetValue(rune.Value, out var glyph)) throw new GovernedTypographyPreflightException($"The governed face '{family}' does not contain U+{rune.Value:X4}.", ResolvedLayoutDiagnosticCode.UnsupportedGlyph);
            face.RequiredCodePoints.Add(rune.Value);
            em += face.GlyphTypeface.AdvanceWidths[glyph];
        }
        return Round(em * points);
    }
    public double Ascent(double points, string family, bool bold) => Round(Get(family, bold).GlyphTypeface.Baseline * points);
    public double Descent(double points, string family, bool bold) => Round((Get(family, bold).GlyphTypeface.Height - Get(family, bold).GlyphTypeface.Baseline) * points);
    public static (double Ascent, double Descent, string FileSha256) PhysicalExtents(double points, string family, bool bold)
    {
        var face = Face.Load(family, bold);
        return (Round(face.GlyphTypeface.Baseline * points), Round((face.GlyphTypeface.Height - face.GlyphTypeface.Baseline) * points), face.Identity.FileSha256);
    }
    public static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
    public static IReadOnlyList<ResolvedLayoutDiagnostic> ValidateExpectedIdentities(IReadOnlyList<ResolvedFontIdentity> expected)
    {
        var diagnostics = new List<ResolvedLayoutDiagnostic>();
        foreach (var contract in expected)
        {
            try
            {
                var actual = Face.Load(contract.Family, string.Equals(contract.Weight, "Bold", StringComparison.OrdinalIgnoreCase)).Identity;
                if (actual with { RequiredCodePoints = contract.RequiredCodePoints } != contract) diagnostics.Add(new(0, "Fonts", ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.FontIdentityMismatch, $"Physical font identity for '{contract.Family}' does not match the resolved layout contract."));
            }
            catch (Exception ex) { diagnostics.Add(new(0, "Fonts", ResolvedLayoutSeverity.Blocking, ResolvedLayoutDiagnosticCode.FontIdentityMismatch, ex.Message)); }
        }
        return diagnostics.AsReadOnly();
    }
    private Face Get(string family, bool bold) => faces.TryGetValue((family, bold), out var face) ? face : throw new InvalidOperationException($"Required governed font '{family}' ({(bold ? "Bold" : "Regular")}) was not preflighted.");

    private sealed class Face
    {
        private Face(GlyphTypeface glyphTypeface, string family, string subfamily, string style, string weight, string stretch, string fileSha256, string metricIdentity) { GlyphTypeface = glyphTypeface; Family = family; Subfamily = subfamily; Style = style; Weight = weight; Stretch = stretch; FileSha256 = fileSha256; MetricIdentity = metricIdentity; }
        public GlyphTypeface GlyphTypeface { get; }
        public HashSet<int> RequiredCodePoints { get; } = [];
        private string Family { get; } private string Subfamily { get; } private string Style { get; } private string Weight { get; } private string Stretch { get; } private string FileSha256 { get; } private string MetricIdentity { get; }
        public ResolvedFontIdentity Identity => new(Family, Subfamily, Style, Weight, Stretch, 0, FileSha256, MetricIdentity, RequiredCodePoints.Order().ToList().AsReadOnly());
        public static Face Load(string family, bool bold)
        {
            var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyph)) throw new GovernedTypographyPreflightException($"Required physical font '{family}' ({(bold ? "Bold" : "Regular")}) is unavailable; fallback is prohibited.", ResolvedLayoutDiagnosticCode.RequiredFontUnavailable);
            var actualBold = glyph.Weight.ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight();
            if (actualBold != bold || glyph.Style != FontStyles.Normal || !string.Equals(glyph.FamilyNames.Values.FirstOrDefault(), family, StringComparison.OrdinalIgnoreCase)) throw new GovernedTypographyPreflightException($"Resolved font face for '{family}' is not the required physical face.", ResolvedLayoutDiagnosticCode.RequiredFontUnavailable);
            if (!glyph.FontUri.IsFile) throw new GovernedTypographyPreflightException($"Required physical font '{family}' is not backed by a verifiable local binary.", ResolvedLayoutDiagnosticCode.RequiredFontUnavailable);
            var path = glyph.FontUri.LocalPath; var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var metric = new StringBuilder(); metric.Append(AlgorithmVersion).Append('|').Append(glyph.Baseline.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(glyph.Height.ToString("R", CultureInfo.InvariantCulture)).Append('|');
            foreach (var mapping in glyph.CharacterToGlyphMap.OrderBy(x => x.Key)) metric.Append(mapping.Key).Append(':').Append(mapping.Value).Append(':').Append(glyph.AdvanceWidths[mapping.Value].ToString("R", CultureInfo.InvariantCulture)).Append('|');
            return new(glyph, family, glyph.FaceNames.Values.FirstOrDefault() ?? (bold ? "Bold" : "Regular"), glyph.Style.ToString(), glyph.Weight.ToString(), glyph.Stretch.ToString(), hash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(metric.ToString()))));
        }
    }
}

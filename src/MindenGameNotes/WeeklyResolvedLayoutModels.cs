namespace MindenGameNotes;

public enum ResolvedPrimitiveLayer { Background = 0, Rules = 100, Content = 200, Overlay = 300 }
public enum ResolvedPrimitiveKind { Text, Rule, TableCell }
public enum ResolvedLayoutSeverity { Advisory, Blocking }
public enum ResolvedLayoutDiagnosticCode
{
    ParentCompositionNotRenderReady, RequiredFontUnavailable, FontIdentityMismatch, UnsupportedGlyph,
    UnsupportedSemanticRole, MinimumSizeFittingFailure, UnbreakableValue, VerticalOverflow, TableOverflow,
    CellOverflow, PrimitiveOutsideRegion, InvalidClipping, InvalidBaseline, DuplicatePrimitiveKey,
    ProjectionCompleteness, ContentLoss, ContentDuplication, NondeterministicPrimitiveOrder
}

public sealed record ResolvedFontIdentity(
    string Family, string Subfamily, string Style, string Weight, string Stretch, int FaceIndex,
    string FileSha256, string MetricIdentity, IReadOnlyList<int> RequiredCodePoints);

public sealed record ResolvedSourceSpan(int Start, int Length);
public sealed record ResolvedTextLine(string Text, ResolvedSourceSpan SourceSpan, double MeasuredAdvance, double BaselineX, double BaselineY);

public interface IResolvedLayoutPrimitive
{
    string Key { get; }
    ResolvedPrimitiveKind Kind { get; }
    ResolvedPrimitiveLayer Layer { get; }
    int Sequence { get; }
    CompositionRect Bounds { get; }
    CompositionRect ClipBounds { get; }
    string GovernedRegionKey { get; }
    CompositionRect GovernedRegionBounds { get; }
}

public sealed record ResolvedTextPrimitive(
    string Key, string SourceText, IReadOnlyList<ResolvedTextLine> Lines, string FontFamily,
    string FontFileSha256, double FontPoints, double Leading, bool Bold, TypographyRole Typography,
    CompositionAlignment Alignment, CompositionRect Bounds, CompositionRect ClipBounds,
    double HorizontalInset, double VerticalInset, string GovernedRegionKey, CompositionRect GovernedRegionBounds,
    ResolvedPrimitiveLayer Layer, int Sequence) : IResolvedLayoutPrimitive
{
    public ResolvedPrimitiveKind Kind => ResolvedPrimitiveKind.Text;
}

public sealed record ResolvedRulePrimitive(
    string Key, RuleRole Role, double Thickness, double X1, double Y1, double X2, double Y2,
    CompositionRect PaintedBounds, CompositionRect ClipBounds, string GovernedRegionKey, CompositionRect GovernedRegionBounds,
    ResolvedPrimitiveLayer Layer, int Sequence) : IResolvedLayoutPrimitive
{
    public ResolvedPrimitiveKind Kind => ResolvedPrimitiveKind.Rule;
    public CompositionRect Bounds => PaintedBounds;
}

public sealed record ResolvedTableCellPrimitive(
    string Key, string TableKey, int RowIndex, int ColumnIndex, string ColumnIdentity,
    ResolvedTextPrimitive Text, double HorizontalInset, double VerticalInset,
    CompositionRect Bounds, CompositionRect ClipBounds, string GovernedRegionKey, CompositionRect GovernedRegionBounds,
    ResolvedPrimitiveLayer Layer, int Sequence) : IResolvedLayoutPrimitive
{
    public ResolvedPrimitiveKind Kind => ResolvedPrimitiveKind.TableCell;
}

public sealed record ResolvedLayoutDiagnostic(int PageNumber, string Region, ResolvedLayoutSeverity Severity, ResolvedLayoutDiagnosticCode Code, string Message);

public sealed record ResolvedPageLayout(
    int PageNumber, CompositionRect PageBounds,
    CompositionRect BodyBounds, IReadOnlyList<IResolvedLayoutPrimitive> Primitives,
    IReadOnlyList<ResolvedLayoutDiagnostic> Diagnostics)
{
    public bool IsRenderReady => Diagnostics.All(x => x.Severity != ResolvedLayoutSeverity.Blocking);
}

public sealed record ResolvedWeeklyGameNotesLayout(
    string ParentCompositionIdentity, string MetricAlgorithmVersion,
    IReadOnlyList<ResolvedFontIdentity> Fonts, IReadOnlyList<ResolvedPageLayout> Pages,
    IReadOnlyList<ResolvedLayoutDiagnostic> Diagnostics, string SemanticIdentity)
{
    public bool IsRenderReady => Pages.Count == 10 && Pages.All(x => x.IsRenderReady) && Diagnostics.All(x => x.Severity != ResolvedLayoutSeverity.Blocking);
}

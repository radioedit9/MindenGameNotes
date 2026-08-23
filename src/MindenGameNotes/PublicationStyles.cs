namespace MindenGameNotes;

public enum PublicationFill { White, LightGray, DarkGray, Black }

public static class PublicationStyles
{
    // Values are intentionally separated enough to survive ordinary monochrome laser printing.
    public static double Gray(PublicationFill fill) => fill switch
    {
        PublicationFill.Black => 0,
        PublicationFill.DarkGray => .28,
        PublicationFill.LightGray => .88,
        _ => 1
    };
    public const double StrongRule = 1.2;
    public const double NormalRule = .65;
    public const double LightRule = .28;
}

namespace MindenGameNotes;

public static class PageGeometry
{
    public const double WidthPoints = 612;
    public const double HeightPoints = 792;
    public const double MarginPoints = 11;
    public const double UsableRight = WidthPoints - MarginPoints;
    public const double UsableBottom = HeightPoints - MarginPoints;
}

public sealed record LayoutBound(string Name,double X,double Y,double Width,double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public static class PageOneBoundsValidator
{
    public static void Validate(GameNotesProject project)
    {
        var required = PageOneRenderer.RequiredContainers();
        var expected = new[] { "Game Information", "Minden Quick Facts", "Tonight's Opponent", "Series History", "A Tide Win Tonight Would", "Looking Back", "Stat of the Week", "Minden Leaders", "By the Numbers", "Footer" };
        foreach (var name in expected) if (!required.Any(x => x.Name == name)) throw new InvalidOperationException($"Required Page 1 container is missing: {name}.");
        foreach (var b in required)
        {
            if (b.X < PageGeometry.MarginPoints-.01 || b.Y < 0 || b.Right > PageGeometry.UsableRight+.01 || b.Bottom > PageGeometry.UsableBottom+.01)
                throw new InvalidOperationException($"Page 1 bounds failure: {b.Name} [{b.X},{b.Y},{b.Right},{b.Bottom}] exceeds the 612×792 MediaBox printable canvas [{PageGeometry.MarginPoints},0,{PageGeometry.UsableRight},{PageGeometry.UsableBottom}].");
        }
        foreach (var item in PageOneRenderer.Compose(project))
        {
            var b = item switch
            {
                Box x => new LayoutBound("box",x.X,x.Y,x.Width,x.Height),
                Rule x => new LayoutBound("rule",Math.Min(x.X1,x.X2),Math.Min(x.Y1,x.Y2),Math.Abs(x.X2-x.X1),Math.Abs(x.Y2-x.Y1)),
                Label x => new LayoutBound("text",x.X,x.Y,x.Width,x.Size+Math.Max(0,x.Value.Count(c=>c=='\n'))*(x.Size+2)),
                ImageMark x => new LayoutBound("logo",x.X,x.Y,x.Width,x.Height),
                _ => null
            };
            if (b is not null && (b.X < 0 || b.Y < 0 || b.Right > PageGeometry.WidthPoints+.01 || b.Bottom > PageGeometry.HeightPoints+.01))
                throw new InvalidOperationException($"Page 1 element bounds failure: {b.Name} [{b.X},{b.Y},{b.Right},{b.Bottom}] exceeds MediaBox.");
        }
    }
}

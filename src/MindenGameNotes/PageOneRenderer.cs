namespace MindenGameNotes;

public abstract record PageElement;
public sealed record Box(double X, double Y, double Width, double Height, double Stroke = .7, bool FillBlack = false, double Radius = 0, PublicationFill Fill = PublicationFill.White) : PageElement;
public sealed record Rule(double X1, double Y1, double X2, double Y2, double Width = .5) : PageElement;
public sealed record Label(string Value, double X, double Y, double Width, double Size, bool Bold = false, TextAlign Align = TextAlign.Left, double Condense = 100, bool White = false, bool BodyBold = false) : PageElement;
public sealed record ImageMark(string Path, double X, double Y, double Width, double Height) : PageElement;
public enum TextAlign { Left, Center, Right }

public static class PageOneRenderer
{
    private const double M = 11;
    public static List<PageElement> Compose(GameNotesProject p)
    {
        var d = p.PageOne ??= new();
        var e = new List<PageElement>();
        var logo = FindLogo();
        if (logo is not null) e.Add(new ImageMark(logo, 15, 5, 98, 78));
        else e.Add(new Label("M", 18, 10, 92, 58, true, TextAlign.Center, 78));
        e.Add(new Label("MINDEN HIGH SCHOOL", 125, 9, 370, 29, true, TextAlign.Left, 96));
        e.Add(new Label("GAME NOTES", 125, 46, 370, 31, true, TextAlign.Left, 90));
        e.Add(new Box(505, 8, 96, 73, 1.2)); e.Add(new Box(509, 12, 88, 25, 0, true));
        e.Add(new Label($"WEEK {d.Week}", 509, 16, 88, 13, true, TextAlign.Center, 87, true));
        e.Add(new Label("2026", 509, 42, 88, 22, true, TextAlign.Center, 90));
        e.Add(new Label("SEASON", 509, 65, 88, 11, true, TextAlign.Center, 90));
        Bar(e, 11, 88, 590, 25, $"MINDEN CRIMSON TIDE ({d.MindenRecord})  at  {d.OpponentTeam} ({d.OpponentRecord})", 15);
        Bar(e, 11, 113, 590, 20, $"{p.GameDate:dddd, MMMM d, yyyy}   •   {d.Kickoff}   •   {p.Venue.Replace("\r", "").Replace("\n", " • ")}", 10);

        // Locked three-column upper dashboard.
        const double y1 = 139, h1 = 213; Section(e, 11, y1, 208, h1, "GAME INFORMATION");
        Rows(e, 17, y1 + 31, 197, 15, [
            ("Date:", p.GameDate.ToString("dddd, MMMM d, yyyy")), ("Kickoff:", d.Kickoff), ("Site:", p.Venue),
            ("Radio:", d.Radio), ("Internet:", d.Internet), ("Weather:", d.Weather)
        ], 54, 30);
        Section(e, 228, y1, 174, h1, "MINDEN QUICK FACTS");
        Rows(e, 235, y1 + 31, 160, 14, [
            ("Location:", "Minden, LA"), ("Founded:", "1897"), ("Enrollment:", d.EnrollmentVerified ? d.Enrollment.Replace(" [VERIFY]", "") : d.Enrollment),
            ("Mascot:", "Crimson Tide"), ("Colors:", "Crimson & White"), ("Class / District:", "4A / 1-4A"), ("Stadium:", "W.W. Williams Stadium (\"The Pit\")"),
            ("2025 Record:", "5-6"), ("Postseason:", "Bi-District"), ("Head Coach:", "Spencer Heard"), ("Minden Record:", d.CoachMindenRecord), ("Years at Minden:", "14th Season")
        ], 78, 23);
        Section(e, 411, y1, 190, h1, "TONIGHT’S OPPONENT");
        e.Add(new Label(string.Join("\n", Wrap(d.OpponentTeam, 17)), 420, y1 + 29, 172, 17, true, TextAlign.Center, 88));
        Rows(e, 418, y1 + 73, 176, 14, [("Location:", d.OpponentLocation), ("Class / District:", d.OpponentClassDistrict), ("Head Coach:", d.OpponentCoach), ("Years at NW:", d.OpponentCoachTenure), ("2025 Record:", d.OpponentPriorRecord), ("Postseason:", d.OpponentPostseason)], 78, 22);
        e.Add(FeaturePanel(418, y1 + 163, 176, 42)); e.Add(new Label("LAST GAME vs. MINDEN", 422, y1 + 169, 168, 10, true, TextAlign.Center));
        var lm = d.OpponentLastMeeting.Split('|'); e.Add(new Label(lm.ElementAtOrDefault(0) ?? "", 422, y1 + 183, 168, 8, true, TextAlign.Center)); e.Add(new Label(lm.ElementAtOrDefault(1) ?? "", 422, y1 + 194, 168, 8, true, TextAlign.Center));

        // Middle row; order and dimensions follow the authority.
        const double y2 = 359, h2 = 252; Section(e, 11, y2, 184, h2, "SERIES HISTORY");
        PairLines(e, 17, y2 + 31, 172, d.SeriesHistory, 88, 16);
        Section(e, 203, y2, 178, h2, "A TIDE WIN TONIGHT WOULD…");
        BulletLines(e, 211, y2 + 31, 162, d.WinTonightWould, 13);
        e.Add(FeaturePanel(210, y2 + 150, 164, 94)); e.Add(new Rule(218,y2+173,366,y2+173,PublicationStyles.NormalRule)); e.Add(new Label("STAT OF THE WEEK", 214, y2 + 157, 156, 11, true, TextAlign.Center));
        var sy = y2 + 176; foreach (var stat in d.StatsOfWeek.Take(3)) { var a = stat.Split('|');if(a.Length>1){e.Add(new Label(a[0],216,sy,152,20,true,TextAlign.Center,92));sy+=22;var lines=Wrap(a[1],48).ToList();for(var i=0;i<lines.Count;i++)e.Add(new Label(lines[i],217,sy+i*8,150,7,false,TextAlign.Center));sy+=lines.Count*8+5;}else{var lines=Wrap(a[0],38).ToList();for(var i=0;i<lines.Count;i++)e.Add(new Label(lines[i],217,sy+i*8,150,7.5,i==0,TextAlign.Center));sy+=lines.Count*8+5;} }
        Section(e, 389, y2, 212, h2, "LOOKING BACK");
        e.Add(new Label(d.LookingBackTitle, 395, y2 + 27, 200, 13, true, TextAlign.Center, 88)); e.Add(new Label(d.LookingBackSubhead, 395, y2 + 43, 200, 8, true, TextAlign.Center));
        ScoreTable(e, 395, y2 + 56, 200, d.LookingBackScores);
        Wrapped(e, d.LookingBackSummary, 396, y2 + 109, 198, 7.5, 55, 10);
        SecondaryBar(e, 395, y2 + 154, 200, 15, "MINDEN LEADERS", 9);
        PairLines(e, 395, y2 + 171, 198, d.MindenLeaders, 55, 9, true, true);

        // Full-width bottom module.
        const double y3 = 618, h3 = 143; Section(e, 11, y3, 590, h3, "BY THE NUMBERS");
        e.Add(FeaturePanel(34, y3 + 29, 104, 98)); SecondaryBar(e, 34, y3 + 29, 104, 17, "2025 FINAL", 10);
        e.Add(new Label("MINDEN", 38, y3 + 58, 96, 9, true, TextAlign.Center)); e.Add(new Label(d.PriorSeasonRecord, 38, y3 + 73, 96, 23, true, TextAlign.Center)); e.Add(new Label(d.PriorSeasonPostseason, 38, y3 + 101, 96, 7.5, false, TextAlign.Center));
        e.Add(new Label("MINDEN (2025)", 166, y3 + 28, 286, 8, true, TextAlign.Center)); var ny = y3 + 45;
        foreach (var row in d.ByTheNumbers.Take(7)) { var a = row.Split('|'); e.Add(new Rule(166, ny + 10, 452, ny + 10, PublicationStyles.LightRule)); e.Add(new Label(a[0], 172, ny, 65, 8, true, TextAlign.Center)); e.Add(new Label(a.ElementAtOrDefault(1) ?? "", 238, ny, 210, 8, false, TextAlign.Center, 90, false, true)); ny += 12; }
        e.Add(FeaturePanel(470, y3 + 20, 110, 108)); SecondaryBar(e, 470, y3 + 20, 110, 17, "SERIES EXTREMES", 9);
        Extreme(e, "Largest Minden Win", d.LargestMindenWin, 475, y3 + 44); Extreme(e, "Largest North Webster Win", d.LargestOpponentWin, 475, y3 + 87);
        // Page 2-style universal footer.
        e.Add(new Rule(M, 778, 163, 778, 1.4)); e.Add(new Rule(449, 778, 601, 778, 1.4));
        e.Add(new Label("MINDEN HIGH SCHOOL GAME NOTES  •  PAGE 1", 166, 772, 280, 9, true, TextAlign.Center, 88));
        return e;
    }

    public static IReadOnlyList<LayoutBound> RequiredContainers() =>
    [
        new("Game Information",11,139,208,213), new("Minden Quick Facts",228,139,174,213), new("Tonight's Opponent",411,139,190,213),
        new("Series History",11,359,184,252), new("A Tide Win Tonight Would",203,359,178,252), new("Looking Back",389,359,212,252),
        new("Stat of the Week",210,509,164,94), new("Minden Leaders",395,513,200,90), new("By the Numbers",11,618,590,143),
        new("Footer",11,772,590,9)
    ];

    private static string? FindLogo()
    {
        var roots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var root in roots)
        {
            var dir = new DirectoryInfo(root);
            for (var i = 0; dir is not null && i < 7; i++, dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "references", "Tide-No Background.png");
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private static void Section(List<PageElement> e, double x, double y, double w, double h, string title) { e.Add(new Box(x, y, w, h, PublicationStyles.NormalRule)); Bar(e, x, y, w, 20, title, 10); }
    private static void Bar(List<PageElement> e, double x, double y, double w, double h, string text, double size) { e.Add(new Box(x, y, w, h, 0, true)); e.Add(new Label(text, x + 4, y + (h - size) / 2 - 1, w - 8, size, true, TextAlign.Center, 90, true)); }
    private static void SecondaryBar(List<PageElement> e,double x,double y,double w,double h,string text,double size){e.Add(new Box(x,y,w,h,0,false,0,PublicationFill.DarkGray));e.Add(new Label(text,x+4,y+(h-size)/2-1,w-8,size,true,TextAlign.Center,90,true));}
    private static Box FeaturePanel(double x,double y,double w,double h)=>new(x,y,w,h,PublicationStyles.StrongRule,false,0,PublicationFill.LightGray);
    private static void Rows(List<PageElement> e, double x, double y, double w, double step, IEnumerable<(string, string)> rows, double keyWidth, int maxChars) { foreach (var (k, v) in rows) { e.Add(new Label(k, x, y, keyWidth-2, 8.2, false, TextAlign.Left, 100, false, true));var lines=Wrap(v,maxChars).ToList();for(var i=0;i<lines.Count;i++)e.Add(new Label(lines[i],x+keyWidth,y+i*9.5,w-keyWidth,8.2));y+=Math.Max(step,lines.Count*9.5+4); } }
    private static void PairLines(List<PageElement> e, double x, double y, double w, IEnumerable<string> rows, double keyWidth, double step, bool rules = false, bool striped = false) { var index=0;foreach (var row in rows) { var a = row.Split('|');var valueWidth=w-keyWidth-(a.Length>2?35:0);var lines=Wrap(a.ElementAtOrDefault(1)??"",Math.Max(12,(int)(valueWidth/4))).ToList();var used=Math.Max(step,lines.Count*9+3);if(striped&&index%2==0)e.Add(new Box(x,y-1,w,used,0,false,0,PublicationFill.LightGray));e.Add(new Label(a[0], x, y, keyWidth-3, 8, false, TextAlign.Left, 100, false, true));for(var i=0;i<lines.Count;i++)e.Add(new Label(lines[i],x+keyWidth,y+i*9,valueWidth,8));if(a.Length>2)e.Add(new Label(a[2],x+w-40,y,40,8,false,TextAlign.Right));if(rules)e.Add(new Rule(x,y+used-2,x+w,y+used-2,PublicationStyles.LightRule));y+=used;index++;} }
    private static void BulletLines(List<PageElement> e, double x, double y, double w, IEnumerable<string> items, double step) { foreach (var item in items) { e.Add(new Label("•", x, y, 10, 8.2, false, TextAlign.Left, 100, false, true)); var lines = Wrap(item, 34).ToList(); for (int i = 0; i < lines.Count; i++) e.Add(new Label(lines[i], x + 12, y + i * 9, w - 12, 8)); y += Math.Max(step, lines.Count * 9 + 3); } }
    private static void Wrapped(List<PageElement> e, string text, double x, double y, double w, double size, int chars, double step) { foreach (var line in Wrap(text, chars)) { e.Add(new Label(line, x, y, w, size)); y += step; } }
    private static IEnumerable<string> Wrap(string text, int max)
    {
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + word.Length + 1 > max) { yield return line; line = word; }
                else line = line.Length == 0 ? word : line + " " + word;
            }
            if (line.Length > 0) yield return line;
        }
    }
    private static void ScoreTable(List<PageElement> e, double x, double y, double w, List<string> rows) { var widths = new[] { 56d, 29, 29, 29, 29, 28 }; var labels = new[] { "", "1ST", "2ND", "3RD", "4TH", "FINAL" }; double xx=x; for(int i=0;i<6;i++){e.Add(new Box(xx,y,widths[i],16,PublicationStyles.LightRule,false,0,i==0?PublicationFill.LightGray:PublicationFill.DarkGray));e.Add(new Label(labels[i],xx,y+4,widths[i],7,true,TextAlign.Center,100,i>0));xx+=widths[i];} var yy=y+16;var ri=0;foreach(var row in rows.Take(2)){var a=row.Split('|');xx=x;for(int i=0;i<6;i++){e.Add(new Box(xx,yy,widths[i],17,PublicationStyles.LightRule,false,0,ri%2==1?PublicationFill.LightGray:PublicationFill.White));e.Add(new Label(a.ElementAtOrDefault(i)??"",xx+2,yy+4,widths[i]-4,7,i>0,i==0?TextAlign.Left:TextAlign.Center,100,false,i==0));xx+=widths[i];}yy+=17;ri++;} }
    private static void Extreme(List<PageElement> e, string title, string raw, double x, double y) { var a=raw.Split('|');e.Add(new Label(title,x,y,100,7,false,TextAlign.Center,100,false,true));e.Add(new Label(a[0],x,y+11,100,20,true,TextAlign.Center));e.Add(new Label(a.ElementAtOrDefault(1)??"",x,y+33,100,7,false,TextAlign.Center)); }
}

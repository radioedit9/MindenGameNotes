using System.Globalization;
using System.Text;

namespace MindenGameNotes;

public static class PdfExporter
{
    public static void Export(string path, GameNotesProject p, TypographyVariant? typography = null)
    {
        PageOneBoundsValidator.Validate(p);
        typography ??= TypographyVariant.Candidates[0];
        var pages = PageComposer.Compose(p);
        var objects = new List<byte[]>();
        void Add(string s) => objects.Add(Encoding.ASCII.GetBytes(s));
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        var bodyFont=new TrueTypeFont(typography.BodyPath);var displayFont=new TrueTypeFont(typography.DisplayPath);EnsureEmbeddable(bodyFont);EnsureEmbeddable(displayFont);
        var logo = GetLogo(p); var pageStart = logo is null ? 11 : 12;
        Add($"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages.Count).Select(i => $"{pageStart + i * 2} 0 R"))}] /Count {pages.Count} >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        var bodyId=AddEmbeddedFont(objects,bodyFont);var displayId=AddEmbeddedFont(objects,displayFont);
        if (logo is not null) objects.Add(ImageObject(logo.Value));
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var content = pageIndex == 0 ? BuildPageOne(p,bodyFont,displayFont) : BuildContent(pages[pageIndex]);
            var imageResources = logo is null ? "" : " /XObject << /Logo 11 0 R >>";
            Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageGeometry.WidthPoints} {PageGeometry.HeightPoints}] /Resources << /Font << /F1 3 0 R /F2 4 0 R /B {bodyId} 0 R /D {displayId} 0 R >>{imageResources} >> /Contents {objects.Count + 2} 0 R >>");
            Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        }
        using var fs = File.Create(path);
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b); }
        Write("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++) { offsets.Add(fs.Position); Write($"{i + 1} 0 obj\n"); fs.Write(objects[i]); Write("\nendobj\n"); }
        var xref = fs.Position; Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets.Skip(1)) Write($"{o:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
    }

    public static void ExportPageOne(string path, GameNotesProject p, TypographyVariant? typography = null)
    {
        PageOneBoundsValidator.Validate(p);
        typography ??= TypographyVariant.Candidates[0];
        var objects = new List<byte[]>(); void Add(string s) => objects.Add(Encoding.ASCII.GetBytes(s));
        var bodyFont=new TrueTypeFont(typography.BodyPath);var displayFont=new TrueTypeFont(typography.DisplayPath);EnsureEmbeddable(bodyFont);EnsureEmbeddable(displayFont);
        var logo=GetLogo(p);var pageId=logo is null?11:12;var contentId=pageId+1;
        Add("<< /Type /Catalog /Pages 2 0 R >>"); Add($"<< /Type /Pages /Kids [{pageId} 0 R] /Count 1 >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"); Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        var bodyId=AddEmbeddedFont(objects,bodyFont);var displayId=AddEmbeddedFont(objects,displayFont);
        if(logo is not null)objects.Add(ImageObject(logo.Value));
        var content = BuildPageOne(p,bodyFont,displayFont);var imageResources=logo is null?"":" /XObject << /Logo 11 0 R >>"; Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageGeometry.WidthPoints} {PageGeometry.HeightPoints}] /Resources << /Font << /F1 3 0 R /F2 4 0 R /B {bodyId} 0 R /D {displayId} 0 R >>{imageResources} >> /Contents {contentId} 0 R >>"); Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream");
        WritePdf(path, objects);
    }

    private static string BuildContent(NotePage page)
    {
        var sb = new StringBuilder("0.063 0.137 0.247 rg 0 0 612 792 re f\n");
        sb.Append("0.62 0.106 0.196 rg 0 722 612 70 re f\n");
        foreach (var line in page.Lines)
        {
            var size = line.Size.ToString("0.##", CultureInfo.InvariantCulture);
            sb.Append($"BT /F1 {size} Tf {line.Color} rg {line.X} {line.Y} Td ({Escape(line.Text)}) Tj ET\n");
        }
        sb.Append("0.62 0.106 0.196 rg 36 32 540 2 re f\n");
        return sb.ToString();
    }
    private static string BuildPageOne(GameNotesProject p,TrueTypeFont bodyFont,TrueTypeFont displayFont)
    {
        var sb = new StringBuilder("1 1 1 rg 0 0 612 792 re f\n0 0 0 RG 0 0 0 rg\n");
        foreach (var item in PageOneRenderer.Compose(p))
        {
            switch (item)
            {
                case Box b:
                    var gray=PublicationStyles.Gray(b.FillBlack?PublicationFill.Black:b.Fill);sb.Append($"{F(gray)} g 0 G {F(b.Stroke)} w ");
                    sb.Append(F(b.X)+" "+F(792-b.Y-b.Height)+" "+F(b.Width)+" "+F(b.Height)+" re "+(b.Stroke>0?"B":"f")+"\n");
                    break;
                case Rule r: sb.Append($"0 0 0 RG {F(r.Width)} w {F(r.X1)} {F(792-r.Y1)} m {F(r.X2)} {F(792-r.Y2)} l S\n"); break;
                case Label l:
                    var lines = l.Value.Split('\n'); for (var li=0; li<lines.Length; li++)
                    {
                        var value=lines[li];var font=l.Bold?displayFont:bodyFont;var estimated=value.Sum(c=>font.Width(c))*l.Size/1000d*l.Condense/100; var dx=l.Align==TextAlign.Center?(l.Width-estimated)/2:l.Align==TextAlign.Right?l.Width-estimated:0;
                        sb.Append($"BT /{(l.Bold?"D":"B")} {F(l.Size)} Tf {F(l.Condense)} Tz {(l.White?"1 1 1":"0 0 0")} rg {(l.BodyBold?"0.22 w 2 Tr ":"")}{F(l.X+dx)} {F(792-l.Y-l.Size-li*(l.Size+2))} Td ({Escape(value)}) Tj{(l.BodyBold?" 0 Tr":"")} ET\n");
                    } break;
                case ImageMark m: sb.Append($"q {F(m.Width)} 0 0 {F(m.Height)} {F(m.X)} {F(792-m.Y-m.Height)} cm /Logo Do Q\n"); break;
            }
        }
        return sb.ToString();
    }
    private static string F(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
    private static (byte[] Bytes,int Width,int Height)? GetLogo(GameNotesProject p)
    {
        var mark=PageOneRenderer.Compose(p).OfType<ImageMark>().FirstOrDefault();return mark is null?null:PageRasterizer.LogoJpeg(mark.Path);
    }
    private static byte[] ImageObject((byte[] Bytes,int Width,int Height) image)
    {
        var header=Encoding.ASCII.GetBytes($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {image.Bytes.Length} >>\nstream\n");var footer=Encoding.ASCII.GetBytes("\nendstream");var result=new byte[header.Length+image.Bytes.Length+footer.Length];Buffer.BlockCopy(header,0,result,0,header.Length);Buffer.BlockCopy(image.Bytes,0,result,header.Length,image.Bytes.Length);Buffer.BlockCopy(footer,0,result,header.Length+image.Bytes.Length,footer.Length);return result;
    }
    private static int AddEmbeddedFont(List<byte[]> objects,TrueTypeFont font)
    {
        var fileId=objects.Count+1;objects.Add(StreamObject(font.Bytes));var descriptorId=objects.Count+1;
        objects.Add(Encoding.ASCII.GetBytes($"<< /Type /FontDescriptor /FontName /{font.PostScriptName} /Flags 32 /FontBBox {font.FontBBox} /ItalicAngle 0 /Ascent {font.Ascender} /Descent {font.Descender} /CapHeight {font.Ascender} /StemV 90 /FontFile2 {fileId} 0 R >>"));var fontId=objects.Count+1;
        objects.Add(Encoding.ASCII.GetBytes($"<< /Type /Font /Subtype /TrueType /BaseFont /{font.PostScriptName} /FirstChar 32 /LastChar 255 /Widths [{font.WidthArray()}] /FontDescriptor {descriptorId} 0 R /Encoding /WinAnsiEncoding >>"));return fontId;
    }
    private static byte[] StreamObject(byte[] bytes){var h=Encoding.ASCII.GetBytes($"<< /Length {bytes.Length} /Length1 {bytes.Length} >>\nstream\n");var f=Encoding.ASCII.GetBytes("\nendstream");var r=new byte[h.Length+bytes.Length+f.Length];Buffer.BlockCopy(h,0,r,0,h.Length);Buffer.BlockCopy(bytes,0,r,h.Length,bytes.Length);Buffer.BlockCopy(f,0,r,h.Length+bytes.Length,f.Length);return r;}
    private static void EnsureEmbeddable(TrueTypeFont font){if((font.EmbeddingFlags&0x0002)!=0)throw new InvalidOperationException($"{font.FamilyName} prohibits document embedding (fsType 0x{font.EmbeddingFlags:X4}).");}
    private static void WritePdf(string path, List<byte[]> objects)
    {
        using var fs=File.Create(path); void Write(string s){var b=Encoding.ASCII.GetBytes(s);fs.Write(b);} Write("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n"); var offsets=new List<long>{0};
        for(var i=0;i<objects.Count;i++){offsets.Add(fs.Position);Write($"{i+1} 0 obj\n");fs.Write(objects[i]);Write("\nendobj\n");} var xref=fs.Position;Write($"xref\n0 {objects.Count+1}\n0000000000 65535 f \n");foreach(var o in offsets.Skip(1))Write($"{o:0000000000} 00000 n \n");Write($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
    }
    private static string Escape(string value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var bytes=Encoding.GetEncoding(1252,EncoderFallback.ReplacementFallback,DecoderFallback.ReplacementFallback).GetBytes(value);var b=new StringBuilder();foreach(var c in bytes){if(c is (byte)'\\' or (byte)'(' or (byte)')')b.Append('\\').Append((char)c);else if(c<32||c>126)b.Append('\\').Append(Convert.ToString(c,8).PadLeft(3,'0'));else b.Append((char)c);}return b.ToString();
    }
}

public sealed record NoteLine(string Text, int X, int Y, int Size = 10, string Color = "1 1 1");
public sealed record NotePage(string Title, List<NoteLine> Lines);

public static class PageComposer
{
    public static List<NotePage> Compose(GameNotesProject p) =>
    [
        Page("GAME NOTES", p, new[] { $"{p.School} {p.TeamName}", $"vs. {p.Opponent}", p.GameDate?.ToString("MMMM d, yyyy") ?? "Date TBD", p.Venue, "", p.Headline, p.Storyline }),
        Page("MATCHUP AT A GLANCE", p, new[] { $"Minden vs. {p.Opponent}", $"Kickoff: {p.KickoffDisplay}", $"Site: {p.Venue}", "", "WEEKLY STORYLINE", p.Storyline }),
        Page("SEASON SCHEDULE", p, p.Schedule.Count == 0 ? new[] { "No schedule imported." } : p.Schedule.Select(g => $"{g.Date:MMM d}   {g.Opponent,-24} {g.Site,-8} {g.Result}")),
        Page("TEAM LEADERS", p, Leaders(p)),
        Page("OFFENSIVE STATISTICS", p, p.Players.OrderByDescending(x => x.PassingYards + x.RushingYards + x.ReceivingYards).Select(x => $"#{x.Number,-3} {x.Name,-25} {x.Position,-4} Total yards {x.TotalYards}")),
        Page("DEFENSIVE STATISTICS", p, p.Players.OrderByDescending(x => x.Tackles).Select(x => $"#{x.Number,-3} {x.Name,-25} {x.Position,-4} Tackles {x.Tackles}")),
        Page("ROSTER", p, p.Players.OrderBy(x => Number(x.Number)).ThenBy(x => x.Name).Select(x => $"#{x.Number,-3} {x.Name,-28} {x.Position,-5} GP {x.Games}")),
        Page("MEDIA INFORMATION", p, new[] { "COACH'S CORNER", p.CoachQuote, "", "MEDIA CONTACT", p.MediaContact, "", $"Prepared {DateTime.Now:MMMM d, yyyy}" })
    ];

    private static NotePage Page(string title, GameNotesProject p, IEnumerable<string> body)
    {
        var lines = new List<NoteLine> { new(p.School.ToUpperInvariant(), 36, 756, 11), new(title, 36, 690, 22) };
        var y = 658;
        foreach (var raw in body.Take(31))
        {
            foreach (var text in Wrap(raw, 82)) { lines.Add(new(text, 36, y, 10, ".12 .12 .12")); y -= 17; }
        }
        lines.Add(new($"MINDEN HIGH SCHOOL  |  {p.TeamName.ToUpperInvariant()}", 36, 16, 8));
        return new(title, lines);
    }
    private static IEnumerable<string> Leaders(GameNotesProject p)
    {
        if (p.Players.Count == 0) return ["No player statistics imported."];
        PlayerStat Top(Func<PlayerStat, int> f) => p.Players.MaxBy(f)!;
        return [$"Passing: {Top(x => x.PassingYards).Name} — {Top(x => x.PassingYards).PassingYards:N0} yards", $"Rushing: {Top(x => x.RushingYards).Name} — {Top(x => x.RushingYards).RushingYards:N0} yards", $"Receiving: {Top(x => x.ReceivingYards).Name} — {Top(x => x.ReceivingYards).ReceivingYards:N0} yards", $"Tackles: {Top(x => x.Tackles).Name} — {Top(x => x.Tackles).Tackles:N0}"];
    }
    private static IEnumerable<string> Wrap(string text, int length) { if (string.IsNullOrEmpty(text)) return [""]; var words = text.Split(' '); var lines = new List<string>(); var line = ""; foreach (var w in words) { if (line.Length + w.Length + 1 > length) { lines.Add(line); line = w; } else line = line.Length == 0 ? w : line + " " + w; } if (line.Length > 0) lines.Add(line); return lines; }
    private static int Number(string s) => int.TryParse(s, out var n) ? n : int.MaxValue;
}

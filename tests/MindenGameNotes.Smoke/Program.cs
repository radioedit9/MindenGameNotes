using MindenGameNotes;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var project = new GameNotesProject { Opponent = "North Webster Knights", GameDate = new DateTime(2026, 9, 4, 19, 0, 0), Venue = "North Webster High School\nBaucum-Farrar Stadium — Springhill, LA" };
        project.Players.Add(new PlayerStat { Number = "7", Name = "Test Player", RushingYards = 123, Tackles = 4, Verified = true });
        if (args.Contains("--proof")) { MakeProof(project); return; }
        var output = Path.Combine(Path.GetTempPath(), $"minden-smoke-{Guid.NewGuid():N}.pdf");
        try
        {
            PdfExporter.Export(output, project); var pdf = File.ReadAllText(output, System.Text.Encoding.Latin1);
            var pages = System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type /Page ").Count;
            if (!pdf.StartsWith("%PDF-1.4") || pages != 8) throw new Exception($"Invalid PDF: page count was {pages}.");
            Console.WriteLine($"PASS: valid {pages}-page PDF ({new FileInfo(output).Length:N0} bytes)");
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }

    private static void MakeProof(GameNotesProject project)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dir = Path.Combine(root, "artifacts", "wp1"); Directory.CreateDirectory(dir);
        foreach(var variant in TypographyVariant.Candidates)
        {
            var pdf=Path.Combine(dir,"Page-1-Final-WP1-Proof.pdf");var png=Path.Combine(dir,"Page-1-Final-WP1-Proof.png");
            PdfExporter.ExportPageOne(pdf,project,variant);PageRasterizer.SavePageOnePng(png,project,150,variant);
            var display=new TrueTypeFont(variant.DisplayPath);var body=new TrueTypeFont(variant.BodyPath);
            Console.WriteLine($"{variant.Id}: {display.FamilyName} fsType=0x{display.EmbeddingFlags:X4} ({display.EmbeddingPermission}); {body.FamilyName} fsType=0x{body.EmbeddingFlags:X4} ({body.EmbeddingPermission})");
        }
        var reference = Path.Combine(root, "references", "Game Notes Page 1.png");var primary=Path.Combine(dir,"Page-1-Final-WP1-Proof.png");if(File.Exists(reference))SaveComparison(reference,primary,Path.Combine(dir,"Page-1-Comparison.png"));
    }

    private static void SaveComparison(string referencePath, string proofPath, string output)
    {
        BitmapImage Load(string p) { var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.UriSource=new Uri(p);b.EndInit();b.Freeze();return b; }
        var a=Load(referencePath);var b=Load(proofPath);const int gap=30;var h=Math.Max(a.PixelHeight,b.PixelHeight);var w=a.PixelWidth+b.PixelWidth+gap;
        var v=new DrawingVisual();using(var dc=v.RenderOpen()){dc.DrawRectangle(Brushes.White,null,new Rect(0,0,w,h));dc.DrawImage(a,new Rect(0,0,a.PixelWidth,a.PixelHeight));dc.DrawImage(b,new Rect(a.PixelWidth+gap,0,b.PixelWidth,b.PixelHeight));}
        var bmp=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bmp.Render(v);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(bmp));using var s=File.Create(output);enc.Save(s);
    }
}

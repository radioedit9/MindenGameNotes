using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MindenGameNotes;

public sealed partial class ImportService
{
    public async Task<int> ImportAsync(string path, GameNotesProject project)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var rows = ext switch
        {
            ".xlsx" => ImportXlsx(path, project),
            ".pdf" => await ImportPdfAsync(path, project),
            _ => throw new NotSupportedException("Choose a .pdf or .xlsx file.")
        };
        project.Imports.Insert(0, new ImportRecord { FileName = Path.GetFileName(path), ImportedUtc = DateTime.UtcNow, Kind = ext[1..].ToUpperInvariant(), RowCount = rows });
        return rows;
    }

    private static int ImportXlsx(string path, GameNotesProject project)
    {
        using var zip = ZipFile.OpenRead(path);
        var strings = new List<string>();
        var shared = zip.GetEntry("xl/sharedStrings.xml");
        if (shared != null)
        {
            using var s = shared.Open();
            strings = XDocument.Load(s).Descendants().Where(x => x.Name.LocalName == "si")
                .Select(si => string.Concat(si.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value))).ToList();
        }
        var sheet = zip.Entries.FirstOrDefault(x => x.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && x.FullName.EndsWith(".xml"))
            ?? throw new InvalidDataException("The workbook has no worksheet.");
        using var stream = sheet.Open();
        var rows = XDocument.Load(stream).Descendants().Where(x => x.Name.LocalName == "row")
            .Select(r => r.Elements().Where(x => x.Name.LocalName == "c").Select(c => Cell(c, strings)).ToList()).Where(r => r.Any(v => v.Length > 0)).ToList();
        if (rows.Count < 2) return 0;
        var headers = rows[0].Select(Normalize).ToList();
        int added = 0;
        foreach (var row in rows.Skip(1))
        {
            string Get(params string[] names) { var i = headers.FindIndex(h => names.Contains(h)); return i >= 0 && i < row.Count ? row[i] : ""; }
            var name = Get("name", "player", "playername");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var p = project.Players.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? new PlayerStat { Name = name };
            p.Number = Get("number", "no", "jersey"); p.Position = Get("position", "pos");
            p.Games = Number(Get("games", "gp")); p.PassingYards = Number(Get("passingyards", "passyds", "passyards"));
            p.RushingYards = Number(Get("rushingyards", "rushyds", "rushyards")); p.ReceivingYards = Number(Get("receivingyards", "recyds", "recyards"));
            p.Tackles = Number(Get("tackles", "tkl")); p.Touchdowns = Number(Get("touchdowns", "td")); p.Verified = false;
            if (!project.Players.Contains(p)) project.Players.Add(p); added++;
        }
        return added;
    }

    private static async Task<int> ImportPdfAsync(string path, GameNotesProject project)
    {
        var exe = FindOnPath("pdftotext.exe") ?? FindOnPath("pdftotext");
        if (exe is null) throw new InvalidOperationException("PDF text extraction requires Poppler's pdftotext utility. Install it or import the mapped Excel workbook.");
        var output = Path.GetTempFileName();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, $"-layout \"{path}\" \"{output}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidDataException("The PDF could not be read.");
            int added = 0;
            foreach (var line in await File.ReadAllLinesAsync(output))
            {
                var m = StatLine().Match(line);
                if (!m.Success) continue;
                project.Players.Add(new PlayerStat { Number = m.Groups[1].Value, Name = m.Groups[2].Value.Trim(), Games = Number(m.Groups[3].Value) }); added++;
            }
            return added;
        }
        finally { File.Delete(output); }
    }

    private static string Cell(XElement c, List<string> strings)
    {
        var value = c.Elements().FirstOrDefault(x => x.Name.LocalName == "v")?.Value ?? c.Descendants().FirstOrDefault(x => x.Name.LocalName == "t")?.Value ?? "";
        return c.Attribute("t")?.Value == "s" && int.TryParse(value, out var i) && i < strings.Count ? strings[i] : value;
    }
    private static int Number(string text) => int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");
    private static string? FindOnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
    [GeneratedRegex(@"^\s*(\d{1,2})\s+([A-Za-z][A-Za-z .,'-]+?)\s+(\d+)\s+")]
    private static partial Regex StatLine();
}

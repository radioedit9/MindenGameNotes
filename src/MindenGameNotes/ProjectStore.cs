using System.Text.Json;

namespace MindenGameNotes;

public sealed class ProjectStore
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindenGameNotes", "project.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task<GameNotesProject> LoadAsync()
    {
        if (!File.Exists(path)) return new GameNotesProject();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<GameNotesProject>(stream, JsonOptions) ?? new GameNotesProject();
    }

    public async Task SaveAsync(GameNotesProject project)
    {
        project.UpdatedUtc = DateTime.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, project, JsonOptions);
        File.Move(temp, path, true);
    }
}

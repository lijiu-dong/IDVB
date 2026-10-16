using System.Text.Json;

namespace IDVBuff.Views;

internal sealed class ShellLayoutMemory
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions ReadJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteJson = new() { WriteIndented = true };
    private static readonly string FilePath = Path.Combine(
        AppDataPaths.RootDirectory,
        "ui-memory.json");

    public bool NavigationCompact { get; set; }
    public bool SurveyProjectsCollapsed { get; set; } = true;
    public string? LastSelectedMapTemplateId { get; set; }

    public static ShellLayoutMemory Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new ShellLayoutMemory();

                return JsonSerializer.Deserialize<ShellLayoutMemory>(
                    File.ReadAllText(FilePath),
                    ReadJson)
                    ?? new ShellLayoutMemory();
            }
            catch
            {
                return new ShellLayoutMemory();
            }
        }
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temporaryPath = $"{FilePath}.tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(this, WriteJson));
                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save UI memory: {exception.Message}");
            }
        }
    }
}

using System.Text.Json;
namespace ZiYin.Service;

public sealed class AuditLog(ServiceSettings settings)
{
    private readonly object gate = new();
    public bool Healthy { get; private set; } = true;
    private readonly string path = Path.Combine(settings.DataDirectory, "audit.jsonl");
    public void Write(string action, string? id = null)
    {
        lock (gate)
        {
            try
            {
            if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024) File.Move(path, path + ".1", true);
            File.AppendAllText(path, JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, action, id }) + Environment.NewLine);
            Healthy = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Healthy = false; }
        }
    }
    public string[] Read()
    {
        lock (gate) return File.Exists(path) ? File.ReadLines(path).TakeLast(200).ToArray() : [];
    }
}

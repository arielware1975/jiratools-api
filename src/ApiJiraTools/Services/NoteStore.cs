using System.Text.Json;
using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

/// <summary>
/// Persistencia de notas por chat en data/notes.json. { chatId: [notas] }
/// </summary>
public sealed class NoteStore
{
    private const string FilePath = "data/notes.json";
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public Dictionary<string, List<UserNote>> LoadAll()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Dictionary<string, List<UserNote>>>(json, JsonOpts) ?? new();
            }
            catch { return new(); }
        }
    }

    public void SaveAll(Dictionary<string, List<UserNote>> all)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all, JsonOpts));
            }
            catch { }
        }
    }

    public List<UserNote> ForChat(long chatId)
    {
        var all = LoadAll();
        return all.TryGetValue(chatId.ToString(), out var list) ? list : new List<UserNote>();
    }

    public UserNote Add(long chatId, string key, string value)
    {
        var all = LoadAll();
        var k = chatId.ToString();
        if (!all.TryGetValue(k, out var list))
        {
            list = new List<UserNote>();
            all[k] = list;
        }
        // Si ya existe la key, actualizar
        var existing = list.FirstOrDefault(n => string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Value = value;
            existing.CreatedAt = DateTime.UtcNow;
            SaveAll(all);
            return existing;
        }
        var note = new UserNote { Key = key, Value = value };
        list.Add(note);
        SaveAll(all);
        return note;
    }

    public bool Remove(long chatId, string keyOrId)
    {
        var all = LoadAll();
        var k = chatId.ToString();
        if (!all.TryGetValue(k, out var list)) return false;
        int before = list.Count;
        list.RemoveAll(n =>
            n.Id.Equals(keyOrId, StringComparison.OrdinalIgnoreCase) ||
            n.Key.Equals(keyOrId, StringComparison.OrdinalIgnoreCase));
        if (list.Count != before)
        {
            SaveAll(all);
            return true;
        }
        return false;
    }

    /// <summary>Busca por substring en la key (case-insensitive). Devuelve todos los matches.</summary>
    public List<UserNote> Search(long chatId, string query)
    {
        var list = ForChat(chatId);
        var q = query.Trim().ToLowerInvariant();
        return list
            .Where(n => n.Key.ToLowerInvariant().Contains(q))
            .OrderBy(n => n.Key)
            .ToList();
    }
}

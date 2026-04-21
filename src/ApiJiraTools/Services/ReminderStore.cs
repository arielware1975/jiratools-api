using System.Text.Json;
using ApiJiraTools.Models;

namespace ApiJiraTools.Services;

/// <summary>
/// Persistencia simple de recordatorios en data/reminders.json.
/// Thread-safe; se usa desde el bot y desde el scheduler.
/// </summary>
public sealed class ReminderStore
{
    private const string FilePath = "data/reminders.json";
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public List<Reminder> LoadAll()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<Reminder>();
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<Reminder>>(json, JsonOpts) ?? new();
            }
            catch { return new List<Reminder>(); }
        }
    }

    public void SaveAll(List<Reminder> reminders)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(reminders, JsonOpts));
            }
            catch { /* no bloquear */ }
        }
    }

    public void Add(Reminder r)
    {
        var list = LoadAll();
        list.Add(r);
        SaveAll(list);
    }

    public bool Remove(string id, long chatId)
    {
        var list = LoadAll();
        int before = list.Count;
        list.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && x.ChatId == chatId);
        if (list.Count != before)
        {
            SaveAll(list);
            return true;
        }
        return false;
    }

    public List<Reminder> ForChat(long chatId)
        => LoadAll().Where(r => r.ChatId == chatId).ToList();

    public void UpdateLastFired(string id, DateTime when)
    {
        var list = LoadAll();
        var r = list.FirstOrDefault(x => x.Id == id);
        if (r != null)
        {
            r.LastFired = when;
            SaveAll(list);
        }
    }
}

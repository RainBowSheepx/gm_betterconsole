using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using BetterConsole.Core.Server;

namespace BetterConsole.App.Services;

/// <summary>One line of the start / stop journal.</summary>
public sealed class JournalEntry
{
    public DateTime Time { get; set; }
    public string ServerId { get; set; } = "";
    public string Server { get; set; } = "";
    /// <summary>started, stopped, crashed, exited, failed.</summary>
    public string Event { get; set; } = "";
    public string Reason { get; set; } = "";
    public int? ExitCode { get; set; }
    public double? UptimeSeconds { get; set; }

    [JsonIgnore] public string TimeText => Time.Date == DateTime.Today ? Time.ToString("HH:mm:ss") : Time.ToString("yyyy-MM-dd HH:mm:ss");
    [JsonIgnore] public string EventText => Event switch
    {
        "started" => "Started",
        "stopped" => "Stopped",
        "crashed" => "Crashed",
        "exited" => "Quit by itself",
        "failed" => "Start failed",
        _ => Event,
    };
    /// <summary>Theme brush of the event.</summary>
    [JsonIgnore] public string ColorKey => Event switch
    {
        "started" => "Brush.Success",
        "crashed" or "failed" => "Brush.Danger",
        "exited" => "Brush.Warning",
        _ => "Brush.TextSecondary",
    };
    [JsonIgnore] public string Icon => Event switch
    {
        "started" => "",
        "stopped" => "",
        "crashed" => "",
        "exited" => "",
        _ => "",
    };
    [JsonIgnore] public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (UptimeSeconds is { } up) parts.Add("up " + ServerController.FormatSpan(TimeSpan.FromSeconds(up)));
            // A crash names its code in the reason already; a normal stop exits with 0.
            if (ExitCode is { } code and not 0 && Event == "stopped") parts.Add("exit code " + ServerController.FormatExitCode(code));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// When the servers started and stopped, and why. Kept in journal.jsonl in the data folder, one JSON
/// object per line; the newest few thousand entries are kept.
/// </summary>
public sealed class Journal
{
    private const int Keep = 5000;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _lock = new();
    private ObservableCollection<JournalEntry>? _entries;
    private bool _listRead;

    public Journal(string file)
    {
        FilePath = file;
        // Keep the file small even when nobody opens the journal.
        Task.Run(TrimIfLarge);
    }

    private void TrimIfLarge()
    {
        lock (_lock)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists || info.Length < 2 * 1024 * 1024) return;
                var lines = File.ReadAllLines(FilePath);
                if (lines.Length > Keep) File.WriteAllLines(FilePath, lines.TakeLast(Keep));
            }
            catch (Exception ex)
            {
                Log.Write("journal trim: " + ex.Message);
            }
        }
    }

    public string FilePath { get; }

    /// <summary>All entries, oldest first (loaded on first use). Changed on the UI thread only.</summary>
    public ObservableCollection<JournalEntry> Entries => _entries ??= new ObservableCollection<JournalEntry>(Load());

    public void Add(string serverId, string serverName, LifecycleEvent e)
    {
        var entry = new JournalEntry
        {
            Time = e.Time,
            ServerId = serverId,
            Server = serverName,
            Event = e.Kind switch
            {
                LifecycleKind.Started => "started",
                LifecycleKind.Stopped => "stopped",
                LifecycleKind.Crashed => "crashed",
                LifecycleKind.Exited => "exited",
                _ => "failed",
            },
            Reason = e.Reason,
            ExitCode = e.ExitCode,
            UptimeSeconds = e.Uptime is { } u ? Math.Round(u.TotalSeconds) : null,
        };
        // Written at once (also while BetterConsole closes). The list gets the entry only when it was
        // read from the file before this append; a list read after it has the entry already.
        bool toList;
        lock (_lock)
        {
            Append(entry);
            toList = _listRead;
        }
        var app = Application.Current;
        if (!toList || app == null) return;
        _ = app.Dispatcher.BeginInvoke(() =>
        {
            if (_entries == null) return;
            _entries.Add(entry);
            while (_entries.Count > Keep) _entries.RemoveAt(0);
        });
    }

    /// <summary>Under _lock.</summary>
    private void Append(JournalEntry entry)
    {
        try
        {
            File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log.Write("journal: " + ex.Message);
        }
    }

    private List<JournalEntry> Load()
    {
        var list = new List<JournalEntry>();
        lock (_lock)
        {
            // From now on new entries go into the list as well (see Add).
            _listRead = true;
            try
            {
                if (!File.Exists(FilePath)) return list;
                var lines = File.ReadAllLines(FilePath);
                foreach (var line in lines)
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<JournalEntry>(line, Json) is { } e) list.Add(e);
                    }
                    catch { }
                }
                if (lines.Length > Keep * 2)
                {
                    list = list.TakeLast(Keep).ToList();
                    File.WriteAllLines(FilePath, list.Select(e => JsonSerializer.Serialize(e, Json)));
                }
            }
            catch (Exception ex)
            {
                Log.Write("journal load: " + ex.Message);
            }
        }
        return list.TakeLast(Keep).ToList();
    }
}

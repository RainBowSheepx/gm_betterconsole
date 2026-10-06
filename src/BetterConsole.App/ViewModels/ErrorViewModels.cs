using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Media;
using BetterConsole.App.Services;
using BetterConsole.Core.Errors;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterConsole.App.ViewModels;

/// <summary>One distinct Lua error with the number of times it happened.</summary>
public sealed partial class ErrorEntryVm : ObservableObject
{
    public ErrorEntryVm(string key, LuaError e)
    {
        Key = key;
        Realm = e.Realm;
        var msg = e.Message.TrimEnd('\n', '\r', ' ');
        int nl = msg.IndexOf('\n');
        Headline = nl < 0 ? msg : msg[..nl];
        Details = nl < 0 ? null : msg[(nl + 1)..];
        Message = msg;
        Stack = e.Stack;
        Addon = e.AddonTitle;
        WorkshopId = e.WorkshopId;
        context = e.Context;
        FirstSeen = e.Time;
        lastSeen = e.Time;
        count = Math.Max(1, e.Count);
        Location = e.Stack.FirstOrDefault(f => f.Source != "[C]" && f.Line > 0) is { } top ? $"{top.Source}:{top.Line}" : null;
        var sb = new StringBuilder();
        for (int i = 0; i < e.Stack.Count; i++)
        {
            var f = e.Stack[i];
            sb.Append(new string(' ', i * 2)).Append(i + 1).Append(". ").Append(f.Function).Append(" - ").Append(f.Source);
            if (f.Line > 0) sb.Append(':').Append(f.Line);
            if (i < e.Stack.Count - 1) sb.Append('\n');
        }
        StackText = sb.Length > 0 ? sb.ToString() : "(no stack trace)";
    }

    public string Key { get; }
    public LuaRealm Realm { get; }
    public string Message { get; }
    public string Headline { get; }
    public string? Details { get; }
    public IReadOnlyList<StackFrame> Stack { get; }
    public string StackText { get; }
    public string? Addon { get; }
    public string? WorkshopId { get; }
    public string? Location { get; }
    public DateTime FirstSeen { get; }
    public bool HasAddon => !string.IsNullOrEmpty(Addon);

    [ObservableProperty] private string? context;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LastSeenText))] private DateTime lastSeen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CountText), nameof(IsRepeated))] private int count;
    [ObservableProperty] private bool isExpanded;
    /// <summary>Briefly true after the error happened again (the card flashes).</summary>
    [ObservableProperty] private bool isFresh;

    public string CountText => Count > 999 ? "999+" : "×" + Count;
    public bool IsRepeated => Count > 1;
    public string LastSeenText => FormatTime(LastSeen);
    public string FirstSeenText => FormatTime(FirstSeen);

    public static string FormatTime(DateTime t) =>
        t.Date == DateTime.Today ? t.ToString("HH:mm:ss") : t.ToString("dd.MM HH:mm:ss");

    public string ToClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Message);
        sb.AppendLine(StackText);
        if (!string.IsNullOrEmpty(Context)) sb.AppendLine(Context);
        if (HasAddon) sb.AppendLine($"Addon: {Addon}{(WorkshopId != null ? $" (workshop {WorkshopId})" : "")}");
        sb.AppendLine($"Count: {Count}, first {FirstSeenText}, last {LastSeenText}");
        return sb.ToString();
    }

    public bool Matches(string filter) =>
        Message.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || StackText.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (Addon?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>
/// Server errors: oldest first, newest at the bottom. A repeat only bumps the counter and the time of
/// the existing card; nothing moves, so the card being read stays where it is.
/// </summary>
public sealed partial class ServerErrorsVm : ObservableObject
{
    private readonly Dictionary<string, ErrorEntryVm> _byKey = new();

    public ObservableCollection<ErrorEntryVm> Items { get; } = new();

    [ObservableProperty] private int totalCount;
    [ObservableProperty] private int unseenCount;
    public int UniqueCount => Items.Count;

    public int MaxItems { get; set; } = 500;

    /// <summary>Returns the entry and whether it is new.</summary>
    public (ErrorEntryVm Entry, bool IsNew) Add(LuaError e, bool mergeSimilar)
    {
        var key = ErrorFingerprint.Of(e, mergeSimilar);
        int n = Math.Max(1, e.Count);
        TotalCount += n;
        UnseenCount += n;
        if (_byKey.TryGetValue(key, out var existing))
        {
            existing.Count += n;
            if (e.Time > existing.LastSeen) existing.LastSeen = e.Time;
            if (!string.IsNullOrEmpty(e.Context)) existing.Context = e.Context;
            existing.IsFresh = true;
            return (existing, false);
        }
        var entry = new ErrorEntryVm(key, e) { Count = n };
        _byKey[key] = entry;
        Items.Add(entry);
        while (Items.Count > MaxItems)
        {
            _byKey.Remove(Items[0].Key);
            Items.RemoveAt(0);
        }
        OnPropertyChanged(nameof(UniqueCount));
        return (entry, true);
    }

    public void Clear()
    {
        _byKey.Clear();
        Items.Clear();
        TotalCount = 0;
        UnseenCount = 0;
        OnPropertyChanged(nameof(UniqueCount));
    }
}

/// <summary>The errors of one player. Newest (most recently happened) first.</summary>
public sealed partial class PlayerErrorsVm : ObservableObject
{
    private readonly Dictionary<string, ErrorEntryVm> _byKey = new();

    public PlayerErrorsVm(PlayerRef player)
    {
        Key = player.Key;
        name = player.Name;
        steamId = player.SteamId;
        SteamId64 = player.SteamId64;
    }

    public string Key { get; }
    public string SteamId64 { get; private set; }
    [ObservableProperty] private string name;
    [ObservableProperty] private string steamId;
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LastSeenText))] private DateTime lastSeen;
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private bool hasUnseen;
    [ObservableProperty] private bool isFresh;
    /// <summary>Repeats that would move cards while the list is frozen.</summary>
    [ObservableProperty] private int pendingMoves;
    /// <summary>The Steam avatar, once loaded (null: the coloured initial is shown).</summary>
    [ObservableProperty] private ImageSource? avatar;

    public ObservableCollection<ErrorEntryVm> Entries { get; } = new();
    public int UniqueCount => Entries.Count;
    public string LastSeenText => ErrorEntryVm.FormatTime(LastSeen);

    public void UpdateIdentity(PlayerRef p)
    {
        if (!string.IsNullOrEmpty(p.Name)) Name = p.Name;
        if (!string.IsNullOrEmpty(p.SteamId)) SteamId = p.SteamId;
        if (!string.IsNullOrEmpty(p.SteamId64)) SteamId64 = p.SteamId64;
    }

    public ErrorEntryVm Add(LuaError e, bool mergeSimilar, bool frozen, int maxItems)
    {
        var key = ErrorFingerprint.Of(e with { Player = null }, mergeSimilar);
        int n = Math.Max(1, e.Count);
        TotalCount += n;
        if (e.Time > LastSeen) LastSeen = e.Time;
        if (!IsExpanded) HasUnseen = true;
        IsFresh = true;
        if (_byKey.TryGetValue(key, out var existing))
        {
            existing.Count += n;
            if (e.Time > existing.LastSeen) existing.LastSeen = e.Time;
            existing.IsFresh = true;
            int idx = Entries.IndexOf(existing);
            if (idx > 0)
            {
                if (frozen) PendingMoves++;
                else Entries.Move(idx, 0);
            }
            return existing;
        }
        var entry = new ErrorEntryVm(key, e) { Count = n };
        _byKey[key] = entry;
        Entries.Insert(0, entry);
        while (Entries.Count > maxItems)
        {
            var last = Entries[^1];
            _byKey.Remove(last.Key);
            Entries.RemoveAt(Entries.Count - 1);
        }
        OnPropertyChanged(nameof(UniqueCount));
        return entry;
    }

    /// <summary>Puts the cards back in "most recent first" order after a freeze.</summary>
    public void ApplyOrder()
    {
        if (PendingMoves == 0) return;
        PendingMoves = 0;
        var sorted = Entries.OrderByDescending(x => x.LastSeen).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Entries.IndexOf(sorted[i]);
            if (cur != i) Entries.Move(cur, i);
        }
    }
}

/// <summary>Client errors grouped by player, players sorted by name.</summary>
public sealed partial class ClientErrorsVm : ObservableObject
{
    private readonly Dictionary<string, PlayerErrorsVm> _byKey = new();

    public ObservableCollection<PlayerErrorsVm> Players { get; } = new();

    [ObservableProperty] private int totalCount;
    [ObservableProperty] private int unseenCount;

    /// <summary>
    /// Set while the pointer is over the list: repeats then only update counters instead of moving
    /// cards to the top, so the card being read does not run away. The order is restored afterwards.
    /// </summary>
    [ObservableProperty] private bool isFrozen;

    public int MaxItemsPerPlayer { get; set; } = 200;

    partial void OnIsFrozenChanged(bool value)
    {
        if (!value) foreach (var p in Players) p.ApplyOrder();
    }

    public void Add(LuaError e, bool mergeSimilar)
    {
        var player = e.Player ?? new PlayerRef("Unknown player", "", "", 0);
        int n = Math.Max(1, e.Count);
        TotalCount += n;
        UnseenCount += n;
        if (!_byKey.TryGetValue(player.Key, out var group))
        {
            // Bridge errors carry SteamID64, console ones only the SteamID: find the same player both ways.
            group = player.IsBot ? null : Players.FirstOrDefault(p => !string.IsNullOrEmpty(player.SteamId) && p.SteamId == player.SteamId);
            if (group == null)
            {
                group = new PlayerErrorsVm(player);
                Insert(group);
            }
            _byKey[player.Key] = group;
        }
        else
        {
            var oldName = group.Name;
            group.UpdateIdentity(player);
            if (oldName != group.Name)
            {
                Players.Remove(group);
                Insert(group);
            }
        }
        group.Add(e, mergeSimilar, IsFrozen, MaxItemsPerPlayer);
        if (ShowAvatars && group.Avatar == null) LoadAvatar(group);
    }

    /// <summary>Steam avatars in the player headers (Settings → Players). Off: the coloured initials.</summary>
    public bool ShowAvatars
    {
        get => _showAvatars;
        set
        {
            if (_showAvatars == value) return;
            _showAvatars = value;
            foreach (var p in Players)
            {
                if (!value) p.Avatar = null;
                else LoadAvatar(p);
            }
        }
    }
    private bool _showAvatars = true;

    private void LoadAvatar(PlayerErrorsVm p) =>
        p.Avatar = AvatarCache.Get(p.SteamId64, img => { if (_showAvatars) p.Avatar = img; });

    private void Insert(PlayerErrorsVm group)
    {
        int i = 0;
        while (i < Players.Count && string.Compare(Players[i].Name, group.Name, StringComparison.CurrentCultureIgnoreCase) <= 0) i++;
        Players.Insert(i, group);
    }

    public void Clear()
    {
        _byKey.Clear();
        Players.Clear();
        TotalCount = 0;
        UnseenCount = 0;
    }
}

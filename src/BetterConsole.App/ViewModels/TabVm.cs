using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterConsole.App.ViewModels;

/// <summary>A tab of a server. Content is created the first time the tab is shown.</summary>
public sealed partial class TabVm : ObservableObject
{
    private FrameworkElement? _content;

    public TabVm(string id, string header, string icon, int order, Func<FrameworkElement> factory)
    {
        Id = id;
        this.header = header;
        this.icon = icon;
        Order = order;
        Factory = factory;
    }

    public string Id { get; }
    /// <summary>A glyph of Segoe Fluent Icons (or any short text).</summary>
    [ObservableProperty] private string icon;
    public int Order { get; set; }
    public Func<FrameworkElement> Factory { get; }
    [ObservableProperty] private string header;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBadge))] private string? badge;
    /// <summary>"danger", "accent" or "muted".</summary>
    [ObservableProperty] private string badgeKind = "muted";
    [ObservableProperty] private bool isSelected;
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    public bool IsCreated => _content != null;
    /// <summary>The view (created on first use; its Tag is the tab id, so the window can find it).</summary>
    public FrameworkElement Content
    {
        get
        {
            if (_content == null)
            {
                _content = Factory();
                _content.Tag = Id;
            }
            return _content;
        }
    }
    /// <summary>The view, if it was created already.</summary>
    public FrameworkElement? CreatedContent => _content;
}

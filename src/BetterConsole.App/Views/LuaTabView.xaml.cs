using System.Windows;
using System.Windows.Controls;
using BetterConsole.App.ViewModels;

namespace BetterConsole.App.Views;

/// <summary>Shows a tab defined by a server addon (BetterConsole.AddTab) with its widgets (their look: LuaWidgets.xaml).</summary>
public partial class LuaTabView : UserControl
{
    public LuaTabView(LuaTabVm tab)
    {
        InitializeComponent();
        DataContext = tab;
        tab.Widgets.CollectionChanged += (_, _) => Update(tab);
        Update(tab);
    }

    private void Update(LuaTabVm tab) => Empty.Visibility = tab.Widgets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}

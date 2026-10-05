using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalBack.App.ViewModels;

namespace LocalBack.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private FolderBrowserViewModel? Browser => (DataContext as HistoryViewModel)?.Browser;

    // ---- Folders view: the list is a plain ListView so it behaves like Explorer's details view ----

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Browser == null) return;
        Browser.SetSelection(FolderList.SelectedItems.OfType<BrowseItemViewModel>());
        // A file picked from the Explorer menu is selected from code; bring it into view.
        if (e.AddedItems.Count == 1 && FolderList.SelectedItems.Count == 1) FolderList.ScrollIntoView(e.AddedItems[0]);
    }

    private void FolderItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListViewItem { DataContext: BrowseItemViewModel item }) return;
        if (item.Open.CanExecute(null)) item.Open.Execute(null);
        e.Handled = true;
    }

    private void FolderList_KeyDown(object sender, KeyEventArgs e)
    {
        if (Browser is not { } b) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        ICommand? cmd = key switch
        {
            Key.Enter when !alt => b.OpenSelected,
            Key.Back => b.Up,
            Key.Left when alt => b.Back,
            Key.Right when alt => b.Forward,
            Key.Up when alt => b.Up,
            Key.BrowserBack => b.Back,
            Key.BrowserForward => b.Forward,
            _ => null,
        };
        if (cmd == null) return;
        if (cmd.CanExecute(null)) cmd.Execute(null);
        e.Handled = true;
    }

    private void FolderList_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string column }) Browser?.SortBy(column);
    }

    /// <summary>The Name column takes whatever width the other columns leave, so there is no sideways scrolling at normal sizes.</summary>
    private void FolderList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double others = ModifiedColumn.Width + TypeColumn.Width + SizeColumn.Width + ChangeColumn.Width;
        NameColumn.Width = Math.Max(150, FolderList.ActualWidth - others - 30);
    }
}

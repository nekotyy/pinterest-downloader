using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PinterestDownload;

sealed class SettingsDialog : Window
{
    public SettingsDialog(AppPreferences current)
    {
        Title = "Настройки"; Width = 480; Height = 500; MinWidth = 420; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var theme = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.Items.Add("Как в системе"); theme.Items.Add("Светлая"); theme.Items.Add("Тёмная");
        theme.SelectedIndex = current.Theme == "Light" ? 1 : current.Theme == "Dark" ? 2 : 0;
        var pageSize = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var count in new[] { 50, 100, 200 }) pageSize.Items.Add(count.ToString());
        pageSize.SelectedIndex = current.PageSize == 50 ? 0 : current.PageSize == 200 ? 2 : 1;
        var autoSelect = new CheckBox { Content = "Выбирать новые пины автоматически", IsChecked = current.AutoSelect };
        var autoClose = new CheckBox { Content = "Закрывать вкладки после скачивания", IsChecked = current.AutoClose };
        var cancel = new Button { Content = "Отмена" };
        var save = new Button { Content = "Сохранить" };
        cancel.Click += (_, _) => Close(null);
        save.Click += (_, _) => Close(new AppPreferences(theme.SelectedIndex == 1 ? "Light" : theme.SelectedIndex == 2 ? "Dark" : "System", autoClose.IsChecked == true, autoSelect.IsChecked == true, pageSize.SelectedIndex == 0 ? 50 : pageSize.SelectedIndex == 2 ? 200 : 100));
        var controls = new StackPanel { Spacing = 12, Children = {
            new TextBlock { Text = "Настройки приложения", FontSize = 21, FontWeight = Avalonia.Media.FontWeight.SemiBold },
            new TextBlock { Text = "Тема" }, theme,
            new TextBlock { Text = "Пинов на странице" }, pageSize,
            autoSelect, autoClose,
            new TextBlock { Text = "Автоматически закрываются только вкладки успешно сохранённых пинов в выбранном браузере.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = .8 },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } }
        }};
        Content = new ScrollViewer { Content = controls, Margin = new Thickness(24) };
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TechArrow.GameUpdater.ViewModels;
namespace TechArrow.GameUpdater.Views;
public partial class MainWindow : Window
{
    private bool _selectingFile;
    public bool CanRestartForUpdate => !_selectingFile;
    public MainWindow(MainViewModel viewModel, AppUpdatesViewModel updates, ClubViewModel club)
    {
        InitializeComponent();
        viewModel.Updates = updates; viewModel.Club = club; DataContext = viewModel;
        Title = "TechArrow Game Updater — v" + updates.CurrentVersion;
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(640, workArea.Width);
        MinHeight = Math.Min(420, workArea.Height);
        Width = Math.Min(1180, workArea.Width * 0.94);
        Height = Math.Min(820, workArea.Height * 0.94);
        UpdateLayoutMode();
    }
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutMode();
    private async void EnableMeasurements_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) await model.EnableActivityCollectorAsync();
    }
    private void UpdateLayoutMode() => Tag = (ActualWidth > 0 ? ActualWidth : Width) < 1000 ||
        (ActualHeight > 0 ? ActualHeight : Height) < 620 ? "Compact" : "Desktop";
    private async void ConnectClub_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { Club: not null } model) return;
        var key = ClubDeviceKey.Password;
        ClubDeviceKey.Clear();
        try { await model.Club.ConnectAsync(key); }
        catch (Exception ex) { model.Club.Error(ex); }
    }
    private async void ChooseExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (_selectingFile || sender is not Button { Tag: string launcher } || DataContext is not MainViewModel viewModel ||
            !viewModel.CanChangeLauncherPaths || !viewModel.Settings.CanSelectFiles) return;
        _selectingFile = true;
        try
        {
            var currentPath = launcher switch
            {
                "Steam" => viewModel.Settings.SteamPath, "Epic" => viewModel.Settings.EpicPath,
                "Lesta" => viewModel.Settings.LestaPath, "BattleNet" => viewModel.Settings.BattleNetPath,
                "Ea" => viewModel.Settings.EaPath, "Riot" => viewModel.Settings.RiotPath,
                "VkPlay" => viewModel.Settings.VkPlayPath, "Wargaming" => viewModel.Settings.WargamingPath,
                _ => ""
            };
            var dialog = new OpenFileDialog
            {
                Title = launcher == "Auto" ? "Добавить лаунчер" : "Заменить файл лаунчера",
                Filter = "Приложения (*.exe)|*.exe",
                CheckFileExists = true, CheckPathExists = true, Multiselect = false,
                DefaultExt = ".exe", RestoreDirectory = true
            };
            if (File.Exists(currentPath))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
                dialog.FileName = Path.GetFileName(currentPath);
            }
            if (dialog.ShowDialog(this) != true) return;
            if (launcher == "Auto" && TechArrow.GameUpdater.Infrastructure.Services.LauncherIdentification.Identify(dialog.FileName) is null)
            {
                var choice = ChooseSharedLauncher(dialog.FileName);
                if (choice == "Cancelled") return;
                launcher = choice ?? "Auto";
            }
            launcher = await viewModel.Settings.SelectExecutableAsync(launcher, dialog.FileName);
            if (launcher == "Steam") await viewModel.ScanSteamAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Выбор лаунчера", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _selectingFile = false; }
    }
    private string? ChooseSharedLauncher(string path)
    {
        var candidates = Enumerable.Range(1, 7).Where(i => TechArrow.GameUpdater.Infrastructure.Services.LauncherProcessProfile.For(i).IsFrontend(Path.GetFileName(path))).ToArray();
        if (candidates.Length < 2) return null;
        string? selected = null;
        var panel = new StackPanel { Margin = new Thickness(24), Width = 380 };
        panel.Children.Add(new TextBlock { Text = "У этого файла общее имя. Выберите клиент:", Margin = new Thickness(0, 0, 0, 16) });
        var dialog = new Window { Owner = this, Title = "Определение лаунчера", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        foreach (var index in candidates)
        {
            var key = TechArrow.GameUpdater.Infrastructure.Services.LauncherIdentification.Keys[index];
            var button = new Button { Content = ((MainViewModel)DataContext).Launchers[index].Name, Margin = new Thickness(0, 0, 0, 8) };
            button.Click += (_, _) => { selected = key; dialog.DialogResult = true; };
            panel.Children.Add(button);
        }
        return dialog.ShowDialog() == true ? selected : "Cancelled";
    }
    private async void RemoveLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || DataContext is not MainViewModel model || !model.CanChangeLauncherPaths) return;
        try { await model.Settings.RemoveExecutableAsync(key); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Удаление из списка", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void UpdateLauncher_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || DataContext is not MainViewModel model || !model.CanChangeLauncherPaths) return;
        try { await model.UpdateLauncherAsync(key); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Обновление клиента", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}

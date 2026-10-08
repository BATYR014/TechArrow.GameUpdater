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
                Title = "Выберите файл клиента: " + launcher,
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
            launcher = await viewModel.Settings.SelectExecutableAsync(launcher, dialog.FileName);
            if (launcher == "Steam") await viewModel.ScanSteamAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Выбор лаунчера", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _selectingFile = false; }
    }
}

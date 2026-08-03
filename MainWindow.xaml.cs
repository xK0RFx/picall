using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Picall.Models;
using Picall.Services;

namespace Picall;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private enum LibraryFilter { All, Photos, Videos, Favorites }

    private readonly AppSettings _settings;
    private readonly MediaScanner _scanner = new();
    private readonly ThumbnailService _thumbnails = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _batchRefreshTimer;
    private readonly DispatcherTimer _indexSaveTimer;
    private readonly DispatcherTimer _driveCheckTimer;
    private CancellationTokenSource? _scanCancellation;
    private MediaWatcher? _watcher;
    private List<MediaItem> _allItems = [];
    private HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MediaItem> _displayedItems = Array.Empty<MediaItem>();
    private LibraryFilter _filter = LibraryFilter.All;
    private int _filterVersion;
    private bool _isScanning;
    private bool _windowLoaded;
    private string _scanRootSignature = string.Empty;
    private string? _selectedSourceRoot;
    private double _tileWidth = 194;
    private double _tileHeight = 244;
    private int _allCount;
    private int _photoCount;
    private int _videoCount;
    private int _favoriteCount;
    private bool _pendingViewRefresh;
    private bool _applyingPendingRefresh;
    private int _pendingNewItems;

    public MainWindow()
    {
        App.WriteStartupLog("Creating main window");
        _settings = AppSettings.Load();
        _selectedSourceRoot = string.IsNullOrWhiteSpace(_settings.SelectedSource) ? null : _settings.SelectedSource;
        TileWidth = Math.Clamp(_settings.TileWidth, 152, 264);
        ExtraFolders = new ObservableCollection<string>(_settings.ExtraFolders.Where(Directory.Exists));
        Sources = [];
        FormatFilters = [];
        UnavailableFormatFilters = [];
        InitializeComponent();
        App.WriteStartupLog("Main window XAML loaded");
        DataContext = this;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await ApplyFilterAsync(); };
        _batchRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _batchRefreshTimer.Tick += async (_, _) =>
        {
            _batchRefreshTimer.Stop();
            UpdateCounts();
            await RefreshViewOrDeferAsync();
        };
        _indexSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _indexSaveTimer.Tick += async (_, _) =>
        {
            _indexSaveTimer.Stop();
            try { await IndexStore.SaveAsync(_allItems.ToArray()); } catch (Exception ex) { TryLog(ex); }
        };
        _driveCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _driveCheckTimer.Tick += async (_, _) =>
        {
            if (_isScanning) return;
            var roots = MediaScanner.GetScanRoots(ExtraFolders);
            var signature = RootSignature(roots);
            if (_scanRootSignature.Length > 0 && !string.Equals(signature, _scanRootSignature, StringComparison.OrdinalIgnoreCase))
            {
                RebuildSources();
                await StartScanAsync();
            }
        };
    }

    public ObservableCollection<string> ExtraFolders { get; }
    public ObservableCollection<SourceOption> Sources { get; }
    public ObservableCollection<FormatFilterOption> FormatFilters { get; }
    public ObservableCollection<FormatFilterOption> UnavailableFormatFilters { get; }
    public int UnavailableFormatCount => UnavailableFormatFilters.Count;

    public IReadOnlyList<MediaItem> DisplayedItems
    {
        get => _displayedItems;
        private set { _displayedItems = value; OnPropertyChanged(); }
    }

    public double TileWidth
    {
        get => _tileWidth;
        set
        {
            if (Math.Abs(_tileWidth - value) < 0.1) return;
            _tileWidth = value;
            TileHeight = Math.Round(value * 1.18 + 16);
            OnPropertyChanged();
        }
    }

    public double TileHeight
    {
        get => _tileHeight;
        private set { _tileHeight = value; OnPropertyChanged(); }
    }

    public int AllCount => _allCount;
    public int PhotoCount => _photoCount;
    public int VideoCount => _videoCount;
    public int FavoriteCount => _favoriteCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WindowMessageHook);
        TryEnableModernWindow();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        App.WriteStartupLog("Main window shown");
        ZoomSlider.Value = TileWidth;
        SelectSortMode(_settings.SortMode);
        SelectComboMode(DateFilterCombo, _settings.DateFilter);
        SelectComboMode(SizeFilterCombo, _settings.SizeFilter);
        _windowLoaded = true;
        Window_StateChanged(this, EventArgs.Empty);
        RebuildSources();
        UpdateFilterBadge();
        _driveCheckTimer.Start();
        await LoadLibraryAsync();
    }

    private async Task LoadLibraryAsync()
    {
        App.WriteStartupLog("Loading media index");
        SetScanState(false, "Загрузка…", "Читаю локальный индекс");
        var favorites = _settings.FavoritePaths;
        var cached = await Task.Run(() => IndexStore.Load(favorites));
        _allItems = cached;
        _knownPaths = cached.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        UpdateCounts();
        await ApplyFilterAsync();
        App.WriteStartupLog($"Media index ready: {cached.Count} items");
        await StartScanAsync();
    }

    private async Task StartScanAsync()
    {
        if (_isScanning) return;
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        var roots = MediaScanner.GetScanRoots(ExtraFolders);
        SetScanState(true, "Индексирую", "Ищу фото и видео…");

        var progress = new Progress<ScanProgress>(p =>
        {
            var found = FormatNumber(p.MediaFound);
            ScanDetailText.Text = p.CurrentRoot.Length == 0
                ? $"Найдено {found}"
                : $"{found} · {ShortRoot(p.CurrentRoot)}";
        });

        try
        {
            var snapshot = _allItems.ToArray();
            var result = await _scanner.ScanAsync(snapshot, roots, OnScannerBatch, progress, token);
            token.ThrowIfCancellationRequested();
            foreach (var item in result) item.IsFavorite = _settings.FavoritePaths.Contains(item.Path);
            _allItems = result;
            _knownPaths = result.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _batchRefreshTimer.Stop();
            UpdateCounts();
            await RefreshViewOrDeferAsync();
            SetScanState(false, "Готово", $"{FormatNumber(result.Count)} файлов в медиатеке");
            StartWatcher(roots);
            ScheduleQaScreenshot();
            await IndexStore.SaveAsync(result, token);
            App.WriteStartupLog($"Scan complete: {result.Count} items");
            _ = Task.Run(() => ThumbnailService.TrimDiskCache(), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            SetScanState(false, "Остановлено", $"{FormatNumber(_allItems.Count)} файлов в медиатеке");
        }
        catch (Exception ex)
        {
            TryLog(ex);
            SetScanState(false, "Не всё найдено", "Нажмите, чтобы повторить");
        }
    }

    private void StartWatcher(IReadOnlyList<string> roots)
    {
        _watcher?.Dispose();
        _watcher = new MediaWatcher(roots, OnMediaFilesChanged);
        _scanRootSignature = RootSignature(roots);
    }

    private void OnMediaFilesChanged(IReadOnlyList<MediaFileChange> changes)
    {
        _ = Task.Run(() =>
        {
            var prepared = changes.Select(change => (change, item: change.Kind == MediaChangeKind.Upsert
                ? MediaScanner.ReadMediaFile(change.Path) : null)).ToArray();
            Dispatcher.BeginInvoke(async () =>
            {
                var changed = false;
                var added = 0;
                foreach (var (change, item) in prepared)
                {
                    var index = _allItems.FindIndex(x => string.Equals(x.Path, change.Path, StringComparison.OrdinalIgnoreCase));
                    if (change.Kind == MediaChangeKind.Remove || item is null)
                    {
                        if (index < 0) continue;
                        _allItems.RemoveAt(index);
                        _knownPaths.Remove(change.Path);
                        changed = true;
                        continue;
                    }

                    if (index >= 0)
                    {
                        var current = _allItems[index];
                        if (current.ModifiedUtc.Ticks == item.ModifiedUtc.Ticks && current.Size == item.Size) continue;
                        item.IsFavorite = _settings.FavoritePaths.Contains(item.Path);
                        _allItems[index] = item;
                    }
                    else
                    {
                        item.IsFavorite = _settings.FavoritePaths.Contains(item.Path);
                        _allItems.Add(item);
                        _knownPaths.Add(item.Path);
                        added++;
                    }
                    changed = true;
                }
                if (!changed) return;
                _pendingNewItems += added;
                UpdateCounts();
                await RefreshViewOrDeferAsync();
                _indexSaveTimer.Stop();
                _indexSaveTimer.Start();
                ScanStateText.Text = "Обновлено";
                ScanDetailText.Text = $"{FormatNumber(_allItems.Count)} файлов в медиатеке";
                ScheduleQaScreenshot();
            }, DispatcherPriority.Background);
        });
    }

    private void OnScannerBatch(IReadOnlyList<MediaItem> batch)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var changed = false;
            var added = 0;
            foreach (var item in batch)
            {
                if (!_knownPaths.Add(item.Path)) continue;
                _allItems.Add(item);
                changed = true;
                added++;
            }
            if (!changed) return;
            _pendingNewItems += added;
            if (!_batchRefreshTimer.IsEnabled) _batchRefreshTimer.Start();
        }, DispatcherPriority.Background);
    }

    private async Task RefreshViewOrDeferAsync()
    {
        if (IsGalleryScrolledDown() && DisplayedItems.Count > 0)
        {
            _pendingViewRefresh = true;
            UpdatePendingRefreshButton();
            await ApplyFilterAsync(false);
            return;
        }

        await ApplyFilterAsync();
    }

    private async Task ApplyFilterAsync(bool updateView = true)
    {
        if (!_windowLoaded) return;
        var version = ++_filterVersion;
        var items = _allItems.ToArray();
        var filter = _filter;
        var query = SearchBox.Text.Trim();
        var sortMode = (SortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "newest";
        var sourceRoot = _selectedSourceRoot;
        var dateMode = (DateFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "any";
        var sizeMode = (SizeFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "any";
        var includedFormats = FormatFilters.Where(x => x.IsSelected).Select(x => x.Extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasFormatOptions = FormatFilters.Count > 0;
        var nowUtc = DateTime.UtcNow;
        var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sourceRoots = MediaScanner.GetScanRoots(ExtraFolders).ToArray();

        var computation = await Task.Run(() =>
        {
            bool MatchesSearch(MediaItem item) => queryTokens.Length == 0 || queryTokens.All(token =>
                item.Name.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                item.Directory.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                item.Extension.Contains(token, StringComparison.OrdinalIgnoreCase));

            bool MatchesDate(MediaItem item) => dateMode switch
            {
                "7d" => item.ModifiedUtc >= nowUtc.AddDays(-7),
                "30d" => item.ModifiedUtc >= nowUtc.AddDays(-30),
                "year" => item.ModifiedUtc >= nowUtc.AddYears(-1),
                _ => true
            };

            const long megabyte = 1024L * 1024L;
            bool MatchesSize(MediaItem item) => sizeMode switch
            {
                "small" => item.Size < megabyte,
                "medium" => item.Size >= megabyte && item.Size < 10 * megabyte,
                "large" => item.Size >= 10 * megabyte && item.Size < 100 * megabyte,
                "huge" => item.Size >= 100 * megabyte,
                _ => true
            };

            bool MatchesCommon(MediaItem item) => MatchesSearch(item) && MatchesDate(item) && MatchesSize(item) &&
                (!hasFormatOptions || includedFormats.Contains(item.Extension));

            bool MatchesLibrary(MediaItem item) => filter switch
            {
                LibraryFilter.Photos => item.Kind == MediaKind.Photo,
                LibraryFilter.Videos => item.Kind == MediaKind.Video,
                LibraryFilter.Favorites => item.IsFavorite,
                _ => true
            };

            bool MatchesSource(MediaItem item) => string.IsNullOrWhiteSpace(sourceRoot) || IsWithinRoot(item.Path, sourceRoot);

            var common = items.Where(MatchesCommon).ToList();
            var navigationScope = common.Where(MatchesSource).ToList();
            var sourceScope = common.Where(MatchesLibrary).ToList();
            IEnumerable<MediaItem> filtered = sourceScope.Where(MatchesSource);
            filtered = sortMode switch
            {
                "oldest" => filtered.OrderBy(x => x.ModifiedUtc),
                "name" => filtered.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
                "size" => filtered.OrderByDescending(x => x.Size),
                _ => filtered.OrderByDescending(x => x.ModifiedUtc)
            };

            var sourceCounts = sourceRoots.ToDictionary(root => root,
                root => sourceScope.Count(x => IsWithinRoot(x.Path, root)), StringComparer.OrdinalIgnoreCase);
            return (
                Items: (IReadOnlyList<MediaItem>)filtered.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList(),
                All: navigationScope.Count,
                Photos: navigationScope.Count(x => x.Kind == MediaKind.Photo),
                Videos: navigationScope.Count(x => x.Kind == MediaKind.Video),
                Favorites: navigationScope.Count(x => x.IsFavorite),
                AllSources: sourceScope.Count,
                SourceCounts: sourceCounts);
        });

        if (version != _filterVersion) return;
        _allCount = computation.All;
        _photoCount = computation.Photos;
        _videoCount = computation.Videos;
        _favoriteCount = computation.Favorites;
        NotifyCountProperties();
        RebuildSources(computation.SourceCounts, computation.AllSources);
        UpdateFilterBadge();
        if (!updateView) return;

        ClearPendingViewRefresh();
        DisplayedItems = computation.Items;
        ResultCountText.Text = ResultLabel(computation.Items.Count);
        EmptyState.Visibility = computation.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (computation.Items.Count == 0)
        {
            var searching = query.Length > 0;
            EmptyTitle.Text = searching ? "Ничего не найдено" : "Здесь пока пусто";
            EmptySubtitle.Text = searching ? "Попробуйте изменить запрос" : "Picall найдёт фото и видео автоматически";
            EmptyIcon.Text = searching ? "\uE721" : "\uE8A9";
        }
    }

    private bool IsGalleryScrolledDown()
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(GalleryList);
        return scrollViewer is not null && scrollViewer.VerticalOffset > 0.01;
    }

    private async void GalleryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_pendingViewRefresh || _applyingPendingRefresh || e.VerticalOffset > 0.01) return;
        await ApplyPendingViewRefreshAsync(false);
    }

    private async void PendingRefreshButton_Click(object sender, RoutedEventArgs e) =>
        await ApplyPendingViewRefreshAsync(true);

    private async Task ApplyPendingViewRefreshAsync(bool scrollToTop)
    {
        if (!_pendingViewRefresh || _applyingPendingRefresh) return;
        _applyingPendingRefresh = true;
        try
        {
            ClearPendingViewRefresh();
            await ApplyFilterAsync();
            if (scrollToTop) FindVisualChild<ScrollViewer>(GalleryList)?.ScrollToTop();
        }
        finally
        {
            _applyingPendingRefresh = false;
        }
    }

    private void ClearPendingViewRefresh()
    {
        _pendingViewRefresh = false;
        _pendingNewItems = 0;
        UpdatePendingRefreshButton();
    }

    private void UpdatePendingRefreshButton()
    {
        if (PendingRefreshButton is null) return;
        PendingRefreshButton.Visibility = _pendingViewRefresh ? Visibility.Visible : Visibility.Collapsed;
        PendingRefreshButton.Content = _pendingNewItems > 0
            ? $"{FormatNumber(_pendingNewItems)} новых · Показать"
            : "Медиатека обновилась · Показать";
    }

    private async void MediaCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MediaItem item } || item.Thumbnail is not null || item.ThumbnailRequested) return;
        item.ThumbnailRequested = true;
        try { item.Thumbnail = await _thumbnails.GetAsync(item); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { TryLog(ex); }
    }

    private void MediaCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: MediaItem item })
        {
            OpenItem(item);
            e.Handled = true;
        }
    }

    private void GalleryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GalleryList.SelectedItem is MediaItem item)
            FooterText.Text = $"{item.Directory}  ·  {item.SizeLabel}";
        else
            FooterText.Text = "Локальная медиатека · данные не покидают компьютер";
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e) => ToggleFavorite(GetItem(sender));
    private void FavoriteMenu_Click(object sender, RoutedEventArgs e) => ToggleFavorite(GetItem(sender));
    private void OpenMenu_Click(object sender, RoutedEventArgs e) { if (GetItem(sender) is { } item) OpenItem(item); }
    private void RevealMenu_Click(object sender, RoutedEventArgs e) { if (GetItem(sender) is { } item) RevealItem(item); }

    private void CopyPathMenu_Click(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is not { } item) return;
        try { Clipboard.SetText(item.Path); } catch { }
    }

    private void ToggleFavorite(MediaItem? item)
    {
        if (item is null) return;
        item.IsFavorite = !item.IsFavorite;
        if (item.IsFavorite) _settings.FavoritePaths.Add(item.Path);
        else _settings.FavoritePaths.Remove(item.Path);
        _settings.Save();
        UpdateCounts();
        if (_filter == LibraryFilter.Favorites) _ = ApplyFilterAsync();
    }

    private static MediaItem? GetItem(object sender)
    {
        if (sender is FrameworkElement { DataContext: MediaItem direct }) return direct;
        if (sender is MenuItem menu && menu.Parent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: MediaItem placed } }) return placed;
        return null;
    }

    private static void OpenItem(MediaItem item)
    {
        try
        {
            if (!File.Exists(item.Path)) return;
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch { }
    }

    private static void RevealItem(MediaItem item)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(item.Path);
            Process.Start(start);
        }
        catch { }
    }

    private async void Filter_Click(object sender, RoutedEventArgs e)
    {
        _filter = sender switch
        {
            RadioButton button when button == PhotoFilter => LibraryFilter.Photos,
            RadioButton button when button == VideoFilter => LibraryFilter.Videos,
            RadioButton button when button == FavoriteFilter => LibraryFilter.Favorites,
            _ => LibraryFilter.All
        };
        SectionTitle.Text = _filter switch
        {
            LibraryFilter.Photos => "Фотографии",
            LibraryFilter.Videos => "Видео",
            LibraryFilter.Favorites => "Избранное",
            _ => "Всё медиа"
        };
        await ApplyFilterAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }

    private async void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        _settings.SortMode = (SortCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "newest";
        _settings.Save();
        await ApplyFilterAsync();
    }

    private async void SourceFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: SourceOption source }) return;
        foreach (var option in Sources) option.IsSelected = ReferenceEquals(option, source);
        _selectedSourceRoot = string.IsNullOrWhiteSpace(source.Root) ? null : source.Root;
        _settings.SelectedSource = _selectedSourceRoot;
        _settings.Save();
        await ApplyFilterAsync();
    }

    private async void AdvancedFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        _settings.DateFilter = (DateFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "any";
        _settings.SizeFilter = (SizeFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "any";
        _settings.Save();
        await ApplyFilterAsync();
    }

    private async void FormatFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: FormatFilterOption option }) return;
        if (option.IsSelected) _settings.ExcludedFormats.Remove(option.Extension);
        else _settings.ExcludedFormats.Add(option.Extension);
        _settings.Save();
        await ApplyFilterAsync();
    }

    private async void SelectAllFormats_Click(object sender, RoutedEventArgs e)
    {
        foreach (var option in FormatFilters) option.IsSelected = true;
        _settings.ExcludedFormats.Clear();
        _settings.Save();
        await ApplyFilterAsync();
    }

    private async void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _filter = LibraryFilter.All;
        AllFilter.IsChecked = true;
        SectionTitle.Text = "Всё медиа";
        _selectedSourceRoot = null;
        foreach (var source in Sources) source.IsSelected = string.IsNullOrEmpty(source.Root);
        foreach (var format in FormatFilters) format.IsSelected = true;
        DateFilterCombo.SelectedIndex = 0;
        SizeFilterCombo.SelectedIndex = 0;
        _settings.SelectedSource = null;
        _settings.DateFilter = "any";
        _settings.SizeFilter = "any";
        _settings.ExcludedFormats.Clear();
        _settings.Save();
        await ApplyFilterAsync();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_windowLoaded) return;
        TileWidth = e.NewValue;
        _settings.TileWidth = e.NewValue;
        _settings.Save();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку с фото или видео",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        var folder = Path.GetFullPath(dialog.FolderName).TrimEnd('\\');
        if (!ExtraFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) ExtraFolders.Add(folder);
        _settings.ExtraFolders = ExtraFolders.ToList();
        _settings.Save();
        RebuildSources();
        if (_isScanning) _scanCancellation?.Cancel();
        await Task.Delay(120);
        await StartScanAsync();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning) { _scanCancellation?.Cancel(); return; }
        await StartScanAsync();
    }

    private void SetScanState(bool scanning, string title, string detail)
    {
        _isScanning = scanning;
        ScanStateText.Text = title;
        ScanDetailText.Text = detail;
        ScanButtonIcon.Text = scanning ? "\uE71A" : "\uE72C";
        ScanButton.ToolTip = scanning ? "Остановить сканирование" : "Обновить индекс";
        ScanPulse.Fill = scanning ? (System.Windows.Media.Brush)FindResource("AccentBrush") : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(44, 37, 64));
        ScanPulse.BeginAnimation(OpacityProperty, scanning
            ? new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private void UpdateCounts()
    {
        _allCount = _allItems.Count;
        _photoCount = _allItems.Count(x => x.Kind == MediaKind.Photo);
        _videoCount = _allItems.Count(x => x.Kind == MediaKind.Video);
        _favoriteCount = _allItems.Count(x => x.IsFavorite);
        NotifyCountProperties();
        RebuildSources();
        RebuildFormatFilters();
    }

    private void NotifyCountProperties()
    {
        OnPropertyChanged(nameof(AllCount));
        OnPropertyChanged(nameof(PhotoCount));
        OnPropertyChanged(nameof(VideoCount));
        OnPropertyChanged(nameof(FavoriteCount));
    }

    private void RebuildSources(IReadOnlyDictionary<string, int>? filteredCounts = null, int? allSourcesCount = null)
    {
        var roots = MediaScanner.GetScanRoots(ExtraFolders);
        var options = new List<SourceOption>
        {
            new()
            {
                Root = string.Empty, Name = "Все диски", Subtitle = "Вся медиатека на компьютере",
                IsDrive = true, Count = allSourcesCount ?? _allItems.Count, IsSelected = string.IsNullOrWhiteSpace(_selectedSourceRoot)
            }
        };
        foreach (var root in roots)
        {
            var isDrive = IsDriveRoot(root);
            var name = SourceName(root, isDrive);
            options.Add(new SourceOption
            {
                Root = root, Name = name, Subtitle = root, IsDrive = isDrive,
                Count = filteredCounts is not null && filteredCounts.TryGetValue(root, out var filteredCount)
                    ? filteredCount : _allItems.Count(x => IsWithinRoot(x.Path, root)),
                IsSelected = string.Equals(root, _selectedSourceRoot, StringComparison.OrdinalIgnoreCase)
            });
        }
        if (_selectedSourceRoot is not null && !options.Any(x => x.IsSelected))
        {
            _selectedSourceRoot = null;
            _settings.SelectedSource = null;
            options[0].IsSelected = true;
        }
        Sources.Clear();
        foreach (var option in options) Sources.Add(option);
    }

    private void RebuildFormatFilters()
    {
        var formats = _allItems.GroupBy(x => x.Extension, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new FormatFilterOption
            {
                Extension = x.Key,
                Count = x.Count(),
                IsSelected = !_settings.ExcludedFormats.Contains(x.Key)
            }).ToList();
        FormatFilters.Clear();
        foreach (var format in formats) FormatFilters.Add(format);
        var found = formats.Select(x => x.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        UnavailableFormatFilters.Clear();
        foreach (var extension in MediaScanner.GetSupportedExtensions().Where(x => !found.Contains(x)))
        {
            UnavailableFormatFilters.Add(new FormatFilterOption
            {
                Extension = extension,
                Count = 0,
                IsSelected = false
            });
        }
        OnPropertyChanged(nameof(UnavailableFormatCount));
    }

    private void UpdateFilterBadge()
    {
        if (FilterBadge is null) return;
        var count = 0;
        if (_filter != LibraryFilter.All) count++;
        if (!string.IsNullOrWhiteSpace(_selectedSourceRoot)) count++;
        if ((DateFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string != "any") count++;
        if ((SizeFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string != "any") count++;
        if (FormatFilters.Any(x => !x.IsSelected)) count++;
        FilterBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterBadgeText.Text = count.ToString();
    }

    private void SelectSortMode(string mode)
    {
        foreach (var item in SortCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, mode, StringComparison.OrdinalIgnoreCase))
            {
                SortCombo.SelectedItem = item;
                return;
            }
        }
        SortCombo.SelectedIndex = 0;
    }

    private static void SelectComboMode(ComboBox comboBox, string mode)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag as string, mode, StringComparison.OrdinalIgnoreCase));
        if (comboBox.SelectedIndex < 0) comboBox.SelectedIndex = 0;
    }

    private static string FormatNumber(long value) => value.ToString("N0").Replace(',', ' ');
    private static string ResultLabel(int count) => $"{FormatNumber(count)} " + Declension(count, "файл", "файла", "файлов");
    private static string Declension(int number, string one, string few, string many)
    {
        var n = Math.Abs(number) % 100;
        if (n is >= 11 and <= 19) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    private static string ShortRoot(string path)
    {
        try { return new DirectoryInfo(path).Name is { Length: > 0 } name ? name : path; }
        catch { return path; }
    }

    private static string RootSignature(IEnumerable<string> roots) =>
        string.Join("|", roots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    private static bool IsWithinRoot(string path, string root)
    {
        var normalized = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path)?.TrimEnd('\\', '/');
        return string.Equals(path.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase);
    }

    private static string SourceName(string root, bool isDrive)
    {
        if (!isDrive)
        {
            try { return new DirectoryInfo(root).Name is { Length: > 0 } name ? name : root; }
            catch { return root; }
        }
        try
        {
            var drive = new DriveInfo(root);
            var letter = drive.Name.TrimEnd('\\');
            return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? letter : $"{drive.VolumeLabel} ({letter})";
        }
        catch { return root.TrimEnd('\\'); }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { FilterButton.IsChecked = FilterButton.IsChecked != true; e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F5) { if (!_isScanning) _ = StartScanAsync(); e.Handled = true; }
        else if (e.Key == Key.Enter && GalleryList.SelectedItem is MediaItem item) { OpenItem(item); e.Handled = true; }
        else if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.ResizeBorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(7);
        if (MaximizeButton is null) return;
        MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M 2,0.7 L 9.3,0.7 9.3,8 M 0.7,2 L 8,2 8,9.3 0.7,9.3 Z"
            : "M 0.7,0.7 L 9.3,0.7 9.3,9.3 0.7,9.3 Z");
        WindowBorder.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(12);
        WindowBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }

    private IntPtr WindowMessageHook(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int getMinMaxInfo = 0x0024;
        if (message != getMinMaxInfo) return IntPtr.Zero;

        var monitor = MonitorFromWindow(windowHandle, 2);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var monitorInfo = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

        var minMax = Marshal.PtrToStructure<NativeMinMaxInfo>(lParam);
        minMax.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMax.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMax.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMax.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        minMax.MaxTrackSize = minMax.MaxSize;
        Marshal.StructureToPtr(minMax, lParam, false);
        return IntPtr.Zero;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        App.WriteStartupLog("Main window closing");
        _scanCancellation?.Cancel();
        _driveCheckTimer.Stop();
        _watcher?.Dispose();
        _settings.TileWidth = TileWidth;
        _settings.ExtraFolders = ExtraFolders.ToList();
        _settings.Save();
        _thumbnails.Dispose();
    }

    private async void ScheduleQaScreenshot()
    {
        var target = Environment.GetEnvironmentVariable("PICALL_QA_SCREENSHOT");
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            if (string.Equals(Environment.GetEnvironmentVariable("PICALL_QA_MAXIMIZED"), "1", StringComparison.Ordinal))
                WindowState = WindowState.Maximized;
            await Task.Delay(700);
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var width = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
            var height = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
            encoder.Save(stream);
            App.WriteStartupLog($"QA screenshot saved: {target}");

            var filterTarget = Environment.GetEnvironmentVariable("PICALL_QA_FILTERS_SCREENSHOT");
            if (!string.IsNullOrWhiteSpace(filterTarget))
            {
                FilterButton.IsChecked = true;
                FilterPopup.IsOpen = true;
                if (string.Equals(Environment.GetEnvironmentVariable("PICALL_QA_EXPAND_FORMATS"), "1", StringComparison.Ordinal))
                    UnavailableFormatsToggle.IsChecked = true;
                await Task.Delay(180);
                if (FilterPopup.Child is FrameworkElement filterVisual)
                {
                    filterVisual.UpdateLayout();
                    var filterDpi = VisualTreeHelper.GetDpi(filterVisual);
                    var filterWidth = Math.Max(1, (int)Math.Ceiling(filterVisual.ActualWidth * filterDpi.DpiScaleX));
                    var filterHeight = Math.Max(1, (int)Math.Ceiling(filterVisual.ActualHeight * filterDpi.DpiScaleY));
                    var filterBitmap = new RenderTargetBitmap(filterWidth, filterHeight, filterDpi.PixelsPerInchX, filterDpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    filterBitmap.Render(filterVisual);
                    var filterEncoder = new PngBitmapEncoder();
                    filterEncoder.Frames.Add(BitmapFrame.Create(filterBitmap));
                    using var filterStream = new FileStream(filterTarget, FileMode.Create, FileAccess.Write, FileShare.Read);
                    filterEncoder.Save(filterStream);
                }
                FilterButton.IsChecked = false;
            }

            var menuTarget = Environment.GetEnvironmentVariable("PICALL_QA_MENU_SCREENSHOT");
            if (!string.IsNullOrWhiteSpace(menuTarget) &&
                GalleryList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem firstContainer &&
                FindVisualChild<ContentPresenter>(firstContainer) is { } presenter &&
                presenter.ContentTemplate?.FindName("Card", presenter) is Border firstCard &&
                firstCard.ContextMenu is { } menu)
            {
                menu.PlacementTarget = firstCard;
                menu.IsOpen = true;
                await Task.Delay(180);
                menu.UpdateLayout();
                var menuDpi = VisualTreeHelper.GetDpi(menu);
                var menuWidth = Math.Max(1, (int)Math.Ceiling(menu.ActualWidth * menuDpi.DpiScaleX));
                var menuHeight = Math.Max(1, (int)Math.Ceiling(menu.ActualHeight * menuDpi.DpiScaleY));
                var menuBitmap = new RenderTargetBitmap(menuWidth, menuHeight, menuDpi.PixelsPerInchX, menuDpi.PixelsPerInchY, PixelFormats.Pbgra32);
                menuBitmap.Render(menu);
                var menuEncoder = new PngBitmapEncoder();
                menuEncoder.Frames.Add(BitmapFrame.Create(menuBitmap));
                using var menuStream = new FileStream(menuTarget, FileMode.Create, FileAccess.Write, FileShare.Read);
                menuEncoder.Save(menuStream);
                menu.IsOpen = false;
            }
        }
        catch (Exception ex) { TryLog(ex); }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) return typed;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void TryEnableModernWindow()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            var backdrop = 2;
            DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
            var corners = 2;
            DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        }
        catch { }
    }

    private static void TryLog(Exception exception)
    {
        try { File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:O}  {exception}\n\n"); } catch { }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref NativeMonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}

using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Windows.Graphics;
using Windows.UI;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Pickers;
using Windows.Storage.Search;
using WinRT.Interop;
using System.IO;   // for Path.Combine



namespace NoteIt
{
    public sealed partial class MainWindow : Window
    {
        private StorageFolder? _currentFolder;                 // the chosen music folder
        private PlaylistInfo? _activePlaylist;                 // which playlist is open
        private readonly List<StorageFile> _playlistDetailFiles = new(); // files shown in detail

        // === marquee state ===
        private readonly DispatcherTimer _marqueeTimer = new();
        private double _marqueeOffset = 0;
        private double _marqueeSpeedPxPerSec = 60; // tune speed
        private double _marqueeResetAfter = 0;


        private const string LastFolderToken = "NOTEIT_LAST_FOLDER";
        private const string PlaylistsFileName = "playlists.json";

        private readonly MediaPlayer _player = new();
        private readonly DispatcherTimer _transportTimer = new(); // periodic UI updates
        private readonly List<StorageFile> _playlist = new();
        private int _index = -1;

        private readonly List<StorageFile> _viewAllSongs = new();
        private readonly List<StorageFile> _viewPlaylist = new();
        private StorageFile? _currentFile;


        // Shuffle
        private bool _shuffle = false;
        private readonly Random _rng = new();
        private List<int> _shuffleBag = new();

        // Scrub/seek
        private bool _scrubbing = false;      // true while mouse is down on progress
        private double _scrubRatio = 0.0;     // 0..1 preview position
        private TimeSpan _naturalDuration = TimeSpan.Zero;

        // Block timer-driven UI while scrubbing / right after seek (prevents flicker)
        private bool _suspendTransportUI = false;
        private readonly DispatcherQueueTimer _resumeUiTimer;

        // Playlists (names only for now)
        private readonly ObservableCollection<PlaylistInfo> _playlists = new();

        // UI readiness gate to avoid SelectionChanged firing during XAML load
        private bool _uiReady = false;

        public MainWindow()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                // If XAML parsing fails, write it to LocalState and rethrow (so you see the line)
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                        "NoteIt_InitComponent.txt");
                File.WriteAllText(path, ex.ToString());
                throw;
            }

            // ---- Safe mode guards: nothing here should crash the app even if assets are missing ----
            try
            {
                // If TitleBar isn't ready, this will just no-op
                this.ExtendsContentIntoTitleBar = true;
                this.SetTitleBar(TitleBar);
            }
            catch { }

            try
            {
                // AppWindow plumbing
                var hwnd = WindowNative.GetWindowHandle(this);
                var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);

                // Use the same black as XAML (no transparency)
                var chrome = Windows.UI.Color.FromArgb(255, 0x12, 0x15, 0x19);

                var tb = appWindow.TitleBar;
                tb.ExtendsContentIntoTitleBar = true;

                // Also set the caption background so the OS doesn’t draw a light stripe
                tb.BackgroundColor = chrome;
                tb.InactiveBackgroundColor = chrome;

                // Caption buttons over black
                tb.ButtonBackgroundColor = Colors.Transparent;
                tb.ButtonInactiveBackgroundColor = Colors.Transparent;
                tb.ButtonForegroundColor = Color.FromArgb(255, 245, 249, 255);
                tb.ButtonHoverBackgroundColor = Color.FromArgb(30, 126, 199, 255);
                tb.ButtonPressedBackgroundColor = Color.FromArgb(60, 126, 199, 255);


                // Icon only if it actually exists (SetIcon throws on bad paths)
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "NoteIt.ico");
                if (File.Exists(iconPath))
                    appWindow.SetIcon(iconPath);

                appWindow?.Resize(new Windows.Graphics.SizeInt32(900, 600));
            }
            catch { /* swallow styling issues so the app still opens */ }


            _player.Volume = VolumeSlider.Value / 100.0;   // = 1.0 at startup

            // Force the initial view on startup
            ViewPicker.SelectedIndex = 0;   // shows "All Songs" in the dropdown
            ApplyView(0);                   // makes the AllSongs panel visible

            // Marquee timer (~60 FPS is overkill; 30–45ms looks fine)
            _marqueeTimer.Interval = TimeSpan.FromMilliseconds(33);
            _marqueeTimer.Tick += (_, __) =>
            {
                if (NowPlayingScroller == null || NowPlayingText1 == null) return;

                _marqueeOffset += _marqueeSpeedPxPerSec * _marqueeTimer.Interval.TotalSeconds;
                if (_marqueeOffset >= _marqueeResetAfter)
                    _marqueeOffset = 0;

                NowPlayingScroller.ScrollToHorizontalOffset(_marqueeOffset);
            };

            // Re-evaluate when the footer resizes
            NowPlayingScroller.SizeChanged += (_, __) => ApplyMarqueeSizing();


            // Window title + size
            this.Title = "NoteIt";

            // Transport timer
            _transportTimer.Interval = TimeSpan.FromMilliseconds(200);
            _transportTimer.Tick += (_, __) => UpdateTransport();
            _transportTimer.Start();

            // Resume-UI timer (short delay post-seek to avoid one-frame snap)
            _resumeUiTimer = DispatcherQueue.CreateTimer();
            _resumeUiTimer.Interval = TimeSpan.FromMilliseconds(120);
            _resumeUiTimer.IsRepeating = false;
            _resumeUiTimer.Tick += (_, __) => _suspendTransportUI = false;

            // Media events
            _player.MediaEnded += (_, __) => DispatcherQueue.TryEnqueue(Next);
            _player.PlaybackSession.PlaybackStateChanged += (_, __) =>
                DispatcherQueue.TryEnqueue(UpdatePlayButton);

            UpdateShuffleButton();

            // Bind playlists list and load saved playlists (binding finalized on Loaded)
            PlaylistsList.ItemsSource = _playlists;
            _ = LoadPlaylistsAsync();

            // Initialize view ONLY after visual tree is ready
            if (Content is FrameworkElement fe)
                fe.Loaded += MainWindow_Loaded;

            // Auto-load last folder if available
            _ = TryLoadLastFolderAsync();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _uiReady = true;

            // Make sure ItemsSource is set (safe to set again)
            PlaylistsList.ItemsSource = _playlists;

            // Apply the current selection (0 = All Songs) now that named elements exist
            ApplyView(ViewPicker?.SelectedIndex ?? 0);

        }

        // ===== View dropdown (All Songs / Playlists) =====
        private void ApplyView(int idx)
        {
            // Guard in case this is called pre-load
            if (AllSongsPanel == null || PlaylistsPanel == null || StatusText == null || PlaylistDetailPanel == null) return;

            if (idx == 0) // All Songs
            {
                AllSongsPanel.Visibility = Visibility.Visible;
                PlaylistsPanel.Visibility = Visibility.Collapsed;
                PlaylistDetailPanel.Visibility = Visibility.Collapsed;
                StatusText.Text = "All Songs";
            }
            else // Playlists
            {
                AllSongsPanel.Visibility = Visibility.Collapsed;
                PlaylistDetailPanel.Visibility = Visibility.Collapsed; // ensure we start at list view
                PlaylistsPanel.Visibility = Visibility.Visible;
                StatusText.Text = "Playlists";
            }
            ApplySearchFilter();


        }

        // ===== Folder & playlist =====
        private async void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) { StatusText.Text = "Canceled"; return; }

            StorageApplicationPermissions.FutureAccessList.AddOrReplace(LastFolderToken, folder);
            await LoadFolderAsync(folder);
        }

        private async System.Threading.Tasks.Task TryLoadLastFolderAsync()
        {
            try
            {
                if (StorageApplicationPermissions.FutureAccessList.ContainsItem(LastFolderToken))
                {
                    var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(LastFolderToken);
                    await LoadFolderAsync(folder);
                }
            }
            catch { /* token invalid / folder moved */ }
        }

        private async System.Threading.Tasks.Task LoadFolderAsync(StorageFolder folder)
        {
            _currentFolder = folder;

            _playlist.Clear();

            var q = new QueryOptions(CommonFileQuery.DefaultQuery, new[] { ".mp3", ".m4a", ".wav" });
            var files = await folder.CreateFileQueryWithOptions(q).GetFilesAsync();

            foreach (var f in files.OrderBy(f => f.Name))
                _playlist.Add(f);

            TracksListView.ItemsSource = null; // we bind the view list, not _playlist
            RebuildAllSongsView();

            if (_playlist.Count > 0)
            {
                _index = 0;
                TracksListView.SelectedIndex = 0;
                TracksListView.ScrollIntoView(TracksListView.SelectedItem);
                StatusText.Text = $"Loaded {_playlist.Count} tracks";
                RefillShuffleBag(_index, GetActiveView().Count);
                await PlayIndexAsync(_index);
            }
            else
            {
                _index = -1;
                StatusText.Text = "No audio files found";
            }
        }

        // ===== Playback =====
        private async System.Threading.Tasks.Task PlayIndexAsync(int idx)
        {
            var scope = GetPlayScope();
            var listView = GetScopeListView();
            if (idx < 0 || idx >= scope.Count) return;

            _index = idx;
            listView.SelectedIndex = idx;
            listView.ScrollIntoView(listView.SelectedItem);

            var file = scope[idx];

            _currentFile = file; // <— remember what's actually playing
            _player.Source = MediaSource.CreateFromStorageFile(file);
            _player.Play();

            UpdatePlayButton();
            SetNowPlayingTitle(file.DisplayName);


            RefillShuffleBag(_index, scope.Count);
            await System.Threading.Tasks.Task.CompletedTask;
        }



        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            var state = _player.PlaybackSession.PlaybackState;

            // If something is currently playing, pause it.
            if (state == MediaPlaybackState.Playing)
            {
                _player.Pause();
                StatusText.Text = "Paused";
                UpdatePlayButton();
                return;
            }

            // If we already have a media source (i.e., we paused earlier), just resume
            // regardless of what list/view is currently shown.
            if (_player.Source != null)
            {
                _player.Play();

                if (_currentFile != null)
                    SetNowPlayingTitle(_currentFile.DisplayName);   // <-- FIX: use _currentFile

                UpdatePlayButton();
                return;
            }

            // Otherwise, nothing has been started yet — start the first item in the current scope
            if (_index >= 0)
            {
                var scope = GetPlayScope();
                if (_index < scope.Count)
                    SetNowPlayingTitle(scope[_index].DisplayName);  // optional: mirrors your status update
            }

            UpdatePlayButton();
        }




        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            var scope = GetPlayScope();
            if (scope.Count == 0) return;

            // If we don’t know our index in this view, try to locate the current file
            var idx = _index;
            if (idx < 0 && _currentFile != null)
                idx = scope.IndexOf(_currentFile);

            var next = (idx - 1 + scope.Count) % scope.Count;
            _ = PlayIndexAsync(next);
        }


        private void NextButton_Click(object sender, RoutedEventArgs e) => Next();

        private void Next()
        {
            // 1) If anything queued, that always wins
            if (_playQueue.Count > 0)
            {
                var f = _playQueue[0];
                _playQueue.RemoveAt(0);
                PlayFile(f, isQueued: true);

                // refresh displayed queue count if the dialog is open is optional.
                return;
            }

            // 2) Otherwise fall back to current scope (your existing code)
            var scope = GetPlayScope();
            if (scope.Count == 0) return;

            if (_shuffle)
            {
                if (_shuffleBag.Count == 0)
                    RefillShuffleBag(_index, scope.Count);

                var pick = _rng.Next(_shuffleBag.Count);
                var nextIdx = _shuffleBag[pick];
                _shuffleBag.RemoveAt(pick);
                _ = PlayIndexAsync(nextIdx);
            }
            else
            {
                var next = (_index + 1) % scope.Count;
                _ = PlayIndexAsync(next);
            }
        }



        private void UpdatePlayButton()
        {
            var state = _player.PlaybackSession.PlaybackState;
            PlayPauseButton.Content = state == MediaPlaybackState.Playing ? "⏸ Pause" : "▶ Play";
        }

        // ===== Shuffle =====
        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            _shuffle = !_shuffle;
            RefillShuffleBag(_index, GetPlayScope().Count);
            UpdateShuffleButton();
        }


        private void RefillShuffleBag(int excludeIndex, int count)
        {
            _shuffleBag = Enumerable.Range(0, count).ToList();
            if (excludeIndex >= 0 && excludeIndex < _shuffleBag.Count)
                _shuffleBag.Remove(excludeIndex);
        }


        private void UpdateShuffleButton()
        {
            ShuffleButton.Content = _shuffle ? "🔀 Shuffle On" : "🔀 Shuffle Off";
        }

        
        // ===== Volume =====
        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _player.Volume = Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        }

        // ===== Progress / Seek =====
        private void UpdateTransport()
        {
            if (_suspendTransportUI) return;

            var session = _player.PlaybackSession;
            _naturalDuration = session.NaturalDuration;

            if (_naturalDuration > TimeSpan.Zero)
                DurationText.Text = ToMmSs(_naturalDuration);
            if (_naturalDuration <= TimeSpan.Zero) return;

            var pos = session.Position;
            CurrentTimeText.Text = ToMmSs(pos);
            var ratio = Math.Clamp(pos.TotalMilliseconds / _naturalDuration.TotalMilliseconds, 0, 1);
            PositionSlider.Value = ratio * 1000.0;
        }

        private void SeekToRatio(double ratio)
        {
            if (_naturalDuration <= TimeSpan.Zero) return;
            ratio = Math.Clamp(ratio, 0, 1);
            var target = TimeSpan.FromTicks((long)(_naturalDuration.Ticks * ratio));
            _player.PlaybackSession.Position = target;

            PositionSlider.Value = ratio * 1000.0;
            CurrentTimeText.Text = ToMmSs(target);
        }

        private static double RatioFromPoint(FrameworkElement element, double x)
            => Math.Clamp(x / Math.Max(1.0, element.ActualWidth), 0, 1);

        private void BeginScrub(double ratio)
        {
            _scrubbing = true;
            _suspendTransportUI = true;
            _scrubRatio = Math.Clamp(ratio, 0, 1);

            if (_naturalDuration > TimeSpan.Zero)
            {
                PositionSlider.Value = _scrubRatio * 1000.0;
                var preview = TimeSpan.FromTicks((long)(_naturalDuration.Ticks * _scrubRatio));
                CurrentTimeText.Text = ToMmSs(preview);
            }
        }

        private void ContinueScrub(double ratio)
        {
            if (!_scrubbing) return;
            _scrubRatio = Math.Clamp(ratio, 0, 1);

            if (_naturalDuration > TimeSpan.Zero)
            {
                PositionSlider.Value = _scrubRatio * 1000.0;
                var preview = TimeSpan.FromTicks((long)(_naturalDuration.Ticks * _scrubRatio));
                CurrentTimeText.Text = ToMmSs(preview);
            }
        }

        private void EndScrubApply(double ratio)
        {
            _scrubbing = false;
            SeekToRatio(ratio);

            // brief pause so the next playback tick won't snap back
            _suspendTransportUI = true;
            _resumeUiTimer.Stop();
            _resumeUiTimer.Start();
        }

        // Click to seek
        private void PositionSlider_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_naturalDuration <= TimeSpan.Zero) return;
            var ratio = RatioFromPoint(PositionSlider, e.GetPosition(PositionSlider).X);
            SeekToRatio(ratio);
            _suspendTransportUI = true;
            _resumeUiTimer.Stop();
            _resumeUiTimer.Start();
            e.Handled = true;
        }

        // Drag to scrub
        private void PositionSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_naturalDuration <= TimeSpan.Zero) return;
            PositionSlider.CapturePointer(e.Pointer);
            var pt = e.GetCurrentPoint(PositionSlider);
            BeginScrub(RatioFromPoint(PositionSlider, pt.Position.X));
            e.Handled = true;
        }

        private void PositionSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_scrubbing) return;
            var pt = e.GetCurrentPoint(PositionSlider);
            if (pt.IsInContact && pt.Properties.IsLeftButtonPressed)
            {
                ContinueScrub(RatioFromPoint(PositionSlider, pt.Position.X));
                e.Handled = true;
            }
        }

        private void PositionSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            PositionSlider.ReleasePointerCaptures();
            var pt = e.GetCurrentPoint(PositionSlider);
            EndScrubApply(RatioFromPoint(PositionSlider, pt.Position.X));
            e.Handled = true;
        }

        private void PositionSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            PositionSlider.ReleasePointerCaptures();
            _scrubbing = false;
            _suspendTransportUI = true;
            _resumeUiTimer.Stop();
            _resumeUiTimer.Start();
        }

        // Thumb value changes while scrubbing = preview only
        private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_scrubbing || _naturalDuration <= TimeSpan.Zero) return;

            _scrubRatio = PositionSlider.Value / 1000.0;
            var preview = TimeSpan.FromTicks((long)(_naturalDuration.Ticks * _scrubRatio));
            CurrentTimeText.Text = ToMmSs(preview);
        }

        // ===== Search =====
        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
        {
            // We don’t use suggestions anymore; just filter live
            ApplySearchFilter();
            sender.ItemsSource = null;     // keep the dropdown closed
        }

        // ===== Playlists: load/save & create (source-gen, no IL2026) =====
        private async System.Threading.Tasks.Task LoadPlaylistsAsync()
        {
            try
            {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(PlaylistsFileName);
                if (item is StorageFile file)
                {
                    var json = await FileIO.ReadTextAsync(file);
                    var list = JsonSerializer.Deserialize(
                        json,
                        NoteItJsonContext.Default.ListPlaylistInfo
                    ) ?? new List<PlaylistInfo>();

                    _playlists.Clear();
                    foreach (var p in list) _playlists.Add(p);
                }
            }
            catch { /* ignore parse errors */ }
        }

        private async System.Threading.Tasks.Task SavePlaylistsAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    PlaylistsFileName, CreationCollisionOption.ReplaceExisting);

                var json = JsonSerializer.Serialize(
                    _playlists.ToList(),
                    NoteItJsonContext.Default.ListPlaylistInfo
                );

                await FileIO.WriteTextAsync(file, json);
            }
            catch { /* ignore IO errors for now */ }
        }

        private async void CreatePlaylist_Click(object sender, RoutedEventArgs e)
        {
            var tb = new TextBox { PlaceholderText = "Playlist name", Width = 320 };
            var dlg = new ContentDialog
            {
                Title = "Create playlist",
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = tb,
                XamlRoot = (this.Content as FrameworkElement)!.XamlRoot
            };
            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            var name = tb.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                StatusText.Text = "Please enter a name.";
                return;
            }
            if (_playlists.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText.Text = "A playlist with that name already exists.";
                return;
            }

            _playlists.Add(new PlaylistInfo { Name = name });
            await SavePlaylistsAsync();
            StatusText.Text = $"Created playlist: {name}";
        }

        // ===== Helpers =====
        private static string ToMmSs(TimeSpan t)
        {
            if (t <= TimeSpan.Zero) return "00:00";
            return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }

        private async System.Threading.Tasks.Task ShowPlaylistAsync(PlaylistInfo pl)
        {
            _activePlaylist = pl;
            PlaylistTitleText.Text = pl.Name;

            _playlistDetailFiles.Clear();

            if (_currentFolder == null)
            {
                StatusText.Text = "Choose a music folder to view playlist contents.";
            }
            else
            {
                foreach (var rel in pl.Tracks)
                {
                    try
                    {
                        var item = await _currentFolder.TryGetItemAsync(rel);
                        if (item is StorageFile f) _playlistDetailFiles.Add(f);
                    }
                    catch { /* ignore missing files */ }
                }
            }

            // 🔑 Make detail view active BEFORE filtering so ApplyFilter targets it
            PlaylistsPanel.Visibility = Visibility.Collapsed;
            PlaylistDetailPanel.Visibility = Visibility.Visible;

            // Build & bind the *view* list for the playlist
            RebuildPlaylistView();

            StatusText.Text = _playlistDetailFiles.Count == 0
                ? "This playlist has no songs yet."
                : $"Loaded {_playlistDetailFiles.Count} song(s).";
            
            ApplySearchFilter();

        }


        private async void PlaylistsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is PlaylistInfo pl)
                await ShowPlaylistAsync(pl);
        }

        private void BackFromPlaylist_Click(object sender, RoutedEventArgs e)
        {
            PlaylistDetailPanel.Visibility = Visibility.Collapsed;
            PlaylistsPanel.Visibility = Visibility.Visible;
            _activePlaylist = null;
        }

        private void AnyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var lv = (ListView)sender;
            if (lv.SelectedItem is not StorageFile file) return;

            // Resolve index in the *current* view (ItemsSource is your filtered view list)
            if (lv.ItemsSource is IList<StorageFile> scope)
            {
                int idx = scope.IndexOf(file);
                if (idx >= 0 && idx != _index)
                    _ = PlayIndexAsync(idx);
            }
        }

        private void AnyList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not StorageFile file) return;

            var lv = (ListView)sender;
            if (lv.ItemsSource is IList<StorageFile> scope)
            {
                int idx = scope.IndexOf(file);
                if (idx >= 0)
                    _ = PlayIndexAsync(idx);
            }
        }





        private void ViewPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return; // ignore constructor-time event during InitializeComponent
            var idx = (sender as ComboBox)?.SelectedIndex ?? 0;
            ApplyView(idx);
        }

        private static string MakeRelative(StorageFolder root, StorageFile file)
        {
            try
            {
                var rp = root.Path.Replace('/', '\\').TrimEnd('\\');
                var fp = file.Path.Replace('/', '\\');
                if (fp.StartsWith(rp + "\\", StringComparison.OrdinalIgnoreCase))
                    return fp.Substring(rp.Length + 1);
            }
            catch { }
            // Fallback: just the file name
            return file.Name;
        }

        private async void AddSongs_Click(object sender, RoutedEventArgs e)
        {
            if (_activePlaylist is null)
            {
                StatusText.Text = "Open a playlist first.";
                return;
            }
            if (_currentFolder is null)
            {
                StatusText.Text = "Choose a music folder first.";
                return;
            }
            if (_playlist.Count == 0)
            {
                StatusText.Text = "No songs in the current folder.";
                return;
            }

            // --- Build candidates: All Songs – already in the playlist ---
            var existing = new HashSet<string>(_activePlaylist.Tracks ?? new List<string>(),
                                               StringComparer.OrdinalIgnoreCase);

            var candidates = new List<(StorageFile File, string RelPath, string Display)>();
            foreach (var f in _playlist)
            {
                var rel = MakeRelative(_currentFolder, f);
                if (!existing.Contains(rel))
                    candidates.Add((f, rel, f.DisplayName));
            }

            if (candidates.Count == 0)
            {
                var noneDlg = new ContentDialog
                {
                    Title = $"Add songs to \"{_activePlaylist.Name}\"",
                    Content = "All songs from this folder are already in the playlist.",
                    CloseButtonText = "OK",
                    XamlRoot = (this.Content as FrameworkElement)!.XamlRoot
                };
                _ = await noneDlg.ShowAsync();
                return;
            }

            // --- UI: filter box + scrollable checkbox list ---
            var root = new StackPanel { Spacing = 8 };

            var filterBox = new TextBox
            {
                PlaceholderText = "Filter songs…",
                Width = 520
            };
            root.Children.Add(filterBox);

            var listPanel = new StackPanel { Spacing = 8 };
            var scroller = new ScrollViewer
            {
                Content = listPanel,
                Height = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            root.Children.Add(scroller);

            // Keep references so we can filter quickly
            var rows = new List<(CheckBox Box, string Text)>(candidates.Count);

            foreach (var c in candidates.OrderBy(c => c.Display, StringComparer.OrdinalIgnoreCase))
            {
                var cb = new CheckBox
                {
                    Content = c.Display,
                    Tag = c,          // store (File, RelPath, Display)
                    MinWidth = 300
                };
                rows.Add((cb, c.Display));
                listPanel.Children.Add(cb);
            }

            // Live filter: show/hide rows as you type (case-insensitive "contains")
            void ApplyFilter(string? q)
            {
                q ??= string.Empty;
                foreach (var r in rows)
                {
                    bool match = r.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    r.Box.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            filterBox.TextChanged += (_, __) => ApplyFilter(filterBox.Text);

            var dlg = new ContentDialog
            {
                Title = $"Add songs to \"{_activePlaylist.Name}\"",
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = root,
                XamlRoot = (this.Content as FrameworkElement)!.XamlRoot
            };

            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            // --- Apply additions (checked boxes only) ---
            var added = 0;
            foreach (var (box, _) in rows)
            {
                if (box.IsChecked == true)
                {
                    var picked = ((StorageFile File, string RelPath, string Display))box.Tag!;
                    if (existing.Add(picked.RelPath))
                    {
                        _activePlaylist.Tracks.Add(picked.RelPath);
                        added++;
                    }
                }
            }

            if (added == 0)
            {
                StatusText.Text = "No songs were selected.";
                return;
            }

            await SavePlaylistsAsync();
            await ShowPlaylistAsync(_activePlaylist);   // refresh the detail page
            StatusText.Text = $"Added {added} song(s) to \"{_activePlaylist.Name}\".";
        }



        // Current playback scope: all songs or the open playlist
        private IList<StorageFile> GetPlayScope()
        {
            var lv = GetScopeListView();
            if (lv.ItemsSource is IList<StorageFile> view)
                return view;

            // Fall back to base scopes if ItemsSource isn't an IList<StorageFile>
            return (PlaylistDetailPanel.Visibility == Visibility.Visible && _activePlaylist != null)
                ? (IList<StorageFile>)_playlistDetailFiles
                : _playlist;
        }


        // The ListView associated with the current scope
        private ListView GetScopeListView()
            => (PlaylistDetailPanel.Visibility == Visibility.Visible && _activePlaylist != null)
               ? PlaylistTracksList
               : TracksListView;

        // Keeps track of the file for the currently opened "⋮" menu
        private StorageFile? _menuTargetFile;

        // Capture which StorageFile the flyout is acting on
        private void PlaylistItemFlyout_Opening(object sender, object e)
        {
            var fe = (sender as MenuFlyout)?.Target as FrameworkElement;
            _menuTargetFile = fe?.DataContext as StorageFile;
        }

        // Small helper to get a safe relative path for a file
        private string MakeRelativeSafe(StorageFile f)
        {
            try
            {
                if (_currentFolder != null)
                    return MakeRelative(_currentFolder, f);
            }
            catch { /* ignore */ }
            return f.Name;
        }

        // ===== Build the flyout dynamically per row =====
        private void MoreFlyout_Opening(object sender, object e)
        {
            // The flyout sits on the row's Button; its DataContext is the row's StorageFile
            if (sender is MenuFlyout flyout &&
                (flyout.Target as FrameworkElement)?.DataContext is StorageFile file)
            {
                var addQ = flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => i.Name == "QueueAddMenu");
                var viewQ = flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => i.Name == "ViewQueueMenu");
                if (addQ != null) addQ.Tag = file;
                if (viewQ != null) viewQ.Tag = file;

                // Tag the remove item with the file (so handler knows which one)
                var removeItem = flyout.Items.OfType<MenuFlyoutItem>()
                                             .FirstOrDefault(i => i.Name == "RemoveThisMenu");
                if (removeItem != null)
                    removeItem.Tag = file;

                // Build the "Add to other playlist" submenu
                var addSub = flyout.Items.OfType<MenuFlyoutSubItem>()
                                         .FirstOrDefault(i => i.Name == "AddToOtherMenu");
                if (addSub == null) return;

                addSub.Items.Clear();

                // Exclude the current playlist and any playlists that already contain this track
                var rel = MakeRelativeSafe(file);

                var candidates = _playlists
                    .Where(p => _activePlaylist == null || !string.Equals(p.Name, _activePlaylist.Name, StringComparison.OrdinalIgnoreCase))
                    .Where(p => !p.Tracks.Contains(rel, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(p => p.Name)
                    .ToList();

                if (candidates.Count == 0)
                {
                    addSub.Items.Add(new MenuFlyoutItem
                    {
                        Text = "No other playlists available",
                        IsEnabled = false
                    });
                    return;
                }

                foreach (var pl in candidates)
                {
                    var mi = new MenuFlyoutItem
                    {
                        Text = pl.Name,
                        // store both playlist + file for the click handler
                        Tag = Tuple.Create(pl, file)
                    };
                    mi.Click += AddToOtherPlaylist_Click;
                    addSub.Items.Add(mi);
                }
            }
        }

        // ===== Remove from this playlist =====
        private async void RemoveFromThisPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (_activePlaylist == null) return;

            var file = (sender as FrameworkElement)?.Tag as StorageFile;
            if (file == null) return;

            var rel = MakeRelativeSafe(file);
            if (_activePlaylist.Tracks.Remove(rel))
            {
                await SavePlaylistsAsync();
                await ShowPlaylistAsync(_activePlaylist);
                StatusText.Text = $"Removed: {file.DisplayName}";
            }
        }

        // ===== Add to other playlist (from submenu item) =====
        private async void AddToOtherPlaylist_Click(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as Tuple<PlaylistInfo, StorageFile>;
            if (tag == null) return;

            var (targetPlaylist, file) = tag;

            var rel = MakeRelativeSafe(file);
            if (!targetPlaylist.Tracks.Contains(rel, StringComparer.OrdinalIgnoreCase))
            {
                targetPlaylist.Tracks.Add(rel);
                await SavePlaylistsAsync();
                StatusText.Text = $"Added to '{targetPlaylist.Name}': {file.DisplayName}";
            }
            else
            {
                StatusText.Text = $"Already in '{targetPlaylist.Name}'.";
            }
        }

        // Builds the “Add to playlist” submenu for All Songs rows
        private void AllSongsFlyout_Opening(object sender, object e)
        {
            if (sender is MenuFlyout flyout &&
                (flyout.Target as FrameworkElement)?.DataContext is StorageFile file)
            {
                var addQ = flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => i.Name == "QueueAddMenu");
                var viewQ = flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => i.Name == "ViewQueueMenu");
                if (addQ != null) addQ.Tag = file;
                if (viewQ != null) viewQ.Tag = file;

                // In this flyout we only have the AddToOtherMenu subitem
                var addSub = flyout.Items.OfType<MenuFlyoutSubItem>()
                                         .FirstOrDefault(i => i.Name == "AddToOtherMenu");
                if (addSub == null) return;

                addSub.Items.Clear();

                var rel = MakeRelativeSafe(file);

                // Every playlist that does not already contain this track
                var candidates = _playlists
                    .Where(p => !p.Tracks.Contains(rel, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(p => p.Name)
                    .ToList();

                if (candidates.Count == 0)
                {
                    addSub.Items.Add(new MenuFlyoutItem
                    {
                        Text = "No available playlists",
                        IsEnabled = false
                    });
                    return;
                }

                foreach (var pl in candidates)
                {
                    var mi = new MenuFlyoutItem
                    {
                        Text = pl.Name,
                        Tag = Tuple.Create(pl, file)   // (PlaylistInfo, StorageFile)
                    };
                    mi.Click += AddToOtherPlaylist_Click;  // you already have this handler
                    addSub.Items.Add(mi);
                }
            }
        }
        // Simple play queue: songs to play after the current one
        private readonly List<StorageFile> _playQueue = new();

        private void PlayFile(StorageFile file, bool isQueued = false)
        {
            _currentFile = file;                     // 🔑 remember what is actually playing
            SetNowPlayingTitle(file.DisplayName);    // 🔑 update the bottom-left title

            _player.Source = MediaSource.CreateFromStorageFile(file);
            _player.Play();
            UpdatePlayButton();
            StatusText.Text = isQueued
                ? $"Playing (queued): {file.DisplayName}"
                : $"Playing: {file.DisplayName}";
        }

        private void AddToQueue_Click(object sender, RoutedEventArgs e)
        {
            var file = (sender as FrameworkElement)?.Tag as StorageFile;
            if (file is null) return;

            _playQueue.Add(file);
            StatusText.Text = $"Queued: {file.DisplayName}  ({_playQueue.Count} in queue)";
        }

        private async void ViewQueue_Click(object sender, RoutedEventArgs e)
        {
            // Snapshot the next 10 (so we can edit this list without iterating the real queue)
            var top = _playQueue.Take(10).ToList();

            // Header text
            var header = new TextBlock
            {
                Text = _playQueue.Count == 0
                    ? "Queue is empty."
                    : $"Showing next {Math.Min(10, _playQueue.Count)} of {_playQueue.Count}",
                Margin = new Thickness(0, 0, 0, 8)
            };

            // Build rows manually (no DataTemplate, no bindings)
            var rows = new StackPanel { Spacing = 6 };

            void RebuildRows()
            {
                rows.Children.Clear();

                foreach (var f in top)
                {
                    var row = new Grid
                    {
                        MinHeight = 38,
                        Padding = new Thickness(8, 4, 8, 4)
                    };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var name = new TextBlock
                    {
                        Text = f.DisplayName,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };

                    var removeBtn = new Button
                    {
                        Content = "Remove",
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Tag = f
                    };
                    removeBtn.Click += (_, __) =>
                    {
                        // Remove first match from the real queue
                        var i = _playQueue.FindIndex(x => x.Path == f.Path);
                        if (i >= 0) _playQueue.RemoveAt(i);

                        // Remove from this dialog snapshot too
                        var j = top.FindIndex(x => x.Path == f.Path);
                        if (j >= 0) top.RemoveAt(j);

                        // Rebuild the visual list
                        RebuildRows();

                        // Update header text
                        header.Text = _playQueue.Count == 0
                            ? "Queue is empty."
                            : $"Showing next {Math.Min(10, _playQueue.Count)} of {_playQueue.Count}";

                        StatusText.Text = $"Removed from queue: {f.DisplayName}";
                    };

                    Grid.SetColumn(name, 0);
                    Grid.SetColumn(removeBtn, 1);
                    row.Children.Add(name);
                    row.Children.Add(removeBtn);

                    rows.Children.Add(row);
                }
            }

            RebuildRows();

            var scroller = new ScrollViewer
            {
                Content = rows,
                Height = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(header);
            content.Children.Add(scroller);

            var dlg = new ContentDialog
            {
                Title = "Up next (queue)",
                Content = content,
                PrimaryButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = (this.Content as FrameworkElement)!.XamlRoot
            };

            await dlg.ShowAsync();
        }

        private async void RenamePlaylist_Click(object sender, RoutedEventArgs e)
        {
            var pl = (sender as FrameworkElement)?.Tag as PlaylistInfo;
            if (pl is null) return;

            var tb = new TextBox { Text = pl.Name, Width = 320 };
            var dlg = new ContentDialog
            {
                Title = "Rename playlist",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = tb,
                XamlRoot = (this.Content as FrameworkElement)!.XamlRoot
            };

            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            var newName = tb.Text?.Trim();
            if (string.IsNullOrEmpty(newName))
            {
                StatusText.Text = "Please enter a name.";
                return;
            }
            // Prevent duplicate names
            if (_playlists.Any(p => !ReferenceEquals(p, pl) &&
                                    string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText.Text = "A playlist with that name already exists.";
                return;
            }

            // Apply rename
            pl.Name = newName;

            // If PlaylistInfo doesn't implement INotifyPropertyChanged, refresh binding:
            PlaylistsList.ItemsSource = null;
            PlaylistsList.ItemsSource = _playlists;

            if (ReferenceEquals(_activePlaylist, pl))
                PlaylistTitleText.Text = pl.Name;

            await SavePlaylistsAsync();
            StatusText.Text = $"Renamed playlist to \"{pl.Name}\".";
        }

        private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
        {
            var pl = (sender as FrameworkElement)?.Tag as PlaylistInfo;
            if (pl is null) return;

            var dlg = new ContentDialog
            {
                Title = "Delete playlist",
                Content = $"Are you sure you want to delete \"{pl.Name}\"?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = (this.Content as FrameworkElement)!.XamlRoot
            };

            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            // If currently viewing this playlist, go back to the list page
            if (ReferenceEquals(_activePlaylist, pl))
            {
                PlaylistDetailPanel.Visibility = Visibility.Collapsed;
                PlaylistsPanel.Visibility = Visibility.Visible;
                _activePlaylist = null;
            }

            _playlists.Remove(pl);

            // Refresh list (in case there’s no change notification)
            PlaylistsList.ItemsSource = null;
            PlaylistsList.ItemsSource = _playlists;

            await SavePlaylistsAsync();
            StatusText.Text = "Playlist deleted.";
        }

        private void RebuildAllSongsView()
        {
            _viewAllSongs.Clear();
            _viewAllSongs.AddRange(_playlist);
            ApplyFilter(SearchBox?.Text);
        }

        private void RebuildPlaylistView()
        {
            _viewPlaylist.Clear();
            _viewPlaylist.AddRange(_playlistDetailFiles);
            ApplyFilter(SearchBox?.Text);
        }

        private IList<StorageFile> GetActiveView()
        {
            // What’s currently displayed
            if (PlaylistDetailPanel.Visibility == Visibility.Visible && _activePlaylist != null)
                return _viewPlaylist;
            return _viewAllSongs;
        }

        // Live filter for whichever list is on screen
        private void ApplyFilter(string? q)
        {
            q ??= string.Empty;
            var activeAllSongs = !(PlaylistDetailPanel.Visibility == Visibility.Visible && _activePlaylist != null);

            if (activeAllSongs)
            {
                IEnumerable<StorageFile> src = _playlist;
                if (q.Length > 0)
                    src = src.Where(f => f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase));

                _viewAllSongs.Clear();
                _viewAllSongs.AddRange(src);
                TracksListView.ItemsSource = null;
                TracksListView.ItemsSource = _viewAllSongs;
            }
            else
            {
                IEnumerable<StorageFile> src = _playlistDetailFiles;
                if (q.Length > 0)
                    src = src.Where(f => f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase));

                _viewPlaylist.Clear();
                _viewPlaylist.AddRange(src);
                PlaylistTracksList.ItemsSource = null;
                PlaylistTracksList.ItemsSource = _viewPlaylist;
            }

            // Keep _index sane if it points past the filtered list
            if (_index >= GetActiveView().Count) _index = GetActiveView().Count - 1;
        }

        private void ApplySearchFilter()
        {
            // Base scope (unfiltered)
            var baseScope = (PlaylistDetailPanel.Visibility == Visibility.Visible && _activePlaylist != null)
                ? _playlistDetailFiles
                : _playlist;

            var q = SearchBox.Text?.Trim() ?? string.Empty;

            IList<StorageFile> view =
                string.IsNullOrEmpty(q)
                ? baseScope
                : baseScope
                    .Where(f => f.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList(); // important: a concrete list so ItemsSource is IList<StorageFile>

            var lv = GetScopeListView();
            lv.ItemsSource = null;
            lv.ItemsSource = view;

            // Keep selection synced to the file that’s currently playing (if present in view)
            _index = (_currentFile != null) ? view.IndexOf(_currentFile) : -1;
            lv.SelectedIndex = _index;
            if (_index >= 0) lv.ScrollIntoView(lv.SelectedItem);

            // Helpful status
            if (PlaylistDetailPanel.Visibility == Visibility.Visible && _activePlaylist != null)
                StatusText.Text = view.Count == 0 ? "No matches in this playlist." : $"{view.Count} match(es) in playlist.";
            else
                StatusText.Text = view.Count == 0 ? "No matches." : $"{view.Count} match(es).";
        }

        private void SetNowPlayingTitle(string title)
        {
            // No "Playing:" prefix anymore — just the title
            NowPlayingText1.Text = title;
            NowPlayingText2.Text = title;

            // force measure before sizing logic
            NowPlayingText1.UpdateLayout();
            NowPlayingStack.UpdateLayout();
            ApplyMarqueeSizing();
        }

        private void ApplyMarqueeSizing()
        {
            if (NowPlayingScroller == null || NowPlayingText1 == null || NowPlayingText2 == null) return;

            // Ensure both texts are visible only when needed
            var viewWidth = NowPlayingScroller.ActualWidth;
            var textWidth = NowPlayingText1.ActualWidth;

            if (viewWidth <= 0 || textWidth <= 0)
            {
                _marqueeTimer.Stop();
                NowPlayingScroller.ScrollToHorizontalOffset(0);
                return;
            }

            // If the text fits, stop marquee and keep single copy
            if (textWidth <= viewWidth)
            {
                _marqueeTimer.Stop();
                NowPlayingText2.Visibility = Visibility.Collapsed;
                NowPlayingScroller.ScrollToHorizontalOffset(0);
                return;
            }

            // Needs marquee: show the second copy and compute loop length
            NowPlayingText2.Visibility = Visibility.Visible;

            // Loop length = textWidth + spacing between copies
            double gap = 24; // matches StackPanel Spacing
            _marqueeResetAfter = textWidth + gap;

            // Restart from left edge for a clean loop
            _marqueeOffset = 0;
            NowPlayingScroller.ScrollToHorizontalOffset(0);
            _marqueeTimer.Start();
        }


    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private bool _isPlaying;
        private bool _isFullscreen;
        private bool _userIsInteracting;

        private readonly DispatcherTimer _hideControlsTimer = new DispatcherTimer();
        private readonly DispatcherTimer _fullscreenHintTimer = new DispatcherTimer();
        private readonly DispatcherTimer _progressTimer = new DispatcherTimer();

        private Point _lastMousePosition;
        private readonly DispatcherTimer _mouseMoveDebounce = new DispatcherTimer();

        // Notes
        private List<VideoNote> _allNotes = new();
        private string _notesFilePath = "";
        private bool _notesVisible = false;

        // Settings persistence
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SecureVideoPlayer");
        private static readonly string SettingsFilePath = Path.Combine(SettingsFolder, "settings.json");

        // Volume memory for mute toggle
        private double _previousVolume = 50;

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        private bool HasVideo => VideoPlayer.Source != null;

        public MainWindow()
        {
            InitializeComponent();

            _hideControlsTimer.Interval = TimeSpan.FromSeconds(4);
            _hideControlsTimer.Tick += HideControlsTimer_Tick;

            _fullscreenHintTimer.Interval = TimeSpan.FromSeconds(3);
            _fullscreenHintTimer.Tick += (_, _) =>
            {
                FullscreenHint.Visibility = Visibility.Collapsed;
                _fullscreenHintTimer.Stop();
            };

            _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
            _progressTimer.Tick += ProgressTimer_Tick;

            _mouseMoveDebounce.Interval = TimeSpan.FromMilliseconds(100);
            _mouseMoveDebounce.Tick += (_, _) => _mouseMoveDebounce.Stop();

            // Load persisted settings
            LoadSettings();

            ControlBar.MouseEnter += (_, _) => _userIsInteracting = true;
            ControlBar.MouseLeave += (_, _) => _userIsInteracting = false;

            Loaded += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                ShowControls();
                Focus();
            };
        }

        // ================= SETTINGS PERSISTENCE =================
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<PlayerSettings>(json);
                    if (settings != null)
                    {
                        VolumeSlider.Value = settings.Volume;
                        VolumePercentText.Text = $"{(int)settings.Volume}%";
                        _previousVolume = settings.Volume > 0 ? settings.Volume : 50;

                        // Set speed combo
                        for (int i = 0; i < SpeedComboBox.Items.Count; i++)
                        {
                            if (SpeedComboBox.Items[i] is ComboBoxItem item &&
                                item.Content is string text && text == settings.Speed)
                            {
                                SpeedComboBox.SelectedIndex = i;
                                break;
                            }
                        }
                        return;
                    }
                }
            }
            catch { }

            // Defaults
            VolumeSlider.Value = 50;
            VolumePercentText.Text = "50%";
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var speed = "1x";
                if (SpeedComboBox?.SelectedItem is ComboBoxItem item &&
                    item.Content is string text)
                    speed = text;

                var settings = new PlayerSettings
                {
                    Volume = VolumeSlider.Value,
                    Speed = speed
                };
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch { }
        }

        // ================= LOADING OVERLAY =================
        public void ShowLoading(string message = "Connecting...")
        {
            LoadingText.Text = message;
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        public void HideLoading()
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        // ================= SHOW / HIDE CONTROLS (with fade) =================
        private void ShowControls()
        {
            if (ControlBar.Visibility != Visibility.Visible)
            {
                ControlBar.Visibility = Visibility.Visible;
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                ControlBar.BeginAnimation(OpacityProperty, fadeIn);
            }

            if (_isFullscreen)
                TitleRow.Height = new GridLength(32);

            // Show cursor
            ContentGrid.Cursor = Cursors.Arrow;

            _hideControlsTimer.Stop();
            if (HasVideo && _isPlaying && !_userIsInteracting)
                _hideControlsTimer.Start();
        }

        private void HideControlsTimer_Tick(object? sender, EventArgs e)
        {
            if (_userIsInteracting)
            {
                _hideControlsTimer.Start();
                return;
            }
            if (HasVideo && _isPlaying)
            {
                // Fade out
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
                fadeOut.Completed += (_, _) =>
                {
                    ControlBar.Visibility = Visibility.Collapsed;
                    ControlBar.Opacity = 1; // Reset for next show
                };
                ControlBar.BeginAnimation(OpacityProperty, fadeOut);

                if (_isFullscreen)
                    TitleRow.Height = new GridLength(0);

                // Hide cursor
                ContentGrid.Cursor = Cursors.None;
            }
            _hideControlsTimer.Stop();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            Close();
        }

        // ================= VIDEO OPEN / PLAYBACK =================
        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv|All files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                VideoPlayer.Source = new Uri(dialog.FileName);
                VideoPlayer.Position = TimeSpan.Zero;
                string videoTitle = Path.GetFileNameWithoutExtension(dialog.FileName);
                this.Title = $"Protected Video Player – {videoTitle}";
                TitleText.Text = videoTitle;
                Play();
                ShowControls();
                LoadNotes(dialog.FileName);
            }
        }

        public void OpenRemoteVideo(string url, string title)
        {
            HideLoading();
            VideoPlayer.Source = new Uri(url);
            VideoPlayer.Position = TimeSpan.Zero;
            this.Title = $"Protected Video Player – {title}";
            TitleText.Text = title;
            Play();
            ShowControls();
        }

        private void Play()
        {
            if (!HasVideo) return;
            VideoPlayer.Play();
            _isPlaying = true;
            PlayPauseButton.Content = "⏸";
            _progressTimer.Start();
            if (!_userIsInteracting)
                _hideControlsTimer.Start();
        }

        private void Pause()
        {
            if (!HasVideo) return;
            VideoPlayer.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
            _progressTimer.Stop();
            _hideControlsTimer.Stop();
        }

        private void TogglePlayPause()
        {
            if (!HasVideo) return;
            if (_isPlaying) Pause();
            else Play();
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
            ShowControls();
        }

        private void SkipBySeconds(double seconds)
        {
            if (!HasVideo) return;
            var newPos = VideoPlayer.Position + TimeSpan.FromSeconds(seconds);
            if (VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                var total = VideoPlayer.NaturalDuration.TimeSpan;
                if (newPos < TimeSpan.Zero) newPos = TimeSpan.Zero;
                if (newPos > total) newPos = total;
            }
            VideoPlayer.Position = newPos;
            ProgressSlider.Value = VideoPlayer.Position.TotalSeconds;
            UpdateTimeDisplay();
            RefreshVisibleNotes();
        }

        private void SkipBackButton_Click(object sender, RoutedEventArgs e)
        {
            SkipBySeconds(-10);
            ShowControls();
        }

        private void SkipForwardButton_Click(object sender, RoutedEventArgs e)
        {
            SkipBySeconds(10);
            ShowControls();
        }

        // ================= PROGRESS =================
        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.NaturalDuration.HasTimeSpan)
                ProgressSlider.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;

            ApplyPlaybackSpeed();
            _progressTimer.Start();
            UpdateTimeDisplay();
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            _progressTimer.Stop();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
            ProgressSlider.Value = 0;
            VideoPlayer.Position = TimeSpan.Zero;
            ShowControls();
            _hideControlsTimer.Stop();
            UpdateTimeDisplay();
        }

        private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            HideLoading();
            MessageBox.Show($"Playback failed: {e.ErrorException?.Message ?? "Unknown error"}\n\nNote: If you are watching a remote stream, you might need to install HLS/HEVC codecs or we may need to integrate a third-party player library.", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (!HasVideo || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
            if (!ProgressSlider.IsMouseCaptureWithin)
                ProgressSlider.Value = VideoPlayer.Position.TotalSeconds;

            UpdateTimeDisplay();
            RefreshVisibleNotes();
        }

        private void UpdateTimeDisplay()
        {
            if (!HasVideo || !VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                TimeDisplay.Text = "0:00 / 0:00";
                return;
            }
            var current = VideoPlayer.Position;
            var total = VideoPlayer.NaturalDuration.TimeSpan;
            TimeDisplay.Text = $"{FormatTime(current)} / {FormatTime(total)}";
        }

        private static string FormatTime(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? t.ToString(@"h\:mm\:ss")
                : t.ToString(@"m\:ss");
        }

        private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Update time display during drag for instant feedback
            if (ProgressSlider.IsMouseCaptureWithin && HasVideo && VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                var current = TimeSpan.FromSeconds(ProgressSlider.Value);
                var total = VideoPlayer.NaturalDuration.TimeSpan;
                TimeDisplay.Text = $"{FormatTime(current)} / {FormatTime(total)}";
            }
        }

        private void ProgressSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!HasVideo) return;
            VideoPlayer.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
            UpdateTimeDisplay();
            ShowControls();
            RefreshVisibleNotes();
        }

        // ================= VOLUME =================
        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var volumePercent = VolumeSlider.Value;
            VolumePercentText.Text = $"{(int)volumePercent}%";
            VolumeIcon.Text = volumePercent == 0 ? "🔇" : "🔊";
            VideoPlayer.Volume = volumePercent / 100.0;
            if (volumePercent > 0) _previousVolume = volumePercent;
            ShowControls();
        }

        private void VolumeIcon_Click(object sender, MouseButtonEventArgs e)
        {
            ToggleMute();
            ShowControls();
        }

        private void ChangeVolume(double deltaPercent)
        {
            var newVal = VolumeSlider.Value + deltaPercent;
            if (newVal < 0) newVal = 0;
            if (newVal > 100) newVal = 100;
            VolumeSlider.Value = newVal;
        }

        private void ToggleMute()
        {
            if (VolumeSlider.Value == 0) VolumeSlider.Value = _previousVolume;
            else VolumeSlider.Value = 0;
        }

        // ================= SPEED =================
        private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyPlaybackSpeed();
            ShowControls();
        }

        private void ApplyPlaybackSpeed()
        {
            if (SpeedComboBox?.SelectedItem is ComboBoxItem item &&
                item.Content is string text)
            {
                if (text.EndsWith("x")) text = text[..^1];
                if (double.TryParse(text, out var speed))
                    VideoPlayer.SpeedRatio = speed;
            }
        }

        // ================= FULLSCREEN =================
        private void ToggleFullscreen()
        {
            _isFullscreen = !_isFullscreen;
            if (_isFullscreen)
            {
                WindowState = WindowState.Maximized;
                WindowStyle = WindowStyle.None;
                FullscreenHint.Visibility = Visibility.Visible;
                _fullscreenHintTimer.Start();
            }
            else
            {
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                TitleRow.Height = new GridLength(32);
                FullscreenHint.Visibility = Visibility.Collapsed;
                _fullscreenHintTimer.Stop();
            }
            ShowControls();
        }

        private void ContentGrid_MouseMove(object sender, MouseEventArgs e)
        {
            if (!HasVideo) return;
            Point currentPosition = e.GetPosition(ContentGrid);
            double distance = Math.Sqrt(
                Math.Pow(currentPosition.X - _lastMousePosition.X, 2) +
                Math.Pow(currentPosition.Y - _lastMousePosition.Y, 2));

            if (distance > 5)
            {
                _lastMousePosition = currentPosition;
                if (!_mouseMoveDebounce.IsEnabled)
                {
                    ShowControls();
                    _mouseMoveDebounce.Start();
                }
            }
        }

        private void ContentGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Don't toggle play/pause if the click originated from a control
            if (IsClickOnControlBar(e))
                return;

            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.ClickCount == 1)
            {
                TogglePlayPause();
                ShowControls();
                e.Handled = true;
            }
        }

        private bool IsClickOnControlBar(MouseButtonEventArgs e)
        {
            // Walk up the visual tree from the original source
            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                if (source == ControlBar) return true;
                source = System.Windows.Media.VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        // ================= KEYBOARD SHORTCUTS =================
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (NoteInput.IsFocused) return;
            if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
            if (e.Key == Key.Escape && _isFullscreen) { ToggleFullscreen(); e.Handled = true; return; }
            if (e.Key == Key.H)
            {
                if (ControlBar.Visibility == Visibility.Visible)
                {
                    ControlBar.Visibility = Visibility.Collapsed;
                    if (_isFullscreen) TitleRow.Height = new GridLength(0);
                    _hideControlsTimer.Stop();
                }
                else ShowControls();
                e.Handled = true; return;
            }
            if (e.Key == Key.M) { ToggleMute(); ShowControls(); e.Handled = true; return; }
            if (e.Key == Key.F) { ToggleFullscreen(); e.Handled = true; return; }
            if (!HasVideo) return;

            ShowControls();
            switch (e.Key)
            {
                case Key.Space: case Key.K: TogglePlayPause(); e.Handled = true; break;
                case Key.J: SkipBySeconds(-10); e.Handled = true; break;
                case Key.L: SkipBySeconds(10); e.Handled = true; break;
                case Key.Left: SkipBySeconds(-5); e.Handled = true; break;
                case Key.Right: SkipBySeconds(5); e.Handled = true; break;
                case Key.Up: ChangeVolume(5); e.Handled = true; break;
                case Key.Down: ChangeVolume(-5); e.Handled = true; break;
                case Key.D0: case Key.NumPad0: JumpToPercent(0); e.Handled = true; break;
                case Key.D1: case Key.NumPad1: JumpToPercent(10); e.Handled = true; break;
                case Key.D2: case Key.NumPad2: JumpToPercent(20); e.Handled = true; break;
                case Key.D3: case Key.NumPad3: JumpToPercent(30); e.Handled = true; break;
                case Key.D4: case Key.NumPad4: JumpToPercent(40); e.Handled = true; break;
                case Key.D5: case Key.NumPad5: JumpToPercent(50); e.Handled = true; break;
                case Key.D6: case Key.NumPad6: JumpToPercent(60); e.Handled = true; break;
                case Key.D7: case Key.NumPad7: JumpToPercent(70); e.Handled = true; break;
                case Key.D8: case Key.NumPad8: JumpToPercent(80); e.Handled = true; break;
                case Key.D9: case Key.NumPad9: JumpToPercent(90); e.Handled = true; break;
            }
        }

        private void JumpToPercent(int percent)
        {
            if (!HasVideo || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
            var total = VideoPlayer.NaturalDuration.TimeSpan;
            var newPos = TimeSpan.FromSeconds(total.TotalSeconds * percent / 100.0);
            VideoPlayer.Position = newPos;
            ProgressSlider.Value = newPos.TotalSeconds;
            UpdateTimeDisplay();
            RefreshVisibleNotes();
        }

        // ================= NOTES PANEL =================
        private void ToggleNotes_Click(object sender, RoutedEventArgs e)
        {
            _notesVisible = !_notesVisible;
            NotesColumn.Width = _notesVisible ? new GridLength(280) : new GridLength(0);
            if (_notesVisible) RefreshVisibleNotes();
        }

        // ================= NOTES STORAGE =================
        private string GetNotesFilePath(string videoPath)
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SecureVideoPlayer", "notes");
            Directory.CreateDirectory(folder);
            var safeName = Path.GetFileNameWithoutExtension(videoPath)
                + "_" + Math.Abs(videoPath.GetHashCode()) + ".json";
            return Path.Combine(folder, safeName);
        }

        private void LoadNotes(string videoPath)
        {
            _notesFilePath = GetNotesFilePath(videoPath);
            if (File.Exists(_notesFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_notesFilePath);
                    _allNotes = JsonSerializer.Deserialize<List<VideoNote>>(json) ?? new();
                }
                catch { _allNotes = new(); }
            }
            else
            {
                _allNotes = new();
            }
            RefreshVisibleNotes();
        }

        private void SaveNotes()
        {
            if (string.IsNullOrEmpty(_notesFilePath)) return;
            try
            {
                var json = JsonSerializer.Serialize(_allNotes,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_notesFilePath, json);
            }
            catch { }
        }

        // ================= NOTES DISPLAY =================
        private void RefreshVisibleNotes()
        {
            if (!_notesVisible) return;
            var currentSeconds = VideoPlayer.Position.TotalSeconds;
            var visible = _allNotes
                .Where(n => Math.Abs(n.Timestamp - currentSeconds) <= 30)
                .OrderBy(n => n.Timestamp)
                .ToList();

            NotesList.ItemsSource = null;
            NotesList.ItemsSource = visible;
            NoteCountText.Text = visible.Count > 0
                ? $"{visible.Count} note{(visible.Count == 1 ? "" : "s")} ±30s"
                : "±30s";
        }

        // ================= ADD NOTE =================
        private void AddNote_Click(object sender, RoutedEventArgs e) => AddNoteFromInput();

        private void NoteInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift))
            {
                AddNoteFromInput();
                e.Handled = true;
            }
        }

        private void AddNoteFromInput()
        {
            var text = NoteInput.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            var note = new VideoNote
            {
                Timestamp = VideoPlayer.Position.TotalSeconds,
                Text = text,
                CreatedAt = DateTime.Now
            };

            _allNotes.Add(note);
            _allNotes = _allNotes.OrderBy(n => n.Timestamp).ToList();
            SaveNotes();
            NoteInput.Clear();
            RefreshVisibleNotes();
        }

        // ================= DELETE NOTE =================
        private void DeleteNote_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VideoNote note)
            {
                _allNotes.Remove(note);
                SaveNotes();
                RefreshVisibleNotes();
            }
        }

        // ================= SEEK TO NOTE =================
        private void NoteTimestamp_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is VideoNote note)
            {
                VideoPlayer.Position = TimeSpan.FromSeconds(note.Timestamp);
                ProgressSlider.Value = note.Timestamp;
            }
        }
    }
}

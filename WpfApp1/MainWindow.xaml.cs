using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private readonly HttpClient _httpClient = new HttpClient();

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        private bool HasVideo => _mediaPlayer?.Media != null;

        public MainWindow()
        {
            InitializeComponent();

            // Initialize LibVLC
            Core.Initialize();
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);
            VideoPlayer.MediaPlayer = _mediaPlayer;

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

            VolumeSlider.Value = 50;
            VolumePercentText.Text = "50%";
            _mediaPlayer.Volume = 50;

            ControlBar.MouseEnter += (_, _) => _userIsInteracting = true;
            ControlBar.MouseLeave += (_, _) => _userIsInteracting = false;

            Loaded += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                ShowControls();
                Focus();
            };

            _mediaPlayer.EndReached += (s, e) => Dispatcher.Invoke(OnMediaEnded);
        }

        private void OnMediaEnded()
        {
            _progressTimer.Stop();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
            ProgressSlider.Value = 0;
            _mediaPlayer.Stop();
            ShowControls();
            _hideControlsTimer.Stop();
        }

        private bool _isPlaying;
        private bool _isFullscreen;
        private bool _userIsInteracting;
        private string _videoFileName = "";

        private readonly DispatcherTimer _hideControlsTimer = new DispatcherTimer();
        private readonly DispatcherTimer _fullscreenHintTimer = new DispatcherTimer();
        private readonly DispatcherTimer _progressTimer = new DispatcherTimer();

        private Point _lastMousePosition;
        private readonly DispatcherTimer _mouseMoveDebounce = new DispatcherTimer();

        // Notes
        private List<VideoNote> _allNotes = new();
        private string _notesFilePath = "";
        private bool _notesVisible = false;

        // ================= SHOW / HIDE CONTROLS =================
        private void ShowControls()
        {
            // Ensure UI elements are visible
            ControlBar.Visibility = Visibility.Visible;
            
            // In fullscreen, we also restore the TitleBar when waking up
            TitleRow.Height = new GridLength(32);

            _hideControlsTimer.Stop();
            
            // Only restart the hide timer if a video is playing and user isn't hovering over controls
            if (HasVideo && _isPlaying && !_userIsInteracting)
                _hideControlsTimer.Start();
        }

        private void HideControlsTimer_Tick(object? sender, EventArgs e)
        {
            // Don't hide if user is currently interacting with the control bar
            if (_userIsInteracting)
                return;

            if (HasVideo && _isPlaying)
            {
                ControlBar.Visibility = Visibility.Collapsed;
                
                // Only hide the TitleBar in Fullscreen mode during playback
                if (_isFullscreen)
                {
                    TitleRow.Height = new GridLength(0);
                }
            }
            _hideControlsTimer.Stop();
        }

        // ================= TITLE BAR =================
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // ================= VIDEO OPEN / PLAYBACK =================
        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m3u8|All files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                var media = new Media(_libVLC, new Uri(dialog.FileName));
                _mediaPlayer.Play(media);
                
                string videoTitle = Path.GetFileNameWithoutExtension(dialog.FileName);
                this.Title = $"Protected Video Player – {videoTitle}";
                TitleText.Text = videoTitle;
                
                _isPlaying = true;
                PlayPauseButton.Content = "⏸";
                _progressTimer.Start();
                ShowControls();
                LoadNotes(dialog.FileName);
            }
        }

        public async Task StartHandshakeAndPlayAsync(string oneTimeToken)
        {
            if (string.IsNullOrEmpty(oneTimeToken)) return;

            try
            {
                // Call the NestJS validation endpoint (public, no Bearer auth needed)
                var response = await _httpClient.GetAsync(
                    $"http://localhost:3000/api/v1/bunny-stream/validate/{Uri.EscapeDataString(oneTimeToken)}");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                // The NestJS TransformInterceptor wraps responses as:
                // { "data": { "videoId": "...", "title": "...", "hlsUrl": "...", "userId": "..." }, "success": true, "timestamp": "..." }
                var data = doc.RootElement.GetProperty("data");
                string hlsUrl = data.GetProperty("hlsUrl").GetString()!;
                string title = data.GetProperty("title").GetString() ?? "Secure Stream";

                Dispatcher.Invoke(() =>
                {
                    var media = new Media(_libVLC, new Uri(hlsUrl));
                    _mediaPlayer.Play(media);
                    _isPlaying = true;
                    PlayPauseButton.Content = "⏸";
                    TitleText.Text = title;
                    this.Title = $"Protected Video Player – {title}";
                    _progressTimer.Start();
                    ShowControls();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    MessageBox.Show($"Secure handshake failed: {ex.Message}", "Playback Error",
                        MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

        private void Play()
        {
            if (!HasVideo) return;
            _mediaPlayer.Play();
            _isPlaying = true;
            PlayPauseButton.Content = "⏸";
            _progressTimer.Start();
            if (!_userIsInteracting)
                _hideControlsTimer.Start();
        }

        private void Pause()
        {
            if (!HasVideo) return;
            _mediaPlayer.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
            _progressTimer.Stop();
            _hideControlsTimer.Stop();
        }

        private void TogglePlayPause()
        {
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
            long newTime = _mediaPlayer.Time + (long)(seconds * 1000);
            if (newTime < 0) newTime = 0;
            if (newTime > _mediaPlayer.Length) newTime = _mediaPlayer.Length;
            _mediaPlayer.Time = newTime;
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
        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (!HasVideo || _mediaPlayer.Length <= 0) return;
            
            if (!ProgressSlider.IsMouseCaptureWithin)
            {
                ProgressSlider.Maximum = _mediaPlayer.Length / 1000.0;
                ProgressSlider.Value = _mediaPlayer.Time / 1000.0;
            }

            RefreshVisibleNotes();
        }

        private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
{
    // Do nothing while dragging — seeking happens on mouse up
}

private void ProgressSlider_MouseUp(object sender, MouseButtonEventArgs e)
{
    if (!HasVideo) return;
    _mediaPlayer.Time = (long)(ProgressSlider.Value * 1000);
    ShowControls();
    RefreshVisibleNotes();
}

        // ================= VOLUME =================
        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer == null) return;
            var volumePercent = VolumeSlider.Value;
            VolumePercentText.Text = $"{(int)volumePercent}%";
            VolumeIcon.Text = volumePercent == 0 ? "🔇" : "🔊";
            _mediaPlayer.Volume = (int)volumePercent;
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
            if (VolumeSlider.Value == 0) VolumeSlider.Value = 50;
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
                if (float.TryParse(text, out var speed))
                    _mediaPlayer?.SetRate(speed);
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

            // Get position relative to the main grid to calculate movement delta
            Point currentPosition = e.GetPosition(this);
            double distance = Math.Sqrt(
                Math.Pow(currentPosition.X - _lastMousePosition.X, 2) +
                Math.Pow(currentPosition.Y - _lastMousePosition.Y, 2));

            // If the mouse moved significantly or if controls are currently hidden, wake up the UI
            if (distance > 2 || ControlBar.Visibility != Visibility.Visible)
            {
                _lastMousePosition = currentPosition;
                
                // Show controls and use a small debounce to avoid flickering
                if (!_mouseMoveDebounce.IsEnabled)
                {
                    ShowControls();
                    _mouseMoveDebounce.Start();
                }
            }
        }

        private void ContentGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
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
            if (!HasVideo || _mediaPlayer.Length <= 0) return;
            long newTime = (long)(_mediaPlayer.Length * (percent / 100.0));
            _mediaPlayer.Time = newTime;
            ProgressSlider.Value = newTime / 1000.0;
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
            if (!_notesVisible || !HasVideo) return;
            var currentSeconds = _mediaPlayer.Time / 1000.0;
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
                Timestamp = _mediaPlayer.Time / 1000.0,
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
                _mediaPlayer.Time = (long)(note.Timestamp * 1000);
                ProgressSlider.Value = note.Timestamp;
            }
        }
    }
}

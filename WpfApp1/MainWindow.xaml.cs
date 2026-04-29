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
using System.Windows.Threading;
using Microsoft.Win32;
using LibVLCSharp.Shared;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mediaPlayer;

        private bool _isPlaying;
        private bool _isFullscreen;

        // Pre-fullscreen state
        private WindowState _preFullscreenState;
        private double _preFullscreenTop, _preFullscreenLeft, _preFullscreenWidth, _preFullscreenHeight;
        private Point _lastMousePos;

        private readonly DispatcherTimer _hideControlsTimer = new();
        private readonly DispatcherTimer _fullscreenHintTimer = new();
        private readonly DispatcherTimer _progressTimer = new();

        private List<VideoNote> _allNotes = new();
        private string _notesFilePath = "";
        private bool _notesVisible = false;

        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureVideoPlayer");
        private static readonly string SettingsFilePath = Path.Combine(SettingsFolder, "settings.json");
        private double _previousVolume = 50;

        private readonly string PlayIcon = "M5 3l14 9-14 9V3z";
        private readonly string PauseIcon = "M6 4h4v16H6zm8 0h4v16h-4z";
        private readonly string VolumeHighIcon = "M11 5L6 9H2v6h4l5 4V5z M15.54 8.46a5 5 0 0 1 0 7.08 M19.07 4.93a10 10 0 0 1 0 14.14";
        private readonly string VolumeMediumIcon = "M11 5L6 9H2v6h4l5 4V5z M15.54 8.46a5 5 0 0 1 0 7.08";
        private readonly string VolumeLowIcon = "M11 5L6 9H2v6h4l5 4V5z";
        private readonly string VolumeMuteIcon = "M11 5L6 9H2v6h4l5 4V5z M23 9l-6 6 M17 9l6 6";

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        // For true fullscreen
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private bool HasVideo => _mediaPlayer?.Media != null;

        public MainWindow()
        {
            InitializeComponent();

            Core.Initialize();
            // Hardware decoding + generous caching for smooth HLS
            _libVLC = new LibVLC(
                "--avcodec-hw=any",
                "--network-caching=3000",
                "--live-caching=3000",
                "--clock-jitter=0",
                "--sout-mux-caching=2000"
            );
            _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            VideoView.MediaPlayer = _mediaPlayer;

            // VLC events (dispatched to UI thread)
            _mediaPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                HideLoading();
                _isPlaying = true;
                PlayPauseButton.Content = MakeIcon(PauseIcon);
                if (_mediaPlayer.Length > 0) ProgressSlider.Maximum = _mediaPlayer.Length / 1000.0;
                ApplyPlaybackSpeed();
                _progressTimer.Start();
                UpdateTimeDisplay();
            });

            _mediaPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _progressTimer.Stop();
                _isPlaying = false;
                PlayPauseButton.Content = MakeIcon(PlayIcon);
                ProgressSlider.Value = 0;
                UpdateTimeDisplay();
            });

            _mediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                HideLoading();
                MessageBox.Show("Playback failed.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            });

            _mediaPlayer.LengthChanged += (_, e) => Dispatcher.BeginInvoke(() =>
            {
                if (e.Length > 0) ProgressSlider.Maximum = e.Length / 1000.0;
            });

            // Timers — auto-hide controls when playing (any mode)
            _hideControlsTimer.Interval = TimeSpan.FromSeconds(4);
            _hideControlsTimer.Tick += (_, _) =>
            {
                if (_isPlaying) FadeOutControls();
                _hideControlsTimer.Stop();
            };

            _fullscreenHintTimer.Interval = TimeSpan.FromSeconds(3);
            _fullscreenHintTimer.Tick += (_, _) => { FullscreenHint.Visibility = Visibility.Collapsed; _fullscreenHintTimer.Stop(); };

            _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
            _progressTimer.Tick += (_, _) =>
            {
                if (!HasVideo || _mediaPlayer.Length <= 0) return;
                if (!ProgressSlider.IsMouseCaptureWithin) ProgressSlider.Value = _mediaPlayer.Time / 1000.0;
                UpdateTimeDisplay();
                if (_notesVisible) RefreshVisibleNotes();
            };

            LoadSettings();

            Loaded += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                Focus();
            };

            Closing += (_, _) =>
            {
                SaveSettings();
                _mediaPlayer?.Stop();
                _mediaPlayer?.Dispose();
                _libVLC?.Dispose();
            };

        }

        private bool _controlsVisible = true;
        private bool _hoveringControls = false;

        private void FadeOutControls()
        {
            if (!_controlsVisible || _hoveringControls) return;
            _controlsVisible = false;

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500));
            fadeOut.Completed += (_, _) =>
            {
                if (!_controlsVisible)
                {
                    ControlBarBorder.Visibility = Visibility.Collapsed;
                    if (_isFullscreen) TitleBar.Visibility = Visibility.Collapsed;
                    Cursor = System.Windows.Input.Cursors.None;
                }
            };
            ControlBarBorder.BeginAnimation(OpacityProperty, fadeOut);
            if (_isFullscreen) TitleBar.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void FadeInControls()
        {
            _controlsVisible = true;
            Cursor = System.Windows.Input.Cursors.Arrow;
            ControlBarBorder.Visibility = Visibility.Visible;
            TitleBar.Visibility = Visibility.Visible;
            ControlBarBorder.BeginAnimation(OpacityProperty, null);
            TitleBar.BeginAnimation(OpacityProperty, null);
            ControlBarBorder.Opacity = 1;
            TitleBar.Opacity = 1;
            _hideControlsTimer.Stop();
            if (_isPlaying) _hideControlsTimer.Start();
        }

        // ═══════════════════ MOUSE (inside VideoView overlay) ═══════════════════
        private void ContentGrid_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _lastMousePos.X) > 3 || Math.Abs(pos.Y - _lastMousePos.Y) > 3)
            { _lastMousePos = pos; FadeInControls(); }
        }

        private void ContentGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null) { if (source == ControlBarBorder) return; source = System.Windows.Media.VisualTreeHelper.GetParent(source); }
            TogglePlayPause();
        }

        private void ControlBar_MouseEnter(object sender, MouseEventArgs e) { _hoveringControls = true; _hideControlsTimer.Stop(); }
        private void ControlBar_MouseLeave(object sender, MouseEventArgs e) { _hoveringControls = false; if (_isPlaying) _hideControlsTimer.Start(); }

        // ═══════════════════ ICONS ═══════════════════
        private object MakeIcon(string pathData, bool isStroke = false)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(pathData),
                Stretch = System.Windows.Media.Stretch.None
            };
            if (isStroke)
            {
                path.Stroke = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E0E0E0"));
                path.StrokeThickness = 1.8;
                path.StrokeStartLineCap = System.Windows.Media.PenLineCap.Round;
                path.StrokeEndLineCap = System.Windows.Media.PenLineCap.Round;
                path.StrokeLineJoin = System.Windows.Media.PenLineJoin.Round;
            }
            else
            {
                path.Fill = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E0E0E0"));
            }
            return new Viewbox { Width = 16, Height = 16, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
        }

        // ═══════════════════ SETTINGS ═══════════════════
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var s = JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(SettingsFilePath));
                    if (s != null)
                    {
                        VolumeSlider.Value = s.Volume;
                        VolumePercentText.Text = $"{(int)s.Volume}%";
                        _previousVolume = s.Volume > 0 ? s.Volume : 50;
                        for (int i = 0; i < SpeedComboBox.Items.Count; i++)
                            if (SpeedComboBox.Items[i] is ComboBoxItem ci && ci.Content as string == s.Speed)
                            { SpeedComboBox.SelectedIndex = i; break; }
                        return;
                    }
                }
            }
            catch { }
            VolumeSlider.Value = 50;
            VolumePercentText.Text = "50%";
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                var speed = SpeedComboBox?.SelectedItem is ComboBoxItem ci ? ci.Content as string ?? "1x" : "1x";
                File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(
                    new PlayerSettings { Volume = VolumeSlider.Value, Speed = speed },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ═══════════════════ LOADING ═══════════════════
        public void ShowLoading(string msg = "Connecting...") { LoadingText.Text = msg; LoadingOverlay.Visibility = Visibility.Visible; }
        public void HideLoading() { LoadingOverlay.Visibility = Visibility.Collapsed; }

        // ═══════════════════ WINDOW CHROME ═══════════════════
        private void MinimizeButton_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void MaximizeButton_Click(object s, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void CloseButton_Click(object s, RoutedEventArgs e) => Close();

        // ═══════════════════ OPEN / PLAY ═══════════════════
        private void OpenButton_Click(object s, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv|All|*.*" };
            if (dlg.ShowDialog() == true)
            {
                _mediaPlayer.Media = new LibVLCSharp.Shared.Media(_libVLC, dlg.FileName, FromType.FromPath);
                TitleText.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
                Play();
                LoadNotes(dlg.FileName);
            }
        }

        public void OpenRemoteVideo(string url, string title)
        {
            ShowLoading("Connecting Secure Stream...");
            _mediaPlayer.Media = new LibVLCSharp.Shared.Media(_libVLC, new Uri(url));
            TitleText.Text = title;
            this.Title = $"Protected Video Player – {title}";
            Play();
        }

        private void Play()
        {
            if (!HasVideo) return;
            _mediaPlayer.Play();
            _isPlaying = true;
            PlayPauseButton.Content = MakeIcon(PauseIcon);
            _progressTimer.Start();
        }

        private void Pause()
        {
            if (!HasVideo) return;
            _mediaPlayer.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = MakeIcon(PlayIcon);
            _progressTimer.Stop();
        }

        private void TogglePlayPause() { if (_isPlaying) Pause(); else Play(); }
        private void PlayPauseButton_Click(object s, RoutedEventArgs e) => TogglePlayPause();

        // ═══════════════════ SEEK ═══════════════════
        private void SkipBySeconds(double sec)
        {
            if (!HasVideo || _mediaPlayer.Length <= 0) return;
            _mediaPlayer.Time = Math.Clamp(_mediaPlayer.Time + (long)(sec * 1000), 0, _mediaPlayer.Length);
            ProgressSlider.Value = _mediaPlayer.Time / 1000.0;
            UpdateTimeDisplay();
        }

        private void SkipBackButton_Click(object s, RoutedEventArgs e) => SkipBySeconds(-10);
        private void SkipForwardButton_Click(object s, RoutedEventArgs e) => SkipBySeconds(10);

        private void UpdateTimeDisplay()
        {
            if (!HasVideo || _mediaPlayer.Length <= 0) { TimeDisplay.Text = "0:00 / 0:00"; return; }
            TimeDisplay.Text = $"{Fmt(TimeSpan.FromMilliseconds(_mediaPlayer.Time))} / {Fmt(TimeSpan.FromMilliseconds(_mediaPlayer.Length))}";
        }

        private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        private void ProgressSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ProgressSlider.IsMouseCaptureWithin && HasVideo && _mediaPlayer.Length > 0)
                TimeDisplay.Text = $"{Fmt(TimeSpan.FromSeconds(ProgressSlider.Value))} / {Fmt(TimeSpan.FromMilliseconds(_mediaPlayer.Length))}";
        }

        private void ProgressSlider_MouseUp(object s, MouseButtonEventArgs e)
        {
            if (!HasVideo) return;
            _mediaPlayer.Time = (long)(ProgressSlider.Value * 1000);
            UpdateTimeDisplay();
        }

        // ═══════════════════ VOLUME ═══════════════════
        private void VolumeSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            var v = VolumeSlider.Value;
            VolumePercentText.Text = $"{(int)v}%";
            if (v == 0) VolumeIcon.Content = MakeIcon(VolumeMuteIcon, true);
            else if (v < 33) VolumeIcon.Content = MakeIcon(VolumeLowIcon, true);
            else if (v < 67) VolumeIcon.Content = MakeIcon(VolumeMediumIcon, true);
            else VolumeIcon.Content = MakeIcon(VolumeHighIcon, true);
            if (_mediaPlayer != null) _mediaPlayer.Volume = (int)v;
            if (v > 0) _previousVolume = v;
        }

        private void VolumeIcon_Click(object s, MouseButtonEventArgs e) =>
            VolumeSlider.Value = VolumeSlider.Value == 0 ? _previousVolume : 0;

        // ═══════════════════ SPEED ═══════════════════
        private void SpeedComboBox_SelectionChanged(object s, SelectionChangedEventArgs e) => ApplyPlaybackSpeed();
        private void ApplyPlaybackSpeed()
        {
            if (SpeedComboBox?.SelectedItem is ComboBoxItem ci && ci.Content is string txt)
            {
                if (txt.EndsWith("x")) txt = txt[..^1];
                if (float.TryParse(txt, out var r)) _mediaPlayer?.SetRate(r);
            }
        }

        // ═══════════════════ FULLSCREEN ═══════════════════
        private void FullscreenButton_Click(object s, RoutedEventArgs e) => ToggleFullscreen();

        private void ToggleFullscreen()
        {
            _isFullscreen = !_isFullscreen;
            if (_isFullscreen)
            {
                // Save current state
                _preFullscreenState = WindowState;
                _preFullscreenTop = Top;
                _preFullscreenLeft = Left;
                _preFullscreenWidth = Width;
                _preFullscreenHeight = Height;

                // True fullscreen: cover the entire screen including taskbar
                WindowState = WindowState.Normal;
                Topmost = true;
                var hwnd = new WindowInteropHelper(this).Handle;
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref mi);
                // Convert from device pixels to WPF logical pixels
                var source = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
                Left = mi.rcMonitor.Left * dpiX;
                Top = mi.rcMonitor.Top * dpiY;
                Width = (mi.rcMonitor.Right - mi.rcMonitor.Left) * dpiX;
                Height = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) * dpiY;

                TitleBar.Visibility = Visibility.Collapsed;
                FullscreenHint.Visibility = Visibility.Visible;
                _fullscreenHintTimer.Start();
                _hideControlsTimer.Start();
            }
            else
            {
                // Restore previous state
                Topmost = false;
                WindowState = _preFullscreenState;
                if (_preFullscreenState == WindowState.Normal)
                {
                    Top = _preFullscreenTop;
                    Left = _preFullscreenLeft;
                    Width = _preFullscreenWidth;
                    Height = _preFullscreenHeight;
                }
                FadeInControls();
                FullscreenHint.Visibility = Visibility.Collapsed;
                _fullscreenHintTimer.Stop();
            }
        }

        // ═══════════════════ KEYBOARD ═══════════════════
        private void Window_PreviewKeyDown(object s, KeyEventArgs e)
        {
            if (NoteInput.IsFocused) return;
            switch (e.Key)
            {
                case Key.F11: case Key.F: ToggleFullscreen(); e.Handled = true; break;
                case Key.Escape: if (_isFullscreen) ToggleFullscreen(); e.Handled = true; break;
                case Key.Space: case Key.K: TogglePlayPause(); e.Handled = true; break;
                case Key.M: VolumeSlider.Value = VolumeSlider.Value == 0 ? _previousVolume : 0; e.Handled = true; break;
                case Key.J: case Key.Left: SkipBySeconds(e.Key == Key.J ? -10 : -5); e.Handled = true; break;
                case Key.L: case Key.Right: SkipBySeconds(e.Key == Key.L ? 10 : 5); e.Handled = true; break;
                case Key.Up: VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5); e.Handled = true; break;
                case Key.Down: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5); e.Handled = true; break;
            }
        }

        // ═══════════════════ NOTES ═══════════════════
        private void ToggleNotes_Click(object s, RoutedEventArgs e)
        {
            _notesVisible = !_notesVisible;
            NotesColumn.Width = _notesVisible ? new GridLength(280) : new GridLength(0);
            if (_notesVisible) RefreshVisibleNotes();
        }

        private void LoadNotes(string videoPath)
        {
            var folder = Path.Combine(SettingsFolder, "notes");
            Directory.CreateDirectory(folder);
            _notesFilePath = Path.Combine(folder, Path.GetFileNameWithoutExtension(videoPath) + "_" + Math.Abs(videoPath.GetHashCode()) + ".json");
            if (File.Exists(_notesFilePath))
            { try { _allNotes = JsonSerializer.Deserialize<List<VideoNote>>(File.ReadAllText(_notesFilePath)) ?? new(); } catch { _allNotes = new(); } }
            else _allNotes = new();
            RefreshVisibleNotes();
        }

        private void SaveNotes()
        {
            if (string.IsNullOrEmpty(_notesFilePath)) return;
            try { File.WriteAllText(_notesFilePath, JsonSerializer.Serialize(_allNotes, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        }

        private void RefreshVisibleNotes()
        {
            if (!_notesVisible) return;
            var sec = _mediaPlayer.Time / 1000.0;
            var vis = _allNotes.Where(n => Math.Abs(n.Timestamp - sec) <= 30).OrderBy(n => n.Timestamp).ToList();
            NotesList.ItemsSource = null;
            NotesList.ItemsSource = vis;
            NoteCountText.Text = vis.Count > 0 ? $"{vis.Count} note{(vis.Count == 1 ? "" : "s")} ±30s" : "±30s";
        }

        private void AddNote_Click(object s, RoutedEventArgs e) => AddNote();
        private void NoteInput_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { AddNote(); e.Handled = true; } }

        private void AddNote()
        {
            var txt = NoteInput.Text.Trim();
            if (string.IsNullOrEmpty(txt)) return;
            _allNotes.Add(new VideoNote { Timestamp = _mediaPlayer.Time / 1000.0, Text = txt, CreatedAt = DateTime.Now });
            _allNotes = _allNotes.OrderBy(n => n.Timestamp).ToList();
            SaveNotes(); NoteInput.Clear(); RefreshVisibleNotes();
        }

        private void DeleteNote_Click(object s, RoutedEventArgs e)
        { if (s is Button b && b.Tag is VideoNote n) { _allNotes.Remove(n); SaveNotes(); RefreshVisibleNotes(); } }

        private void NoteTimestamp_Click(object s, MouseButtonEventArgs e)
        { if (s is TextBlock t && t.DataContext is VideoNote n) { _mediaPlayer.Time = (long)(n.Timestamp * 1000); ProgressSlider.Value = n.Timestamp; } }
    }
}

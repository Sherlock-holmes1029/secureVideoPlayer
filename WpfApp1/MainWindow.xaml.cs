using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using System.IO;


namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private bool _isPlaying;
        private bool _isFullscreen;
        private bool _userIsInteracting; // NEW: Track if user is using controls
        private string _videoFileName = "";

        private readonly DispatcherTimer _hideControlsTimer = new DispatcherTimer();
        private readonly DispatcherTimer _fullscreenHintTimer = new DispatcherTimer();
        private readonly DispatcherTimer _progressTimer = new DispatcherTimer();

        private Point _lastMousePosition;
        private readonly DispatcherTimer _mouseMoveDebounce = new DispatcherTimer(); // NEW: Debounce mouse

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        private bool HasVideo => VideoPlayer.Source != null;

        public MainWindow()
        {
            InitializeComponent();

            // Hide controls timer - increased to 4 seconds
            _hideControlsTimer.Interval = TimeSpan.FromSeconds(4);
            _hideControlsTimer.Tick += HideControlsTimer_Tick;

            // Fullscreen hint timer
            _fullscreenHintTimer.Interval = TimeSpan.FromSeconds(3);
            _fullscreenHintTimer.Tick += (_, _) =>
            {
                FullscreenHint.Visibility = Visibility.Collapsed;
                _fullscreenHintTimer.Stop();
            };

            // Progress update timer
            _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
            _progressTimer.Tick += ProgressTimer_Tick;

            // NEW: Mouse move debounce - only show controls if mouse keeps moving
            _mouseMoveDebounce.Interval = TimeSpan.FromMilliseconds(100);
            _mouseMoveDebounce.Tick += (_, _) => _mouseMoveDebounce.Stop();

            // Initial volume
            VolumeSlider.Value = 50;
            VolumePercentText.Text = "50%";

            // NEW: Track when user enters/leaves control bar
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

        // ================= SHOW / HIDE CONTROLS =================
        private void ShowControls()
        {
            ControlBar.Visibility = Visibility.Visible;

            // In fullscreen, show title bar only when controls are visible
            if (_isFullscreen)
            {
                TitleRow.Height = new GridLength(32);
            }

            _hideControlsTimer.Stop();

            // Only auto-hide if video is playing and user isn't interacting
            if (HasVideo && _isPlaying && !_userIsInteracting)
            {
                _hideControlsTimer.Start();
            }
        }

        private void HideControlsTimer_Tick(object? sender, EventArgs e)
        {
            // Don't hide if user is actively using controls
            if (_userIsInteracting)
            {
                _hideControlsTimer.Start(); // Restart timer
                return;
            }

            if (HasVideo && _isPlaying)
            {
                ControlBar.Visibility = Visibility.Collapsed;

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
            }
        }

        private void Play()
        {
            if (!HasVideo) return;

            VideoPlayer.Play();
            _isPlaying = true;
            PlayPauseButton.Content = "⏸";
            _progressTimer.Start();

            // Start auto-hide timer when playing
            if (!_userIsInteracting)
            {
                _hideControlsTimer.Start();
            }
        }

        private void Pause()
        {
            if (!HasVideo) return;

            VideoPlayer.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
            _progressTimer.Stop();

            // Stop auto-hide when paused - keep controls visible
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
            {
                var total = VideoPlayer.NaturalDuration.TimeSpan;
                ProgressSlider.Maximum = total.TotalSeconds;
            }

            ApplyPlaybackSpeed();
            _progressTimer.Start();
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            _progressTimer.Stop();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
            ProgressSlider.Value = 0;
            VideoPlayer.Position = TimeSpan.Zero;

            // Show controls when video ends
            ShowControls();
            _hideControlsTimer.Stop();
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (!HasVideo || !VideoPlayer.NaturalDuration.HasTimeSpan)
                return;

            if (!ProgressSlider.IsMouseCaptureWithin)
            {
                ProgressSlider.Value = VideoPlayer.Position.TotalSeconds;
            }
        }

        private void ProgressSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!HasVideo)
                return;

            if (ProgressSlider.IsMouseCaptureWithin)
            {
                VideoPlayer.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
                ShowControls();
            }
        }

        // ================= VOLUME =================
        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var volumePercent = VolumeSlider.Value;
            VolumePercentText.Text = $"{(int)volumePercent}%";
            VolumeIcon.Text = volumePercent == 0 ? "" : "";
            VideoPlayer.Volume = volumePercent / 100.0;
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
            if (VolumeSlider.Value == 0)
                VolumeSlider.Value = 50;
            else
                VolumeSlider.Value = 0;
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
                if (text.EndsWith("x"))
                    text = text[..^1];

                if (double.TryParse(text, out var speed))
                {
                    VideoPlayer.SpeedRatio = speed;
                }
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
            if (!HasVideo)
                return;

            Point currentPosition = e.GetPosition(ContentGrid);

            // Calculate distance moved
            double distance = Math.Sqrt(
                Math.Pow(currentPosition.X - _lastMousePosition.X, 2) +
                Math.Pow(currentPosition.Y - _lastMousePosition.Y, 2)
            );

            // Only show controls if mouse moved significantly (>5 pixels)
            // This prevents tiny movements/jitter from showing controls
            if (distance > 5)
            {
                _lastMousePosition = currentPosition;

                // Use debounce to avoid showing on every tiny movement
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
                // Single click toggles play/pause
                TogglePlayPause();
                ShowControls();
                e.Handled = true;
            }
        }

        // ================= KEYBOARD SHORTCUTS =================
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // F11 / Esc always work
            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && _isFullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            // H = toggle hide/show controls manually
            if (e.Key == Key.H)
            {
                if (ControlBar.Visibility == Visibility.Visible)
                {
                    ControlBar.Visibility = Visibility.Collapsed;
                    if (_isFullscreen)
                    {
                        TitleRow.Height = new GridLength(0);
                    }
                    _hideControlsTimer.Stop();
                }
                else
                {
                    ShowControls();
                }
                e.Handled = true;
                return;
            }

            // M = mute / unmute
            if (e.Key == Key.M)
            {
                ToggleMute();
                ShowControls();
                e.Handled = true;
                return;
            }

            // F = toggle fullscreen (alternative to F11)
            if (e.Key == Key.F)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            // For media controls, need a video
            if (!HasVideo) return;

            ShowControls();

            switch (e.Key)
            {
                case Key.Space:
                case Key.K:
                    TogglePlayPause();
                    e.Handled = true;
                    break;

                case Key.J:
                    SkipBySeconds(-10);
                    e.Handled = true;
                    break;

                case Key.L:
                    SkipBySeconds(10);
                    e.Handled = true;
                    break;

                case Key.Left:
                    SkipBySeconds(-5);
                    e.Handled = true;
                    break;

                case Key.Right:
                    SkipBySeconds(5);
                    e.Handled = true;
                    break;

                case Key.Up:
                    ChangeVolume(5);
                    e.Handled = true;
                    break;

                case Key.Down:
                    ChangeVolume(-5);
                    e.Handled = true;
                    break;

                // NEW: 0-9 keys to jump to percentage
                case Key.D0:
                case Key.NumPad0:
                    JumpToPercent(0);
                    e.Handled = true;
                    break;
                case Key.D1:
                case Key.NumPad1:
                    JumpToPercent(10);
                    e.Handled = true;
                    break;
                case Key.D2:
                case Key.NumPad2:
                    JumpToPercent(20);
                    e.Handled = true;
                    break;
                case Key.D3:
                case Key.NumPad3:
                    JumpToPercent(30);
                    e.Handled = true;
                    break;
                case Key.D4:
                case Key.NumPad4:
                    JumpToPercent(40);
                    e.Handled = true;
                    break;
                case Key.D5:
                case Key.NumPad5:
                    JumpToPercent(50);
                    e.Handled = true;
                    break;
                case Key.D6:
                case Key.NumPad6:
                    JumpToPercent(60);
                    e.Handled = true;
                    break;
                case Key.D7:
                case Key.NumPad7:
                    JumpToPercent(70);
                    e.Handled = true;
                    break;
                case Key.D8:
                case Key.NumPad8:
                    JumpToPercent(80);
                    e.Handled = true;
                    break;
                case Key.D9:
                case Key.NumPad9:
                    JumpToPercent(90);
                    e.Handled = true;
                    break;
            }
        }

        private void JumpToPercent(int percent)
        {
            if (!HasVideo || !VideoPlayer.NaturalDuration.HasTimeSpan)
                return;

            var total = VideoPlayer.NaturalDuration.TimeSpan;
            var newPos = TimeSpan.FromSeconds(total.TotalSeconds * percent / 100.0);
            VideoPlayer.Position = newPos;
            ProgressSlider.Value = newPos.TotalSeconds;
        }
    }
}
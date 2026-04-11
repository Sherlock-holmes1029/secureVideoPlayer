using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WpfApp1
{
    public partial class PlayerOverlayWindow : Window
    {
        private readonly MainWindow _owner;
        private bool _isPlaying;
        private bool _userIsInteracting;
        private Point _lastMousePosition;

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        
        private readonly DispatcherTimer _hideControlsTimer = new DispatcherTimer();
        private readonly DispatcherTimer _fullscreenHintTimer = new DispatcherTimer();
        private readonly DispatcherTimer _progressTimer = new DispatcherTimer();
        private readonly DispatcherTimer _mouseMoveDebounce = new DispatcherTimer();

        private List<VideoNote> _allNotes = new();
        private string _notesFilePath = "";
        private bool _notesVisible = false;

        private MediaPlayer? MediaPlayer => _owner?.MediaPlayer;
        private bool HasVideo => MediaPlayer?.Media != null;

        public PlayerOverlayWindow(MainWindow owner)
        {
            _owner = owner;
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

            VolumeSlider.Value = 50;
            VolumePercentText.Text = "50%";

            Loaded += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

                ShowControls();
                Focus();
            };
        }

        public void SetPlayingState(bool isPlaying, string title = null)
        {
            _isPlaying = isPlaying;
            PlayPauseButton.Content = isPlaying ? "⏸" : "▶";
            if (title != null) TitleText.Text = title;
            
            if (isPlaying)
            {
                _progressTimer.Start();
                ShowControls();
            }
            else
            {
                _progressTimer.Stop();
                _hideControlsTimer.Stop();
            }
        }

        public void LoadVideoNotes(string filePath)
        {
            LoadNotes(filePath);
        }

        // ================= SHOW / HIDE CONTROLS =================
        private void ShowControls()
        {
            ControlBar.Visibility = Visibility.Visible;
            _owner.TitleRowHeight = 32;

            _hideControlsTimer.Stop();
            if (HasVideo && _isPlaying && !_userIsInteracting)
                _hideControlsTimer.Start();
        }

        private void HideControlsTimer_Tick(object? sender, EventArgs e)
        {
            if (_userIsInteracting) return;

            if (HasVideo && _isPlaying)
            {
                ControlBar.Visibility = Visibility.Collapsed;
                if (_owner.IsFullscreen)
                {
                    _owner.TitleRowHeight = 0;
                }
            }
            _hideControlsTimer.Stop();
        }

        // ================= EVENTS =================
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                _owner.DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
            _owner.WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
            _owner.WindowState = _owner.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => _owner.Close();

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m3u8|All files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                _owner.OpenMedia(dialog.FileName);
                TitleText.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
                SetPlayingState(true);
                LoadNotes(dialog.FileName);
            }
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            _owner.TogglePlayPause();
            SetPlayingState(_owner.IsPlaying);
            ShowControls();
        }

        private void SkipBackButton_Click(object sender, RoutedEventArgs e)
        {
            _owner.SkipBySeconds(-10);
            ShowControls();
        }

        private void SkipForwardButton_Click(object sender, RoutedEventArgs e)
        {
            _owner.SkipBySeconds(10);
            ShowControls();
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (!HasVideo || MediaPlayer.Length <= 0) return;
            
            if (!ProgressSlider.IsMouseCaptureWithin)
            {
                ProgressSlider.Maximum = MediaPlayer.Length / 1000.0;
                ProgressSlider.Value = MediaPlayer.Time / 1000.0;
            }
            RefreshVisibleNotes();
        }

        private void ProgressSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!HasVideo) return;
            _owner.SeekTo(ProgressSlider.Value);
            ShowControls();
            RefreshVisibleNotes();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_owner == null || MediaPlayer == null) return;
            var volumePercent = VolumeSlider.Value;
            VolumePercentText.Text = $"{(int)volumePercent}%";
            VolumeIcon.Text = volumePercent == 0 ? "🔇" : "🔊";
            MediaPlayer.Volume = (int)volumePercent;
            ShowControls();
        }

        private void VolumeIcon_Click(object sender, MouseButtonEventArgs e)
        {
            if (VolumeSlider.Value == 0) VolumeSlider.Value = 50;
            else VolumeSlider.Value = 0;
            ShowControls();
        }

        private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MediaPlayer == null) return;
            if (SpeedComboBox?.SelectedItem is ComboBoxItem item && item.Content is string text)
            {
                if (text.EndsWith("x")) text = text[..^1];
                if (float.TryParse(text, out var speed))
                    MediaPlayer?.SetRate(speed);
            }
            ShowControls();
        }

        private void ControlBar_MouseEnter(object sender, MouseEventArgs e) => _userIsInteracting = true;
        private void ControlBar_MouseLeave(object sender, MouseEventArgs e) => _userIsInteracting = false;

        private void RootGrid_MouseMove(object sender, MouseEventArgs e)
        {
            if (!HasVideo) return;

            Point currentPosition = e.GetPosition(this);
            double distance = Math.Sqrt(Math.Pow(currentPosition.X - _lastMousePosition.X, 2) + Math.Pow(currentPosition.Y - _lastMousePosition.Y, 2));

            if (distance > 2 || ControlBar.Visibility != Visibility.Visible)
            {
                _lastMousePosition = currentPosition;
                if (!_mouseMoveDebounce.IsEnabled)
                {
                    ShowControls();
                    _mouseMoveDebounce.Start();
                }
            }
        }

        private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                _owner.ToggleFullscreen();
                if (_owner.IsFullscreen)
                {
                    FullscreenHint.Visibility = Visibility.Visible;
                    _fullscreenHintTimer.Start();
                }
                e.Handled = true;
            }
            else if (e.ClickCount == 1)
            {
                _owner.TogglePlayPause();
                SetPlayingState(_owner.IsPlaying);
                ShowControls();
                e.Handled = true;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (NoteInput.IsFocused) return;
            if (e.Key == Key.F11 || e.Key == Key.F) { _owner.ToggleFullscreen(); e.Handled = true; return; }
            if (e.Key == Key.Escape && _owner.IsFullscreen) { _owner.ToggleFullscreen(); e.Handled = true; return; }
            if (e.Key == Key.H)
            {
                if (ControlBar.Visibility == Visibility.Visible)
                {
                    ControlBar.Visibility = Visibility.Collapsed;
                    if (_owner.IsFullscreen) _owner.TitleRowHeight = 0;
                    _hideControlsTimer.Stop();
                }
                else ShowControls();
                e.Handled = true; return;
            }
            if (e.Key == Key.M) { VolumeIcon_Click(null, null); e.Handled = true; return; }
            if (!HasVideo) return;

            ShowControls();
            switch (e.Key)
            {
                case Key.Space: case Key.K: _owner.TogglePlayPause(); SetPlayingState(_owner.IsPlaying); e.Handled = true; break;
                case Key.J: _owner.SkipBySeconds(-10); e.Handled = true; break;
                case Key.L: _owner.SkipBySeconds(10); e.Handled = true; break;
                case Key.Left: _owner.SkipBySeconds(-5); e.Handled = true; break;
                case Key.Right: _owner.SkipBySeconds(5); e.Handled = true; break;
                case Key.Up: ChangeVolume(5); e.Handled = true; break;
                case Key.Down: ChangeVolume(-5); e.Handled = true; break;
            }
        }

        private void ChangeVolume(double deltaPercent)
        {
            var newVal = VolumeSlider.Value + deltaPercent;
            if (newVal < 0) newVal = 0;
            if (newVal > 100) newVal = 100;
            VolumeSlider.Value = newVal;
        }

        // ================= NOTES PANEL =================
        private void ToggleNotes_Click(object sender, RoutedEventArgs e)
        {
            _notesVisible = !_notesVisible;
            NotesColumn.Width = _notesVisible ? new GridLength(280) : new GridLength(0);
            if (_notesVisible) RefreshVisibleNotes();
        }

        private string GetNotesFilePath(string videoPath)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SecureVideoPlayer", "notes");
            Directory.CreateDirectory(folder);
            var safeName = Path.GetFileNameWithoutExtension(videoPath) + "_" + Math.Abs(videoPath.GetHashCode()) + ".json";
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
            else _allNotes = new();
            RefreshVisibleNotes();
        }

        private void SaveNotes()
        {
            if (string.IsNullOrEmpty(_notesFilePath)) return;
            try
            {
                var json = JsonSerializer.Serialize(_allNotes, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_notesFilePath, json);
            }
            catch { }
        }

        private void RefreshVisibleNotes()
        {
            if (!_notesVisible || !HasVideo) return;
            var currentSeconds = MediaPlayer.Time / 1000.0;
            var visible = _allNotes.Where(n => Math.Abs(n.Timestamp - currentSeconds) <= 30).OrderBy(n => n.Timestamp).ToList();
            NotesList.ItemsSource = null;
            NotesList.ItemsSource = visible;
            NoteCountText.Text = visible.Count > 0 ? $"{visible.Count} note{(visible.Count == 1 ? "" : "s")} ±30s" : "±30s";
        }

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

            var note = new VideoNote { Timestamp = MediaPlayer.Time / 1000.0, Text = text, CreatedAt = DateTime.Now };
            _allNotes.Add(note);
            _allNotes = _allNotes.OrderBy(n => n.Timestamp).ToList();
            SaveNotes();
            NoteInput.Clear();
            RefreshVisibleNotes();
        }

        private void DeleteNote_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is VideoNote note)
            {
                _allNotes.Remove(note);
                SaveNotes();
                RefreshVisibleNotes();
            }
        }

        private void NoteTimestamp_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is VideoNote note)
            {
                _owner.SeekTo(note.Timestamp);
            }
        }
    }
}

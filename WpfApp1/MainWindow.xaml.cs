using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using LibVLCSharp.Shared;

namespace WpfApp1
{
    public partial class MainWindow : Window
    {
        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private PlayerOverlayWindow? _overlay;
        private readonly HttpClient _httpClient = new HttpClient();

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        public MediaPlayer MediaPlayer => _mediaPlayer;
        public bool IsPlaying { get; private set; }
        public bool IsFullscreen { get; private set; }
        public double TitleRowHeight { get; set; } // Kept for logic compatibility

        public MainWindow()
        {
            InitializeComponent();

            // Initialize LibVLC
            Core.Initialize();
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);
            VideoPlayer.MediaPlayer = _mediaPlayer;

            Loaded += MainWindow_Loaded;
            _mediaPlayer.EndReached += (s, e) => Dispatcher.Invoke(OnMediaEnded);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            // Create and show the overlay
            _overlay = new PlayerOverlayWindow(this);
            _overlay.Owner = this; // CRITICAL: Glue windows together in Z-order
            ThemeService.ApplyToWindow(_overlay); // Ensure overlay is themed
            _overlay.Show();
            
            SyncOverlay();
            Focus();
        }

        private void OnMediaEnded()
        {
            IsPlaying = false;
            _mediaPlayer.Stop();
            _overlay?.SetPlayingState(false);
        }

        // ================= SYNC LOGIC =================
        private void Window_LocationChanged(object sender, EventArgs e) => SyncOverlay();
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => SyncOverlay();
        
        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (_overlay == null) return;

            // The overlay strictly stays in WindowState.Normal to avoid AllowsTransparency maximize issues
            if (_overlay.WindowState != WindowState.Normal)
                _overlay.WindowState = WindowState.Normal;

            if (WindowState == WindowState.Minimized)
                _overlay.Hide();
            else
                _overlay.Show();
            
            SyncOverlay();
        }

        private void SyncOverlay()
        {
            if (_overlay == null) return;
            
            if (WindowState == WindowState.Maximized && !IsFullscreen)
            {
                // When maximized but not fullscreen, WPF windows have a 'bleed' border (usually 8px)
                // that extends outside the monitor. AllowsTransparency windows don't handle this
                // correctly if maximized, so we keep overlay at Normal state and map visible area.
                var border = SystemParameters.WindowResizeBorderThickness;
                _overlay.Left = this.Left + border.Left;
                _overlay.Top = this.Top + border.Top;
                _overlay.Width = this.ActualWidth - (border.Left + border.Right);
                _overlay.Height = this.ActualHeight - (border.Top + border.Bottom);
            }
            else
            {
                // Normal or Fullscreen (where borders are removed)
                _overlay.Left = this.Left;
                _overlay.Top = this.Top;
                _overlay.Width = this.ActualWidth;
                _overlay.Height = this.ActualHeight;
            }
        }

        // ================= PLAYBACK API =================
        public void OpenMedia(string filePath)
        {
            var media = new Media(_libVLC, new Uri(filePath));
            _mediaPlayer.Play(media);
            IsPlaying = true;
            this.Title = $"Protected Video Player – {Path.GetFileNameWithoutExtension(filePath)}";
        }

        public async Task StartHandshakeAndPlayAsync(string oneTimeToken)
        {
            if (string.IsNullOrEmpty(oneTimeToken)) return;

            try
            {
                var response = await _httpClient.GetAsync($"http://localhost:3000/api/v1/bunny-stream/validate/{Uri.EscapeDataString(oneTimeToken)}");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                string hlsUrl = data.GetProperty("hlsUrl").GetString()!;
                string title = data.GetProperty("title").GetString() ?? "Secure Stream";

                Dispatcher.Invoke(() =>
                {
                    var media = new Media(_libVLC, new Uri(hlsUrl));
                    _mediaPlayer.Play(media);
                    IsPlaying = true;
                    this.Title = $"Protected Video Player – {title}";
                    _overlay?.SetPlayingState(true, title);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => MessageBox.Show($"Secure handshake failed: {ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }

        public void TogglePlayPause()
        {
            if (_mediaPlayer.Media == null) return;
            if (IsPlaying) _mediaPlayer.Pause();
            else _mediaPlayer.Play();
            IsPlaying = !IsPlaying;
        }

        public void SkipBySeconds(double seconds)
        {
            if (_mediaPlayer.Media == null) return;
            long newTime = _mediaPlayer.Time + (long)(seconds * 1000);
            if (newTime < 0) newTime = 0;
            if (newTime > _mediaPlayer.Length) newTime = _mediaPlayer.Length;
            _mediaPlayer.Time = newTime;
        }

        public void SeekTo(double seconds)
        {
            if (_mediaPlayer.Media == null) return;
            _mediaPlayer.Time = (long)(seconds * 1000);
        }

        public void ToggleFullscreen()
        {
            IsFullscreen = !IsFullscreen;
            if (IsFullscreen)
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                if (_overlay != null)
                {
                    _overlay.WindowStyle = WindowStyle.None;
                    // Keep overlay Normal even in fullscreen to ensure perfect coverage
                    _overlay.WindowState = WindowState.Normal; 
                }
            }
            else
            {
                WindowStyle = WindowStyle.None; // Keep custom chrome
                WindowState = WindowState.Normal;
                if (_overlay != null)
                {
                    _overlay.WindowStyle = WindowStyle.None;
                    _overlay.WindowState = WindowState.Normal;
                }
            }
            SyncOverlay();
        }
    }
}

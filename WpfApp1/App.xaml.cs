using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;

namespace WpfApp1
{
    public partial class App : Application
    {
        private const string ApiBaseUrl = "http://localhost:4000/api/v1";
        private const string MutexName = "SecureVideoPlayer_SingleInstance";
        private const string PipeName = "SecureVideoPlayer_Pipe";

        private static Mutex? _mutex;
        private CancellationTokenSource? _pipeCts;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ── Single instance check ──
            _mutex = new Mutex(true, MutexName, out bool isNewInstance);

            string? initialToken = null;
            if (e.Args.Length > 0)
            {
                try
                {
                    var uri = new Uri(e.Args[0]);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    initialToken = query.Get("token");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(">>> URI Parse failed: " + ex.Message);
                }
            }

            // If another instance is already running, send the token via pipe and exit
            if (!isNewInstance)
            {
                if (!string.IsNullOrEmpty(initialToken))
                {
                    try
                    {
                        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                        client.Connect(2000);
                        using var writer = new StreamWriter(client);
                        await writer.WriteLineAsync(initialToken);
                        await writer.FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(">>> Pipe send failed: " + ex.Message);
                    }
                }
                Shutdown();
                return;
            }

            // ── Create window ──
            var window = new MainWindow();
            
            // Note: Theme will be applied after token resolution to avoid hardcoding tenant IDs
            
            // ── Token resolution (awaited properly) ──
            if (!string.IsNullOrEmpty(initialToken))
            {
                window.ShowLoading("Resolving video...");
                await ResolveAndPlayVideo(window, initialToken);
            }
            else
            {
                // If no token, we can't resolve theme easily, so use default or just show window
                window.Show();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _pipeCts?.Cancel();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }

        private void StartPipeServer(MainWindow window)
        {
            _pipeCts = new CancellationTokenSource();
            var ct = _pipeCts.Token;

            Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                        await server.WaitForConnectionAsync(ct);
                        using var reader = new StreamReader(server);
                        var token = await reader.ReadLineAsync();

                        if (!string.IsNullOrEmpty(token))
                        {
                            await window.Dispatcher.InvokeAsync(async () =>
                            {
                                // Bring window to front
                                window.WindowState = WindowState.Normal;
                                window.Activate();
                                window.Topmost = true;
                                window.Topmost = false;
                                window.Focus();

                                window.ShowLoading("Resolving video...");
                                await ResolveAndPlayVideo(window, token);
                            });
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(">>> Pipe server error: " + ex.Message);
                    }
                }
            }, ct);
        }

        private async Task ResolveAndPlayVideo(MainWindow window, string token)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var response = await http.GetAsync($"{ApiBaseUrl}/bunny-stream/validate/{token}");

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    var data = doc.RootElement.GetProperty("data");

                    var hlsUrl = data.TryGetProperty("hlsUrl", out var hls) ? hls.GetString() : null;
                    var mp4Url = data.TryGetProperty("mp4Url", out var mp4) ? mp4.GetString() : null;
                    var title = data.GetProperty("title").GetString() ?? "Secure Video";

                    // Apply theme if provided
                    if (data.TryGetProperty("theme", out var theme) && theme.ValueKind != JsonValueKind.Null)
                    {
                        try
                        {
                            var modeDefault = theme.GetProperty("modeDefault").GetString() ?? "light";
                            var varsKey = modeDefault == "dark" ? "cssVarsDark" : "cssVars";
                            var cssVars = theme.GetProperty(varsKey)
                                              .EnumerateObject()
                                              .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

                            ThemeService.Apply(cssVars, modeDefault);
                            ThemeService.ApplyToWindow(window);
                        }
                        catch { }
                    }

                    // For VLC (LibVLCSharp), HLS is natively supported and preferred for adaptive streaming.
                    var playbackUrl = !string.IsNullOrEmpty(hlsUrl) ? hlsUrl : mp4Url;

                    if (!string.IsNullOrEmpty(playbackUrl))
                    {
                        window.OpenRemoteVideo(playbackUrl, title);
                        window.Show(); // Ensure window is visible if it wasn't
                    }
                    else
                    {
                        window.HideLoading();
                        MessageBox.Show("The server returned an empty playback URL.", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    window.HideLoading();
                    MessageBox.Show("The video token has expired or is invalid. Please try launching again from your browser.", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                window.HideLoading();
                MessageBox.Show($"Failed to connect to the server: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

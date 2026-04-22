using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Diagnostics;
using System.Threading.Tasks;

namespace WpfApp1
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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

            try
            {
                using var http = new HttpClient();
                // 1. Resolve Tenant Configuration (Theme)
                var json = await http.GetStringAsync("http://localhost:4000/api/v1/tenants/resolve/18");

                var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                var modeDefault = data.GetProperty("modeDefault").GetString() ?? "light";

                var varsKey = modeDefault == "dark" ? "cssVarsDark" : "cssVars";
                var cssVars = data.GetProperty(varsKey)
                                  .EnumerateObject()
                                  .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

                ThemeService.Apply(cssVars, modeDefault);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(">>> Theme fetch failed: " + ex.Message);
            }

            var window = new MainWindow();
            ThemeService.ApplyToWindow(window);

            // 2. If we have a token, resolve the video details and start playback
            if (!string.IsNullOrEmpty(initialToken))
            {
                _ = ResolveAndPlayVideo(window, initialToken);
            }

            window.Show();
        }

        private async Task ResolveAndPlayVideo(MainWindow window, string token)
        {
            try
            {
                using var http = new HttpClient();
                // Call the validation endpoint
                var response = await http.GetAsync($"http://localhost:4000/api/v1/bunny-stream/validate/{token}");
                
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    var data = doc.RootElement.GetProperty("data");

                    var hlsUrl = data.GetProperty("hlsUrl").GetString();
                    var title = data.GetProperty("title").GetString() ?? "Secure Video";

                    if (!string.IsNullOrEmpty(hlsUrl))
                    {
                        window.OpenRemoteVideo(hlsUrl, title);
                    }
                }
                else
                {
                    MessageBox.Show("The video token has expired or is invalid. Please try launching again from your browser.", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect to the server: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

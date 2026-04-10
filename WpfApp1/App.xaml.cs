using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using Microsoft.Win32;

namespace WpfApp1
{
    public partial class App : Application
    {
        private readonly HttpClient _http = new HttpClient();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 0. Ensure the custom URI protocol is registered
            RegisterCustomProtocol();

            // 1. Show the window immediately to prevent startup freezing
            var window = new MainWindow();
            window.Show();

            // 2. Parse the URI from arguments
            //    Browsers pass the entire URI as a single argument:
            //    e.g. lmsplayer://play?token=eyJhbGciOiJIUzI1NiIsInR5cCI6...
            string? token = null;

            if (e.Args.Length > 0)
            {
                try
                {
                    var uri = new Uri(e.Args[0]);
                    var queryParams = HttpUtility.ParseQueryString(uri.Query);
                    token = queryParams["token"];
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($">>> Failed to parse launch URI: {ex.Message}");
                }
            }

            // 3. Initiate the secure handshake if we have a token
            if (!string.IsNullOrEmpty(token))
            {
                _ = window.StartHandshakeAndPlayAsync(token);
            }

            // 4. Load the theme asynchronously with caching
            _ = LoadThemeAsync(window);
        }

        /// <summary>
        /// Registers the "lmsplayer" custom URI protocol in the current user's registry
        /// so the OS knows to launch this executable when a lmsplayer:// link is opened.
        /// </summary>
        private void RegisterCustomProtocol()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName
                    ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WpfApp1.exe");

                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\lmsplayer");
                key.SetValue("", "URL:lmsplayer Protocol");
                key.SetValue("URL Protocol", "");

                using var iconKey = key.CreateSubKey(@"DefaultIcon");
                iconKey.SetValue("", $"\"{exePath}\",1");

                using var commandKey = key.CreateSubKey(@"shell\open\command");
                commandKey.SetValue("", $"\"{exePath}\" \"%1\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($">>> Protocol registration failed: {ex.Message}");
            }
        }

        private async Task LoadThemeAsync(MainWindow window)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string cacheDir = Path.Combine(appData, "SecureVideoPlayer");
            string cachePath = Path.Combine(cacheDir, "theme_cache.json");

            bool themeApplied = false;

            // Try to load from local cache first for instant styling
            if (File.Exists(cachePath))
            {
                try
                {
                    string cachedJson = await File.ReadAllTextAsync(cachePath);
                    ApplyTheme(cachedJson, window);
                    themeApplied = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(">>> Cache load failed: " + ex.Message);
                }
            }

            // Fetch the latest theme from the server
            try
            {
                // Note: Using the specific IP provided in the original code
                var json = await _http.GetStringAsync("http://100.125.206.30:4000/api/v1/tenants/resolve/18");
                
                // Update local cache
                Directory.CreateDirectory(cacheDir);
                await File.WriteAllTextAsync(cachePath, json);
                
                // Apply the fresh theme to the UI
                Dispatcher.Invoke(() => ApplyTheme(json, window));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(">>> Network theme fetch failed: " + ex.Message);
                // If we haven't applied a theme yet (cache failed), maybe use fallback logic here if needed
            }
        }

        private void ApplyTheme(string json, MainWindow window)
        {
            try
            {
                var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                var modeDefault = data.GetProperty("modeDefault").GetString() ?? "light";

                var varsKey = modeDefault == "dark" ? "cssVarsDark" : "cssVars";
                var cssVars = data.GetProperty(varsKey)
                                  .EnumerateObject()
                                  .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

                // Apply to global resources (Brushes)
                ThemeService.Apply(cssVars, modeDefault);
                
                // Apply specific layout properties (Radius, Fonts) to the active window
                ThemeService.ApplyToWindow(window);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(">>> Theme application failed: " + ex.Message);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Diagnostics;

namespace WpfApp1
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                using var http = new HttpClient();
                var json = await http.GetStringAsync("http://89.167.111.108:4000/api/v1/tenants/resolve/18");

                var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                var modeDefault = data.GetProperty("modeDefault").GetString() ?? "light";

                var varsKey = modeDefault == "dark" ? "cssVarsDark" : "cssVars";
                var cssVars = data.GetProperty(varsKey)
                                  .EnumerateObject()
                                  .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

                // Step 1: apply colors to global resources
                ThemeService.Apply(cssVars, modeDefault);

                Debug.WriteLine(">>> PlayerSurfaceBrush: " + Application.Current.Resources["PlayerSurfaceBrush"]);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(">>> Theme fetch failed: " + ex.Message);
            }

            // Step 2: create window, then apply fonts/radius after it loads
            var window = new MainWindow();
            ThemeService.ApplyToWindow(window);
            window.Show();
        }
    }
}

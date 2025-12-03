using Microsoft.UI;
using Microsoft.UI.Xaml;
using System;
using System.IO;
using Windows.Storage;
using WinRT.Interop;
using Microsoft.UI.Windowing;   // AppWindow


namespace NoteIt
{
    public partial class App : Application
    {
        public App()
        {
            // If App.xaml has a bad resource, InitializeComponent() can throw here.
            try
            {
                this.InitializeComponent();
            }
            catch (Exception ex)
            {
                LogSync("App.InitializeComponent", ex);
                throw; // keep crashing after logging
            }

            // Catch everything else early
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                // noisy, but useful during investigation
                LogSync("FirstChance", e.Exception);
            };

            this.UnhandledException += (s, e) =>
            {
                LogSync("Application.UnhandledException", e.Exception);
                e.Handled = false; // still crash so we see it in Event Viewer too
            };
        }

        private MainWindow? _window;

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();

            // Get AppWindow for this Window
            IntPtr hwnd = WindowNative.GetWindowHandle(_window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            // Sets icon for taskbar, Alt-Tab, and the system title bar (if not custom)
            

            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
            System.Diagnostics.Debug.WriteLine($"Icon exists? {System.IO.File.Exists(iconPath)}  [{iconPath}]");
            appWindow.SetIcon(iconPath);

            _window.Activate();
        }


        private static void LogSync(string where, Exception ex)
        {
            try
            {
                string path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "startup_log.txt");

                string Dump(Exception e)
                {
                    var s = $"[{DateTime.Now:O}] {where}: {e.GetType().FullName}: {e.Message}\r\n{e.StackTrace}\r\n";
                    var inner = e.InnerException;
                    while (inner != null)
                    {
                        s += $"-- Inner: {inner.GetType().FullName}: {inner.Message}\r\n{inner.StackTrace}\r\n";
                        inner = inner.InnerException;
                    }
                    s += "============================================================\r\n";
                    return s;
                }

                File.AppendAllText(path, Dump(ex));
            }
            catch { /* never let logging crash */ }
        }
    }
}

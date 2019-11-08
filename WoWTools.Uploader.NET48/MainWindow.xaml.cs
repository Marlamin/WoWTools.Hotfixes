using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;

namespace WoWTools.Uploader
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private string cacheFolder;
        private BackgroundWorker uploadWorker;

        public MainWindow()
        {
            InitializeComponent();

            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings;
            Console.WriteLine(config.Count);
            if (config["firstRun"] == null || bool.Parse(config["firstRun"].Value) == true)
            {
                LoadSettings();
            }
            else
            {
                CheckAndStart();
                TBIcon.Visibility = Visibility.Visible;
            }
        }

        private void CheckAndStart()
        {
            Width = 0;
            Height = 0;
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            ShowActivated = false;
            this.Visibility = Visibility.Hidden;

            uploadWorker = new BackgroundWorker();
            uploadWorker.DoWork += UploadWorker_DoWork;
            uploadWorker.RunWorkerCompleted += UploadWorker_RunWorkerCompleted;

            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings;
            cacheFolder = Path.Combine(config["installDir"].Value, "_retail_", "Cache", "ADB", "enUS");

            var watcher = new FileSystemWatcher();
            watcher.Renamed += Watcher_Renamed;
            watcher.Path = cacheFolder;
            watcher.Filter = "*.bin";
            watcher.EnableRaisingEvents = true;

            var ptrFolder = Path.Combine(config["installDir"].Value, "_ptr_", "Cache", "ADB", "enUS");
            if (Directory.Exists(ptrFolder))
            {
                var watcherPTR = new FileSystemWatcher();
                watcherPTR.Renamed += Watcher_Renamed;
                watcherPTR.Path = ptrFolder;
                watcherPTR.Filter = "*.bin";
                watcherPTR.EnableRaisingEvents = true;
            }
        }

        private void UploadWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            // Set icon to normal icon
            using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/images/cog.ico")).Stream;
            TBIcon.Icon = new System.Drawing.Icon(iconStream);

            var result = (HttpResponseMessage)e.Result;
            if (result.IsSuccessStatusCode)
            {
                Notify("Uploaded", "Cache succesfully uploaded!", BalloonIcon.Info);
            }
            else
            {
                Notify("Error uploading cache", "Server responded with HTTP " + result.StatusCode, BalloonIcon.Error);
            }
        }

        private void Watcher_Renamed(object sender, RenamedEventArgs e)
        {
            if (Path.GetFileName(e.FullPath) != "DBCache.bin")
            {
                return;
            }

            uploadWorker.RunWorkerAsync(e.FullPath);

            // Set icon to uploading icon
            using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/images/cog_upload.ico")).Stream;
            TBIcon.Icon = new System.Drawing.Icon(iconStream);
        }

        private void UploadWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            e.Result = UploadCache((string)e.Argument);
        }

        public HttpResponseMessage UploadCache(string path)
        {
            if (!File.Exists(path))
            {
                Application.Current.Dispatcher.Invoke(new Action(() => { Notify("Error reading cache!", "File not found", BalloonIcon.Error); }));
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
            }

            using (var webClient = new HttpClient())
            using (var cacheStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var memStream = new MemoryStream())
            using (var bin = new BinaryReader(cacheStream))
            {
                if (new string(bin.ReadChars(4)) != "XFTH")
                {
                    Application.Current.Dispatcher.Invoke(new Action(() => { Notify("Error uploading cache!", "Cache file is invalid!", BalloonIcon.Error); }));
                    return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
                }

                bin.BaseStream.Position = 0;

                cacheStream.CopyTo(memStream);

                webClient.DefaultRequestHeaders.Add("WT-BuildInfo", Convert.ToBase64String(File.ReadAllBytes(Path.Combine(ConfigurationManager.AppSettings["installDir"], ".build.info"))));
                webClient.DefaultRequestHeaders.Add("WT-UserToken", ConfigurationManager.AppSettings["APIToken"]);
                var fileBytes = memStream.ToArray();

                MultipartFormDataContent form = new MultipartFormDataContent();
                form.Add(new ByteArrayContent(fileBytes, 0, fileBytes.Length), "files", "DBCache.bin");
                var result = webClient.PostAsync("https://wow.tools/api/cache/upload", form).Result;
                Console.WriteLine("Return status: " + result.StatusCode);
                return result;
            }
        }

        private void Exit_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            TBIcon.Visibility = Visibility.Collapsed;
            Environment.Exit(0);
        }

        private void Notify(string title, string message, BalloonIcon icon)
        {
            Console.WriteLine(title + ": " + message);
            TBIcon.ShowBalloonTip(title, message, icon);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);

            if (config.AppSettings.Settings["firstRun"] == null)
            {
                config.AppSettings.Settings.Add("firstRun", "false");
            }
            else
            {
                config.AppSettings.Settings["firstRun"].Value = "false";
            }

            if (config.AppSettings.Settings["installDir"] == null)
            {
                config.AppSettings.Settings.Add("installDir", BaseDir.Text);
            }
            else
            {
                config.AppSettings.Settings["installDir"].Value = BaseDir.Text;
            }

            if (config.AppSettings.Settings["APIToken"] == null)
            {
                config.AppSettings.Settings.Add("APIToken", APIToken.Text);
            }
            else
            {
                config.AppSettings.Settings["APIToken"].Value = APIToken.Text;
            }

            config.Save(ConfigurationSaveMode.Modified);

            RegistryKey rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

            if ((bool)StartupBox.IsChecked)
            {
                rk.SetValue("WoW.tools Uploader", Application.ResourceAssembly.Location.Replace(".dll", ".exe"), RegistryValueKind.String);
            }
            else
            {
                rk.DeleteValue("WoW.tools Uploader", false);
            }

            System.Diagnostics.Process.Start(Application.ResourceAssembly.Location.Replace(".dll", ".exe"));
            Application.Current.Shutdown();
        }

        private void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            CheckWoWDir();
        }

        private void CheckWoWDir()
        {
            var givenPath = BaseDir.Text;

            var validDir = false;
            if (Directory.Exists(givenPath))
            {
                cacheFolder = Path.Combine(givenPath, "_retail_", "Cache", "ADB", "enUS");

                if (Directory.Exists(cacheFolder) && File.Exists(Path.Combine(givenPath, ".build.info")))
                {
                    validDir = true;
                }
            }

            if (validDir)
            {
                SaveButton.Content = "Save";
                SaveButton.IsEnabled = true;
            }
            else
            {
                SaveButton.Content = "Select a valid WoW directory and check it first";
                SaveButton.IsEnabled = false;
            }
        }

        private void LoadSettings()
        {
            var config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).AppSettings.Settings;

            Width = 475;
            Height = 250;
            WindowStyle = WindowStyle.ToolWindow;
            ShowInTaskbar = true;
            ShowActivated = true;
            this.Visibility = Visibility.Visible;

            SaveButton.Content = "Select a valid WoW directory and check it first";
            SaveButton.IsEnabled = false;

            if (config["installDir"] == null || string.IsNullOrWhiteSpace(config["installDir"].Value))
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node\\Blizzard Entertainment\\World of Warcraft"))
                    {
                        if (key != null)
                        {
                            var obj = key.GetValue("InstallPath");
                            if (obj != null)
                            {
                                var wowLoc = (string)obj;
                                wowLoc = wowLoc.Replace("_retail_", "").Replace("_classic_", "").Replace("_ptr_", "").Replace("_classic_beta_", "").Replace("_beta_", "").Replace("\\\\", "");
                                BaseDir.Text = wowLoc;
                                CheckWoWDir();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Unable to get WoW install path from registry. Falling back to online mode! Error: " + ex.Message);
                }
            }
            else
            {
                BaseDir.Text = config["installDir"].Value;
                CheckWoWDir();
            }

            if (config["APIToken"] != null && !string.IsNullOrWhiteSpace(config["APIToken"].Value))
            {
                APIToken.Text = config["APIToken"].Value;
            }

            RegistryKey rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

            if (rk.GetValue("WoW.tools Uploader") != null)
            {
                StartupBox.IsChecked = true;
            }
        }

        private void Settings_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            LoadSettings();
        }

        private void BaseDir_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            SaveButton.Content = "Select a valid WoW directory and check it first";
            SaveButton.IsEnabled = false;
        }
    }
}

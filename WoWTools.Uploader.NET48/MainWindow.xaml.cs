using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
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
        private bool showNotifications;
        private bool addonUploads;
        private List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private Dictionary<string, string> activeInstalls = new Dictionary<string, string>();
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
            foreach (var directory in Directory.GetDirectories(config["installDir"].Value))
            {
                if (!File.Exists(Path.Combine(directory, ".flavor.info"))) { continue; }
                var flavorLines = File.ReadAllLines(Path.Combine(directory, ".flavor.info"));
                if (flavorLines.Length != 2 || flavorLines[1].Substring(0, 3) != "wow")
                {
                    Console.WriteLine("Malformed .flavor.info");
                    continue;
                }

                var cacheFolder = Path.Combine(directory, "Cache", "ADB", "enUS");

                if (!Directory.Exists(cacheFolder)) continue;

                activeInstalls.Add(flavorLines[1], directory);

                var watcher = new FileSystemWatcher();
                watcher.Renamed += Watcher_Renamed;
                watcher.Path = cacheFolder;
                watcher.Filter = "*.bin";
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }

            showNotifications = bool.Parse(config["showNotifications"].Value);

            if (config["addonUploads"] != null)
            {
                addonUploads = bool.Parse(config["addonUploads"].Value);
            }
            else
            {
                addonUploads = false;
            }

            var menu = new ContextMenu();

            foreach (var activeInstall in activeInstalls)
            {
                var installItem = new MenuItem();
                var installName = activeInstall.Key;
                installItem.Name = installName;
                installItem.Header = "Upload " + installName;
                installItem.Click += Upload_PreviewMouseUp;
                menu.Items.Add(installItem);
            }

            var settingsItem = new MenuItem();
            settingsItem.Name = "Settings";
            settingsItem.Header = "Settings";
            settingsItem.Click += Settings_PreviewMouseUp;
            menu.Items.Add(settingsItem);

            var exitItem = new MenuItem();
            exitItem.Name = "Exit";
            exitItem.Header = "Exit";
            exitItem.Click += Exit_PreviewMouseUp;
            menu.Items.Add(exitItem);

            this.TBIcon.ContextMenu = menu;

            CheckForUpdates();
        }

        private async void CheckForUpdates()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
                    var latestVersion = await client.GetStringAsync("https://wow.tools/uploader/?versionCheck=" + currentVersion);
                    if (latestVersion.Length < 20 && latestVersion != currentVersion)
                    {
                        Notify("Update available", "An update to " + latestVersion + " is available on https://wow.tools/uploader/", BalloonIcon.Info);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("An error occurred checking for updates: " + e.Message);
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
                if (showNotifications)
                {
                    Notify("Uploaded", "Cache succesfully uploaded!", BalloonIcon.Info);
                }
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

            using var webClient = new HttpClient();
            using var memStream = new MemoryStream();
            using (var archive = new ZipArchive(memStream, ZipArchiveMode.Create))
            {
                // DBCache.bin
                try
                {
                    using (var cacheStream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var bin = new BinaryReader(cacheStream))
                    {
                        if (new string(bin.ReadChars(4)) != "XFTH")
                        {
                            Application.Current.Dispatcher.Invoke(new Action(() => { Notify("Error uploading cache!", "Cache file is invalid!", BalloonIcon.Error); }));
                            return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
                        }

                        bin.BaseStream.Position = 0;

                        var entry = archive.CreateEntry("DBCache.bin", CompressionLevel.Optimal);
                        using (var entryStream = entry.Open())
                        {
                            cacheStream.CopyTo(entryStream);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Unable to open/compress DBCache.bin: " + e.Message);
                }

                // WDB files
                try
                {
                    var wdbPath = Path.Combine(Path.GetDirectoryName(path), @"..\..\WDB\enUS");
                    if (Directory.Exists(wdbPath))
                    {
                        foreach (var wdbFile in Directory.GetFiles(wdbPath, "*.wdb"))
                        {
                            var wdbEntry = archive.CreateEntry(Path.GetFileName(wdbFile), CompressionLevel.Optimal);
                            using (var wdbEntryStream = wdbEntry.Open())
                            {
                                using (var fr = File.OpenRead(wdbFile))
                                {
                                    fr.CopyTo(wdbEntryStream);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Unable to open/compress WDB files: " + e.Message);
                }

                // .build.info
                try
                {
                    var buildInfoEntry = archive.CreateEntry(".build.info", CompressionLevel.Optimal);
                    using (var buildInfoEntryStream = buildInfoEntry.Open())
                    {
                        using (var fr = File.OpenRead(Path.Combine(ConfigurationManager.AppSettings["installDir"], ".build.info")))
                        {
                            fr.CopyTo(buildInfoEntryStream);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Unable to open/compress .build.info: " + e.Message);
                }

                // Addon data
                if (addonUploads)
                {
                    var wtfPath = Path.Combine(Path.GetDirectoryName(path), @"..\..\..\WTF");
                    if (Directory.Exists(wtfPath))
                    {
                        foreach (var wtfFile in Directory.GetFiles(wtfPath, "*.lua*", SearchOption.AllDirectories))
                        {
                            if (wtfFile.Contains("SavedVariables") &&
                                (Path.GetFileName(wtfFile) == "WoWDBProfiler.lua" || Path.GetFileName(wtfFile) == "WoWDBProfiler.lua.bak" || Path.GetFileName(wtfFile) == "+Wowhead_Looter.lua" || Path.GetFileName(wtfFile) == "+Wowhead_Looter.lua.bak"))
                            {
                                try
                                {
                                    var wtfEntry = archive.CreateEntry(Path.GetFileName(wtfFile), CompressionLevel.Optimal);
                                    using (var wtfEntryStream = wtfEntry.Open())
                                    {
                                        using (var fr = File.OpenRead(wtfFile))
                                        {
                                            fr.CopyTo(wtfEntryStream);
                                        }
                                    }
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine("Unable to open/compress " + wtfFile + ": " + e.Message);
                                }
                            }
                        }
                    }
                }
            }

            webClient.DefaultRequestHeaders.Add("WT-UserToken", ConfigurationManager.AppSettings["APIToken"]);
            webClient.DefaultRequestHeaders.Add("User-Agent", "WoW.Tools uploader");
            var fileBytes = memStream.ToArray();

            MultipartFormDataContent form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(fileBytes, 0, fileBytes.Length), "files", "Cache.zip");
            var result = webClient.PostAsync("https://wow.tools/dbc/api/cache/uploadzip", form).Result;
            Console.WriteLine("Return status: " + result.StatusCode);
            return result;
        }

        private void Exit_PreviewMouseUp(object sender, RoutedEventArgs e)
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

            /* Install Directory */
            if (config.AppSettings.Settings["installDir"] == null)
            {
                config.AppSettings.Settings.Add("installDir", BaseDir.Text);
            }
            else
            {
                config.AppSettings.Settings["installDir"].Value = BaseDir.Text;
            }

            /* API Token */
            if (config.AppSettings.Settings["APIToken"] == null)
            {
                config.AppSettings.Settings.Add("APIToken", APIToken.Text);
            }
            else
            {
                config.AppSettings.Settings["APIToken"].Value = APIToken.Text;
            }

            /* Notifications */
            if (config.AppSettings.Settings["showNotifications"] == null)
            {
                config.AppSettings.Settings.Add("showNotifications", "false");
            }

            if ((bool)NotificationBox.IsChecked)
            {
                config.AppSettings.Settings["showNotifications"].Value = "true";
            }
            else
            {
                config.AppSettings.Settings["showNotifications"].Value = "false";
            }

            /* Addon uploads */
            if (config.AppSettings.Settings["addonUploads"] == null)
            {
                config.AppSettings.Settings.Add("addonUploads", "false");
            }

            if ((bool)AddonBox.IsChecked)
            {
                config.AppSettings.Settings["addonUploads"].Value = "true";
            }
            else
            {
                config.AppSettings.Settings["addonUploads"].Value = "false";
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

                if ((Directory.Exists(cacheFolder) || Directory.Exists(cacheFolder.Replace("_retail_", "_beta_"))) && File.Exists(Path.Combine(givenPath, ".build.info")))
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
                    Console.WriteLine("Unable to get WoW install path from registry. Error: " + ex.Message);
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

            if (config["showNotifications"] == null || config["showNotifications"].Value == "true")
            {
                showNotifications = true;
            }
            else
            {
                showNotifications = false;
            }

            NotificationBox.IsChecked = showNotifications;

            if (config["addonUploads"] != null && config["addonUploads"].Value == "true")
            {
                addonUploads = true;
            }
            else
            {
                addonUploads = false;
            }

            AddonBox.IsChecked = addonUploads;

            var rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

            var rkResult = (string)rk.GetValue("WoW.tools Uploader", "Not found");
            if (rkResult == "Not found")
            {
                StartupBox.IsChecked = false;
            }
            else
            {
                StartupBox.IsChecked = true;
            }
        }

        private void Settings_PreviewMouseUp(object sender, RoutedEventArgs e)
        {
            LoadSettings();
        }

        private void Upload_PreviewMouseUp(object sender, RoutedEventArgs e)
        {
            var actualSender = (MenuItem)sender;

            var senderDir = activeInstalls[actualSender.Name];
            UploadCache(Path.Combine(senderDir, "Cache", "ADB", "enUS", "DBCache.bin"));
        }

        private void BaseDir_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            SaveButton.Content = "Select a valid WoW directory and check it first";
            SaveButton.IsEnabled = false;
        }

        private void StartupLabel_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            StartupBox.IsChecked = !StartupBox.IsChecked;
        }

        private void NotificationLabel_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            NotificationBox.IsChecked = !NotificationBox.IsChecked;
        }

        private void AddonLabel_OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            AddonBox.IsChecked = !AddonBox.IsChecked;
        }
    }
}

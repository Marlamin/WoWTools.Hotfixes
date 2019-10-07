using Hardcodet.Wpf.TaskbarNotification;
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
        private bool ready = false;
        private string cacheFolder;
        private BackgroundWorker uploadWorker;

        public MainWindow()
        {
            InitializeComponent();

            Width = 0;
            Height = 0;
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            ShowActivated = false;
            this.Visibility = Visibility.Hidden;

            ready = true;

            uploadWorker = new BackgroundWorker();
            uploadWorker.DoWork += UploadWorker_DoWork;
            uploadWorker.RunWorkerCompleted += UploadWorker_RunWorkerCompleted;

            var config = ConfigurationManager.AppSettings;
            if (string.IsNullOrWhiteSpace(config["installDir"]))
            {
                Notify("Error during startup!", "Install directory not set, exiting..", BalloonIcon.Error);
                ExitNicely();
                ready = false;
            }

            if (!File.Exists(Path.Combine(config["installDir"], ".build.info")))
            {
                Notify("Error during startup!", ".build.info not found, exiting..", BalloonIcon.Error); ;
                ExitNicely();
                ready = false;
            }

            cacheFolder = Path.Combine(config["installDir"], "_retail_", "Cache", "ADB", "enUS");

            if (!Directory.Exists(cacheFolder))
            {
                Notify("Error during startup!", "No valid locale folder found in ADB directory, I only support enUS, exiting..", BalloonIcon.Error);
                ExitNicely();
                ready = false;
            }

            if (!File.Exists(Path.Combine(cacheFolder, "DBCache.bin")))
            {
                Notify("Error during startup!", "DBCache.bin not found, exiting..", BalloonIcon.Error);
                ExitNicely();
                ready = false;
            }

            if (ready)
            {
                var watcher = new FileSystemWatcher();
                watcher.Renamed += Watcher_Renamed;
                watcher.Path = cacheFolder;
                watcher.Filter = "*.bin";
                watcher.EnableRaisingEvents = true;
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
            if (Path.GetFileNameWithoutExtension(e.FullPath) != "DBCache")
            {
                return;
            }

            uploadWorker.RunWorkerAsync();

            // Set icon to uploading icon
            using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/images/cog_upload.ico")).Stream;
            TBIcon.Icon = new System.Drawing.Icon(iconStream);
        }

        private void UploadWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            e.Result = UploadCache();
        }

        public HttpResponseMessage UploadCache()
        {
            using (var webClient = new HttpClient())
            using (var cacheStream = File.Open(Path.Combine(cacheFolder, "DBCache.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
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
                webClient.DefaultRequestHeaders.Add("WT-UserToken", ConfigurationManager.AppSettings["userToken"]);
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
            Environment.Exit(0);
        }

        private void Upload_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            // Set icon to uploading icon
            using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/images/cog_upload.ico")).Stream;
            TBIcon.Icon = new System.Drawing.Icon(iconStream);

            uploadWorker.RunWorkerAsync();
        }

        private void Notify(string title, string message, BalloonIcon icon)
        {
            Console.WriteLine(title + ": " + message);
            TBIcon.ShowBalloonTip(title, message, icon);
        }

        private void ExitNicely()
        {
            TBIcon.Visibility = Visibility.Collapsed;
            Environment.Exit(1);
        }
    }
}

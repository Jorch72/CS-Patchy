﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Common;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Threading.Tasks;
using System.Diagnostics;
using MonoTorrent.Client.Tracker;
using System.ComponentModel;
using Newtonsoft.Json;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using Timer = System.Threading.Timer;
using System.Threading;

namespace Patchy
{
    public partial class MainWindow
    {
        private ClientManager Client { get; set; }
        private Timer Timer { get; set; }
        private SettingsManager SettingsManager { get; set; }
        private List<FileSystemWatcher> AutoWatchers { get; set; }
        private DateTime LastIdleEvent { get; set; }

        private void Initialize()
        {
            AutoWatchers = new List<FileSystemWatcher>();
            SettingsManager.Initialize();
            SettingsManager = new SettingsManager();
            LoadSettings();
            Client.Initialize(SettingsManager);
            foreach (var label in SettingsManager.Labels)
                AddLabel(label);
            // Load prior session on another thread because it takes some time
            Task.Factory.StartNew(() =>
                {
                    BEncodedDictionary resume = null;
                    if (File.Exists(SettingsManager.FastResumePath))
                    {
                        resume = BEncodedValue.Decode<BEncodedDictionary>(
                            File.ReadAllBytes(SettingsManager.FastResumePath));
                        File.Delete(SettingsManager.FastResumePath);
                    }
                    var torrents = Directory.GetFiles(SettingsManager.TorrentCachePath, "*.torrent");
                    var serializer = new JsonSerializer();
                    foreach (var torrent in torrents)
                    {
                        var path = Path.Combine(SettingsManager.TorrentCachePath, Path.GetFileNameWithoutExtension(torrent)) + ".info";
                        try
                        {
                            TorrentInfo info;
                            using (var reader = new StreamReader(path))
                                info = serializer.Deserialize<TorrentInfo>(new JsonTextReader(reader));
                            var wrapper = new TorrentWrapper(Torrent.Load(torrent), info.Path, new TorrentSettings());
                            PeriodicTorrent periodicTorrent;
                            if (resume != null && resume.ContainsKey(wrapper.Torrent.InfoHash.ToHex()))
                            {
                                periodicTorrent = Client.LoadFastResume(
                                    new FastResume((BEncodedDictionary)resume[wrapper.Torrent.InfoHash.ToHex()]), wrapper);
                            }
                            else
                                periodicTorrent = Client.AddTorrent(wrapper);
                            periodicTorrent.LoadInfo(info);
                            periodicTorrent.CacheFilePath = torrent;
                        }
                        catch { }
                    }
                });
            Timer = new Timer(o => Dispatcher.Invoke(new Action(PeriodicUpdate)),
                null, 1000, 1000);
            InitializeIdleMonitor();
        }

        private HookProc KeyboardHook, MouseHook;
        private void InitializeIdleMonitor()
        {
            LastIdleEvent = DateTime.Now;
            KeyboardHook = (code, wParam, lParam) =>
                {
                    LastIdleEvent = DateTime.Now;
                    return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                };
            MouseHook = (code, wParam, lParam) =>
                {
                    LastIdleEvent = DateTime.Now;
                    return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                };
            SetWindowsHookEx(HookType.WH_KEYBOARD, KeyboardHook, IntPtr.Zero, Thread.CurrentThread.ManagedThreadId);
            SetWindowsHookEx(HookType.WH_MOUSE, MouseHook, IntPtr.Zero, Thread.CurrentThread.ManagedThreadId);
        }

        public void AddTorrent(MagnetLink link, string path, bool suppressMessages = false)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            var name = HttpUtility.HtmlDecode(HttpUtility.UrlDecode(link.Name));
            var cache = Path.Combine(
                    SettingsManager.TorrentCachePath,
                    ClientManager.CleanFileName(name) + ".torrent");
            for (int i = 0; i < link.AnnounceUrls.Count; i++)
                link.AnnounceUrls[i] = HttpUtility.UrlDecode(HttpUtility.UrlDecode(link.AnnounceUrls[i]));
            var wrapper = new TorrentWrapper(link, path, new TorrentSettings(), cache);
            if (Client.Torrents.Any(t => t.Torrent.InfoHash == wrapper.InfoHash))
            {
                if (!suppressMessages)
                    MessageBox.Show(name + " has already been added.", "Error");
                return;
            }
            var periodic = Client.AddTorrent(wrapper);
            periodic.CacheFilePath = cache;
            periodic.UpdateInfo();
            var serializer = new JsonSerializer();
            using (var writer = new StreamWriter(Path.Combine(SettingsManager.TorrentCachePath,
                Path.GetFileNameWithoutExtension(periodic.CacheFilePath) + ".info")))
                serializer.Serialize(new JsonTextWriter(writer), periodic.TorrentInfo);
        }

        public void AddTorrent(Torrent torrent, string path, bool suppressMessages = false)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            var wrapper = new TorrentWrapper(torrent, path, new TorrentSettings());
            if (Client.Torrents.Any(t => t.Torrent.InfoHash == wrapper.InfoHash))
            {
                if (!suppressMessages)
                    MessageBox.Show(torrent.Name + " has already been added.", "Error");
                return;
            }
            var periodic = Client.AddTorrent(wrapper);
            // Save torrent to cache
            var cache = Path.Combine(SettingsManager.TorrentCachePath, Path.GetFileName(torrent.TorrentPath));
            if (File.Exists(cache))
                File.Delete(cache);
            File.Copy(torrent.TorrentPath, cache);
            periodic.CacheFilePath = cache;
            periodic.UpdateInfo();
            var serializer = new JsonSerializer();
            using (var writer = new StreamWriter(Path.Combine(SettingsManager.TorrentCachePath,
                Path.GetFileNameWithoutExtension(periodic.CacheFilePath) + ".info")))
                serializer.Serialize(new JsonTextWriter(writer), periodic.TorrentInfo);

            if (SettingsManager.DeleteTorrentsAfterAdd)
                File.Delete(torrent.TorrentPath);
        }

        private void PeriodicUpdate()
        {
            CheckMagnetLinks();
            foreach (var torrent in Client.Torrents)
            {
                torrent.Update();
                if (torrent.Torrent.Complete && !torrent.CompletedOnAdd && !torrent.NotifiedComplete && torrent.State == TorrentState.Seeding)
                {
                    if (SettingsManager.ShowNotificationOnCompletion)
                    {
                        NotifyIcon.ShowBalloonTip(5000, "Download Complete",
                            torrent.Name, System.Windows.Forms.ToolTipIcon.Info);
                        BalloonTorrent = torrent;
                        FlashWindow(new WindowInteropHelper(this).Handle, true);
                    }
                    torrent.NotifiedComplete = true;
                    if (!string.IsNullOrEmpty(SettingsManager.PostCompletionDestination))
                    {
                        Task.Factory.StartNew(() =>
                            {
                                Client.MoveTorrent(torrent.Torrent, SettingsManager.PostCompletionDestination);
                                if (!string.IsNullOrEmpty(SettingsManager.TorrentCompletionCommand))
                                {
                                    var command = SettingsManager.TorrentCompletionCommand;
                                    // Do torrent-specific replacements
                                    if (torrent.Files.Length == 1)
                                        command = command.Replace("%F", torrent.Files[0].File.FullPath);
                                    command = command.Replace("%D", torrent.Torrent.SavePath)
                                        .Replace("%N", torrent.Torrent.Name)
                                        .Replace("%I", torrent.Torrent.InfoHash.ToHex());
                                    if (torrent.Torrent.TrackerManager.CurrentTracker != null)
                                        command = command.Replace("%T", torrent.Torrent.TrackerManager.CurrentTracker.Uri.ToString());
                                    ExecuteCommand(command);
                                }
                            });
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(SettingsManager.TorrentCompletionCommand))
                        {
                            var command = SettingsManager.TorrentCompletionCommand;
                            // Do torrent-specific replacements
                            if (torrent.Files.Length == 1)
                                command = command.Replace("%F", torrent.Files[0].File.FullPath);
                            command = command.Replace("%D", torrent.Torrent.SavePath)
                                .Replace("%N", torrent.Torrent.Name)
                                .Replace("%I", torrent.Torrent.InfoHash.ToHex());
                            if (torrent.Torrent.TrackerManager.CurrentTracker != null)
                                command = command.Replace("%T", torrent.Torrent.TrackerManager.CurrentTracker.Uri.ToString());
                            ExecuteCommand(command);
                        }
                    }
                }
            }
            UpdateNotifyIcon();
        }

        private void ExecuteCommand(string command)
        {
            try
            {
                string path = command;
                string args = null;
                if (command.StartsWith("\""))
                {
                    path = command.Remove(path.IndexOf('\"', 1)).Substring(1);
                    args = command.Substring(path.IndexOf('\"', 1) + 1);
                }
                else
                {
                    if (command.Contains(' '))
                    {
                        path = command.Remove(path.IndexOf(' '));
                        args = command.Substring(path.IndexOf(' ') + 1);
                    }
                }
                var info = new ProcessStartInfo(path);
                if (args != null)
                    info.Arguments = args;
                Process.Start(info);
            }
            catch
            {
                // TODO: Notify users?
            }
        }

        private void CheckMagnetLinks()
        {
            var visibility = Visibility.Collapsed;
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (IgnoredClipboardValue != text)
                {
                    if (Uri.IsWellFormedUriString(text, UriKind.Absolute))
                    {
                        var uri = new Uri(text);
                        if (uri.Scheme == "magnet")
                        {
                            try
                            {
                                var link = new MagnetLink(text);
                                if (!Client.Torrents.Any(t => t.Torrent.InfoHash == link.InfoHash))
                                {
                                    quickAddName.Text = HttpUtility.HtmlDecode(HttpUtility.UrlDecode(link.Name));
                                    visibility = Visibility.Visible;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            quickAddGrid.Visibility = visibility;
        }

        public void HandleArguments(string[] args)
        {
            if (args.Length == 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Visibility = Visibility.Visible;
                        Activate();
                    }));
                return;
            }
            if (args[0] == "--minimized")
            {
                Visibility = Visibility.Hidden;
                ShowInTaskbar = false;
                ShowActivated = false;
                WindowStyle = WindowStyle.None;
                Width = Height = 0;
                return;
            }
            Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var magnetLink = new MagnetLink(args[0]);
                        if (SettingsManager.PromptForSaveOnShellLinks)
                        {
                            var window = new AddTorrentWindow(SettingsManager);
                            window.MagnetLink = magnetLink;
                            if (window.ShowDialog().Value)
                            {
                                if (window.IsMagnet)
                                    AddTorrent(window.MagnetLink, window.DestinationPath);
                                else
                                    AddTorrent(window.Torrent, window.DestinationPath);

                                SaveSettings();

                                Visibility = Visibility.Visible;
                                Activate();
                                FlashWindow(new WindowInteropHelper(this).Handle, true);
                            }
                        }
                        else
                        {
                            var path = Path.Combine(SettingsManager.DefaultDownloadLocation, ClientManager.CleanFileName(magnetLink.Name));
                            if (!Directory.Exists(path))
                                Directory.CreateDirectory(path);
                            AddTorrent(magnetLink, path, true);
                        }
                    }
                    catch
                    {
                        try
                        {
                            var torrent = Torrent.Load(args[0]);
                            if (SettingsManager.PromptForSaveOnShellLinks)
                            {
                                var window = new AddTorrentWindow(SettingsManager, args[0]);
                                if (window.ShowDialog().Value)
                                {
                                    if (window.IsMagnet)
                                        AddTorrent(window.MagnetLink, window.DestinationPath);
                                    else
                                        AddTorrent(window.Torrent, window.DestinationPath);

                                    SaveSettings();

                                    Visibility = Visibility.Visible;
                                    Activate();
                                    FlashWindow(new WindowInteropHelper(this).Handle, true);
                                }
                            }
                            else
                            {
                                var path = Path.Combine(SettingsManager.DefaultDownloadLocation, ClientManager.CleanFileName(torrent.Name));
                                if (!Directory.Exists(path))
                                    Directory.CreateDirectory(path);
                                AddTorrent(torrent, path, true);

                                Visibility = Visibility.Visible;
                                Activate();
                                FlashWindow(new WindowInteropHelper(this).Handle, true);
                            }
                        }
                        catch { }
                    }
                }));
        }

        private void LoadSettings()
        {
            SettingsManager.PropertyChanged += SettingsManager_PropertyChanged;
            if (!File.Exists(SettingsManager.SettingsFile))
            {
                SettingsManager.SetToDefaults();
                SaveSettings();
            }
            else
            {
                var serializer = new JsonSerializer();
                serializer.MissingMemberHandling = MissingMemberHandling.Ignore;
                try
                {
                    using (var reader = new StreamReader(SettingsManager.SettingsFile))
                        serializer.Populate(reader, SettingsManager);
                }
                catch
                {
                    MessageBox.Show("Your settings are corrupted. They have been reset to the defaults.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    SettingsManager.SetToDefaults();
                    SaveSettings();
                }
            }
        }

        private void SaveSettings()
        {
            var serializer = new JsonSerializer();
            using (var writer = new StreamWriter(SettingsManager.SettingsFile))
                serializer.Serialize(writer, SettingsManager);
        }

        void SettingsManager_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "SaveSession":
                    App.ClearCacheOnExit = !SettingsManager.SaveSession;
                    break;
                case "ShowTrayIcon":
                    NotifyIcon.Visible = SettingsManager.ShowTrayIcon;
                    break;
                case "MinutesBetweenRssUpdates":
                    ReloadRssTimer();
                    break;
                case "AutomaticAddDirectories":
                    foreach (var watcher in AutoWatchers)
                        watcher.Dispose();
                    AutoWatchers.Clear();
                    foreach (var item in SettingsManager.AutomaticAddDirectories)
                    {
                        var watcher = new FileSystemWatcher(item, "*.torrent");
                        watcher.EnableRaisingEvents = true;
                        watcher.Created += WatcherOnCreated;
                        AutoWatchers.Add(watcher);
                    }
                    break;
            }
        }

        private void WatcherOnCreated(object sender, FileSystemEventArgs fileSystemEventArgs)
        {
            Dispatcher.Invoke(new Action(() =>
                {
                    try
                    {
                        var torrent = Torrent.Load(fileSystemEventArgs.FullPath);
                        AddTorrent(torrent, SettingsManager.DefaultDownloadLocation, true);
                        BalloonTorrent = null;
                        NotifyIcon.ShowBalloonTip(5000, "Automatically added torrent",
                            "Automatically added " + torrent.Name, ToolTipIcon.Info);
                    }
                    catch { }
                }));
        }
    }
}

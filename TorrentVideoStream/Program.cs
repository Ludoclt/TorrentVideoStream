using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using MonoTorrent;
using MonoTorrent.Client;

namespace TorrentVideoStream
{
    internal class Program
    {
        static void Main(string[] args)
        {
            CancellationTokenSource cancellation = new CancellationTokenSource();

            var mainTask = MainAsync(args, cancellation.Token);

            Console.CancelKeyPress += delegate { cancellation.Cancel(); mainTask.Wait(); };
            AppDomain.CurrentDomain.ProcessExit += delegate { cancellation.Cancel(); mainTask.Wait(); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e) { Console.WriteLine(e.ExceptionObject); cancellation.Cancel(); mainTask.Wait(); };
            Thread.GetDomain().UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e) { Console.WriteLine(e.ExceptionObject); cancellation.Cancel(); mainTask.Wait(); };

            mainTask.Wait();
        }

        static async Task MainAsync(string[] args, CancellationToken token)
        {
            EngineSettingsBuilder settingBuilder = new EngineSettingsBuilder
            {
                AllowPortForwarding = true,

                AutoSaveLoadDhtCache = true,

                AutoSaveLoadFastResume = true,

                AutoSaveLoadMagnetLinkMetadata = true,

                ListenPort = 55123,

                DhtPort = 55123,
            };

            ClientEngine engine = new ClientEngine(settingBuilder.ToSettings());
            TorrentStreamEngine torrentStream = new TorrentStreamEngine(engine);

            var stream = await torrentStream.StreamAsync(MagnetLink.Parse("magnet:?xt=urn:btih:EA2976C389CD5DF50252EAAEC0098CB65CC792BA&dn=The%20Book%20of%20Boba%20Fett%20S01E01%201080p%20HEVC%20x265-MeGusta&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969%2Fannounce&tr=udp%3A%2F%2Ftracker.openbittorrent.com%3A6969%2Fannounce&tr=udp%3A%2F%2F9.rarbg.to%3A2710%2Fannounce&tr=udp%3A%2F%2F9.rarbg.me%3A2780%2Fannounce&tr=udp%3A%2F%2F9.rarbg.to%3A2730%2Fannounce&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=http%3A%2F%2Fp4p.arenabg.com%3A1337%2Fannounce&tr=udp%3A%2F%2Ftracker.torrent.eu.org%3A451%2Fannounce&tr=udp%3A%2F%2Ftracker.tiny-vps.com%3A6969%2Fannounce&tr=udp%3A%2F%2Fopen.stealth.si%3A80%2Fannounce"), token);

            await torrentStream.StartPlayback(stream);

            Console.ReadKey();

            foreach (var manager in engine.Torrents)
            {
                var stoppingTask = manager.StopAsync();
                while (manager.State != TorrentState.Stopped)
                {
                    Console.WriteLine("{0} is {1}", manager.Torrent.Name, manager.State);
                    await Task.WhenAll(stoppingTask, Task.Delay(250));
                }
                await stoppingTask;
                if (engine.Settings.AutoSaveLoadFastResume)
                    Console.WriteLine($"FastResume data for {manager.Torrent?.Name ?? manager.InfoHash.ToHex()} has been written to disk.");
            }

            if (engine.Settings.AutoSaveLoadDhtCache)
                Console.WriteLine($"DHT cache has been written to disk.");

            if (engine.Settings.AllowPortForwarding)
                Console.WriteLine("uPnP and NAT-PMP port mappings have been removed");
        }
    }

    class TorrentStreamEngine
    {
        ClientEngine Engine { get; }
        string DownloadDirectory { get; }
        Listener Listener { get; }

        static LibVLC libVLC;
        static MediaPlayer mediaPlayer;
        static readonly List<RendererItem> renderers = new List<RendererItem>();

        public TorrentStreamEngine(ClientEngine engine)
        {
            DownloadDirectory = "StreamingCache";
            Listener = new Listener(10);
            Engine = engine;
        }

        internal async Task<Stream> StreamAsync(InfoHash infoHash, CancellationToken token)
            => await StreamAsync(await Engine.AddStreamingAsync(new MagnetLink(infoHash), DownloadDirectory), token);

        internal async Task<Stream> StreamAsync(MagnetLink magnetLink, CancellationToken token)
            => await StreamAsync(await Engine.AddStreamingAsync(magnetLink, DownloadDirectory), token);

        internal async Task<Stream> StreamAsync(Torrent torrent, CancellationToken token)
            => await StreamAsync(await Engine.AddStreamingAsync(torrent, DownloadDirectory), token);

        async Task<Stream> StreamAsync(TorrentManager manager, CancellationToken token)
        {
            manager.PeerConnected += (o, e) => {
                lock (Listener)
                    Listener.WriteLine($"Connection succeeded: {e.Peer.Uri}");
            };
            manager.ConnectionAttemptFailed += (o, e) => {
                lock (Listener)
                    Listener.WriteLine(
                        $"Connection failed: {e.Peer.ConnectionUri} - {e.Reason}");
            };
            // Every time a piece is hashed, this is fired.
            manager.PieceHashed += delegate (object o, PieceHashedEventArgs e) {
                lock (Listener)
                    Listener.WriteLine($"Piece Hashed: {e.PieceIndex} - {(e.HashPassed ? "Pass" : "Fail")}");
            };

            // Every time the state changes (Stopped -> Seeding -> Downloading -> Hashing) this is fired
            manager.TorrentStateChanged += delegate (object o, TorrentStateChangedEventArgs e) {
                lock (Listener)
                    Listener.WriteLine($"OldState: {e.OldState} NewState: {e.NewState}");
            };

            // Every time the tracker's state changes, this is fired
            manager.TrackerManager.AnnounceComplete += (sender, e) => {
                Listener.WriteLine($"{e.Successful}: {e.Tracker}");
            };

            await manager.StartAsync();
            PrintStatsAsync(token);
            await manager.WaitForMetadataAsync(token);

            ITorrentFileInfo largestFile = manager.Files.OrderBy(t => t.Length).Last();
            Stream stream = await manager.StreamProvider.CreateStreamAsync(largestFile);
            return stream;
        }

        public async Task StartPlayback(Stream stream)
        {
            Console.WriteLine("LibVLCSharp -> Loading LibVLC core library...");

            Core.Initialize();

            libVLC = new LibVLC();
            libVLC.Log += (s, e) => Console.WriteLine($"LibVLC -> {e.FormattedLog}");

            Media media = new Media(libVLC, new StreamMediaInput(stream));
            mediaPlayer = new MediaPlayer(media);

            //if (cliOptions.Chromecast)
            //{
            //    var result = await FindAndUseChromecast();
            //    if (!result)
            //        return;
            //}

            Console.WriteLine("LibVLCSharp -> Starting video streaming...");
            mediaPlayer.Play();
        }

        private static async Task<bool> FindAndUseChromecast()
        {
            using var rendererDiscoverer = new RendererDiscoverer(libVLC);
            rendererDiscoverer.ItemAdded += RendererDiscovererItemAdded;
            if (rendererDiscoverer.Start())
            {
                Console.WriteLine("LibVLCSharp -> Searching for chromecasts...");
                await Task.Delay(2000);
            }
            else
            {
                Console.WriteLine("LibVLCSharp -> Failed starting the chromecast discovery");
            }

            rendererDiscoverer.ItemAdded -= RendererDiscovererItemAdded;

            if (!renderers.Any())
            {
                Console.WriteLine("LibVLCSharp -> No chromecast found... aborting.");
                return false;
            }

            mediaPlayer.SetRenderer(renderers.First());
            return true;
        }

        private static void RendererDiscovererItemAdded(object sender, RendererDiscovererItemAddedEventArgs e)
        {
            Console.WriteLine($"LibVLCSharp -> Found a new renderer {e.RendererItem.Name} of type {e.RendererItem.Type}!");
            renderers.Add(e.RendererItem);
        }

        async void PrintStatsAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Details for all the loaded torrent managers are shown.
                    StringBuilder sb = new StringBuilder(1024);
                    while (Engine.IsRunning)
                    {
                        sb.Remove(0, sb.Length);

                        AppendFormat(sb, $"Transfer Rate:      {Engine.TotalDownloadSpeed / 1024.0:0.00}kB/sec down / {Engine.TotalUploadSpeed / 1024.0:0.00}kB/sec up");
                        AppendFormat(sb, $"Memory Cache:       {Engine.DiskManager.CacheBytesUsed / 1024.0:0.00}/{Engine.Settings.DiskCacheBytes / 1024.0:0.00} kB");
                        AppendFormat(sb, $"Disk IO Rate:       {Engine.DiskManager.ReadRate / 1024.0:0.00} kB/s read / {Engine.DiskManager.WriteRate / 1024.0:0.00} kB/s write");
                        AppendFormat(sb, $"Disk IO Total:      {Engine.DiskManager.TotalBytesRead / 1024.0:0.00} kB read / {Engine.DiskManager.TotalBytesWritten / 1024.0:0.00} kB written");
                        AppendFormat(sb, $"Open Connections:   {Engine.ConnectionManager.OpenConnections}");

                        // Print out the port mappings
                        foreach (var mapping in Engine.PortMappings.Created)
                            AppendFormat(sb, $"Successful Mapping    {mapping.PublicPort}:{mapping.PrivatePort} ({mapping.Protocol})");
                        foreach (var mapping in Engine.PortMappings.Failed)
                            AppendFormat(sb, $"Failed mapping:       {mapping.PublicPort}:{mapping.PrivatePort} ({mapping.Protocol})");
                        foreach (var mapping in Engine.PortMappings.Pending)
                            AppendFormat(sb, $"Pending mapping:      {mapping.PublicPort}:{mapping.PrivatePort} ({mapping.Protocol})");

                        foreach (TorrentManager manager in Engine.Torrents)
                        {
                            AppendSeparator(sb);
                            AppendFormat(sb, $"State:              {manager.State}");
                            AppendFormat(sb, $"Name:               {(manager.Torrent == null ? "MetaDataMode" : manager.Torrent.Name)}");
                            AppendFormat(sb, $"Progress:           {manager.Progress:0.00}");
                            AppendFormat(sb, $"Transfer Rate:      {manager.Monitor.DownloadSpeed / 1024.0:0.00}kB/s down / {manager.Monitor.UploadSpeed / 1024.0:0.00} kB/s up");
                            AppendFormat(sb, $"Total transferred:  {manager.Monitor.DataBytesDownloaded / (1024.0 * 1024.0):0.00} MB down / {manager.Monitor.DataBytesUploaded / (1024.0 * 1024.0):0.00} MB up");
                            AppendFormat(sb, $"Tracker Status");
                            foreach (var tier in manager.TrackerManager.Tiers)
                                AppendFormat(sb, $"\t{tier.ActiveTracker} : Announce Succeeded: {tier.LastAnnounceSucceeded}. Scrape Succeeded: {tier.LastScrapeSucceeded}.");

                            if (manager.PieceManager != null)
                                AppendFormat(sb, "Current Requests:   {0}", await manager.PieceManager.CurrentRequestCountAsync());

                            var peers = await manager.GetPeersAsync();
                            AppendFormat(sb, "Outgoing:");
                            foreach (PeerId p in peers.Where(t => t.ConnectionDirection == Direction.Outgoing))
                            {
                                AppendFormat(sb, "\t{2} - {1:0.00}/{3:0.00}kB/sec - {0} - {4} ({5})", p.Uri,
                                                                                            p.Monitor.DownloadSpeed / 1024.0,
                                                                                            p.AmRequestingPiecesCount,
                                                                                            p.Monitor.UploadSpeed / 1024.0,
                                                                                            p.EncryptionType,
                                                                                            string.Join("|", p.SupportedEncryptionTypes.Select(t => t.ToString()).ToArray()));
                            }
                            AppendFormat(sb, "");
                            AppendFormat(sb, "Incoming:");
                            foreach (PeerId p in peers.Where(t => t.ConnectionDirection == Direction.Incoming))
                            {
                                AppendFormat(sb, "\t{2} - {1:0.00}/{3:0.00}kB/sec - {0} - {4} ({5})", p.Uri,
                                                                                            p.Monitor.DownloadSpeed / 1024.0,
                                                                                            p.AmRequestingPiecesCount,
                                                                                            p.Monitor.UploadSpeed / 1024.0,
                                                                                            p.EncryptionType,
                                                                                            string.Join("|", p.SupportedEncryptionTypes.Select(t => t.ToString()).ToArray()));
                            }

                            AppendFormat(sb, "", null);
                            if (manager.Torrent != null)
                                foreach (var file in manager.Files)
                                    AppendFormat(sb, "{1:0.00}% - {0}", file.Path, file.BitField.PercentComplete);
                        }
                        Console.Clear();
                        Console.WriteLine(sb.ToString());
                        Listener.ExportTo(Console.Out);

                        await Task.Delay(5000, token);
                    }
                }
            }
            catch (OperationCanceledException)
            {

            }
        }

        void AppendSeparator(StringBuilder sb)
        {
            AppendFormat(sb, "", null);
            AppendFormat(sb, "- - - - - - - - - - - - - - - - - - - - - - - - - - - - - -", null);
            AppendFormat(sb, "", null);
        }

        void AppendFormat(StringBuilder sb, string str, params object[] formatting)
        {
            if (formatting != null)
                sb.AppendFormat(str, formatting);
            else
                sb.Append(str);
            sb.AppendLine();
        }
    }
}

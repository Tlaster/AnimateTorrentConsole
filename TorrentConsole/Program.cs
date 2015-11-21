using Ragnar;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace TorrentConsole
{
    internal class EqualityComparer<T, V> : IEqualityComparer<T>
    {
        private Func<T, V> _keySelector;

        public EqualityComparer(Func<T, V> keySelector)
        {
            _keySelector = keySelector;
        }

        public bool Equals(T x, T y) => EqualityComparer<V>.Default.Equals(_keySelector(x), _keySelector(y));

        public int GetHashCode(T obj) => EqualityComparer<V>.Default.GetHashCode(_keySelector(obj));
    }

    internal class Program
    {
        private const string ANIMATELIST_TABLE_NAME = "AnimateList";
        private const string DATABASE_NAME = "AnimateDatabase";
        private const string DEFAULT_DOWNLOAD_FOLDER = @"C:\Animate\";
        private const string REGEX_PATTERN = @"\[Leopard-Raws\](.+) - ([0-9]+) RAW";
        private static Dictionary<AddTorrentParams, TorrentHandle> _dic;
        private static string _downloadFolder = DEFAULT_DOWNLOAD_FOLDER;
        private static Session _session;
        private static Timer _timer;
        private static bool _enableSQLServer;

        private static async void _timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (_dic.Count == 0) return;
            for (int i = 0; i < _dic.Count; i++)
            {
                var item = _dic.ElementAt(i);
                using (var status = item.Value.QueryStatus())
                {
                    if (status.IsFinished || status.IsSeeding)
                    {
                        var info = new DirectoryInfo(status.SavePath).GetFiles().Where(file => file.Name == status.Name).FirstOrDefault();
                        info.MoveTo($"{info.DirectoryName}{item.Key.Name}{Path.GetExtension(info.FullName)}");
                        await ImportMedia(info.FullName, 1, 12);
                        _session.RemoveTorrent(item.Value);
                        _dic.Remove(item.Key);
                        Console.WriteLine($"{status.Name} is finished");
                    }
                    if (status.Error != "")
                    {
                        _session.RemoveTorrent(item.Value);
                        _dic[item.Key] = _session.AddTorrent(item.Key);
                        Console.WriteLine($"{status.Name} error,try again");
                    }
                }
            }
        }

        private static bool CheckDatabaseExists(string databaseName)
        {
            try
            {
                using (SqlConnection tmpConn = new SqlConnection("server=localhost;Integrated Security=yes"))
                {
                    using (SqlCommand sqlCmd = new SqlCommand($"select database_id from sys.databases where Name = '{databaseName}'", tmpConn))
                    {
                        tmpConn.Open();
                        object resultObj = sqlCmd.ExecuteScalar();
                        int databaseID = 0;
                        if (resultObj != null)
                            int.TryParse(resultObj.ToString(), out databaseID);
                        tmpConn.Close();
                        return databaseID > 0;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool CheckTableExists(string databaseName, string tableName)
        {
            try
            {
                using (SqlConnection tmpConn = new SqlConnection($"server=localhost;database={databaseName};Integrated Security=yes"))
                {
                    using (SqlCommand sqlCmd = new SqlCommand($"SELECT count(*) as IsExists FROM dbo.sysobjects where id = object_id('[dbo].[{tableName}]')", tmpConn))
                    {
                        tmpConn.Open();
                        object resultObj = sqlCmd.ExecuteScalar();
                        int databaseID = 0;
                        if (resultObj != null)
                            int.TryParse(resultObj.ToString(), out databaseID);
                        tmpConn.Close();
                        return databaseID == 1;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void CreateDatabase(string databaseName)
        {
            using (SqlConnection tmpConn = new SqlConnection("server=localhost;Integrated Security=yes"))
            {
                using (SqlCommand sqlCmd = new SqlCommand($"create database {databaseName}", tmpConn))
                {
                    tmpConn.Open();
                    sqlCmd.ExecuteScalar();
                    tmpConn.Close();
                }
            }
        }

        private static void CreateTable(string databaseName, string tableName, string tableAttribute)
        {
            using (SqlConnection tmpConn = new SqlConnection($"server=localhost;database={databaseName};Integrated Security=yes"))
            {
                using (SqlCommand sqlCmd = new SqlCommand($"create table {tableName} ( {tableAttribute} )", tmpConn))
                {
                    tmpConn.Open();
                    sqlCmd.ExecuteScalar();
                    tmpConn.Close();
                }
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            //TODO:make log
        }

        private static async Task ImportMedia(string mediaFile, int waitTime, int position, int imageWidth = 1280, int imageHeight = 720)
        {
            MediaPlayer player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
            player.Open(new Uri(mediaFile));
            player.Pause();
            player.Position = TimeSpan.FromSeconds(position);
            //We need to give MediaPlayer some time to load.
            //The efficiency of the MediaPlayer depends
            //upon the capabilities of the machine it is running on and
            //would be different from time to time
            await Task.Delay(TimeSpan.FromSeconds(waitTime));
            RenderTargetBitmap rtb = new RenderTargetBitmap(imageWidth, imageHeight, 96, 96, PixelFormats.Pbgra32);
            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
                dc.DrawVideo(player, new Rect(0, 0, imageWidth, imageHeight));
            rtb.Render(dv);
            Duration duration = player.NaturalDuration;
            int videoLength = 0;
            if (duration.HasTimeSpan)
                videoLength = (int)duration.TimeSpan.TotalSeconds;
            using (var file = File.Create($"{Path.GetDirectoryName(mediaFile)}\\{Path.GetFileNameWithoutExtension(mediaFile)}.png"))
            {
                BitmapFrame frame = BitmapFrame.Create(rtb).GetCurrentValueAsFrozen() as BitmapFrame;
                BitmapEncoder encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(frame as BitmapFrame);
                encoder.Save(file);
            }
            player.Close();
        }

        private static void Main(string[] args)
        {
            Console.WriteLine("init...");
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            _dic = new Dictionary<AddTorrentParams, TorrentHandle>(new EqualityComparer<AddTorrentParams, string>(item => item.Url));
            _session = new Session();
            _session.ListenOn(6881, 6889);
            var settings = _session.QuerySettings();
            settings.UploadRateLimit = 30 * 1024;
            settings.SeedTimeLimit = 1;
            _session.SetSettings(settings);
            _timer = new Timer(TimeSpan.FromSeconds(10d).TotalMilliseconds);
            _timer.Elapsed += _timer_Elapsed;
            Console.WriteLine("Enable SQL Server?(Y/N):");
            _enableSQLServer = Console.ReadLine() == "Y";
            if (_enableSQLServer)
            {
                if (!CheckDatabaseExists(DATABASE_NAME))
                {
                    CreateDatabase(DATABASE_NAME);
                    CreateTable(DATABASE_NAME, ANIMATELIST_TABLE_NAME, "ID int identity(1,1) primary key,Name nvarchar(100) not null,DirPath nvarchar(100) not null");
                }
                else if (!CheckTableExists(DATABASE_NAME, ANIMATELIST_TABLE_NAME))
                {
                    CreateTable(DATABASE_NAME, ANIMATELIST_TABLE_NAME, "ID int identity(1,1) primary key,Name nvarchar(100) not null,DirPath nvarchar(100) not null");
                }
            }
            Console.WriteLine($"please set the download folder(by default: {_downloadFolder} ,press enter to keep the default):");
            _downloadFolder = Console.ReadLine();
            if (string.IsNullOrEmpty(_downloadFolder) || string.IsNullOrWhiteSpace(_downloadFolder))
            {
                _downloadFolder = DEFAULT_DOWNLOAD_FOLDER;
            }
            Directory.CreateDirectory(_downloadFolder);
            Console.WriteLine("please set the delay interval(in minutes):");
            double interval = 30d;
            while (!double.TryParse(Console.ReadLine(), out interval))
            {
                Console.WriteLine("error, please try again:");
            }
            Console.WriteLine("running...");
            _timer.Start();
            Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromMinutes(interval));
                    var rssStr = "";
                    try
                    {
                        Console.WriteLine("Getting rss...");
                        using (var client = new HttpClient())
                            rssStr = await client.GetStringAsync("http://leopard-raws.org/rss.php");
                    }
                    catch (HttpRequestException) { continue; }
                    catch (WebException) { continue; }
                    if (string.IsNullOrEmpty(rssStr)) continue;
                    XDocument doc = XDocument.Parse(rssStr);
                    var list = (from item in doc.Descendants()
                                where item?.Name == "item"
                                //check the item if exist
                                && CheckExists(item)
                                //check the item if is downloading
                                && _dic.Where(download => download.Key.Url == item.Descendants().Where(node => node.Name == "link").FirstOrDefault()?.Value).Count() == 0
                                select new
                                {
                                    Title = Regex.Match(item.Descendants().Where(node => node.Name == "title").FirstOrDefault()?.Value, REGEX_PATTERN).Groups[0].Value,
                                    Link = item.Descendants().Where(node => node.Name == "link").FirstOrDefault()?.Value,
                                    Name = Regex.Match(item.Descendants().Where(node => node.Name == "title").FirstOrDefault()?.Value, REGEX_PATTERN).Groups[1].Value.Trim(),
                                    FileName = Regex.Match(item.Descendants().Where(node => node.Name == "title").FirstOrDefault()?.Value, REGEX_PATTERN).Groups[2].Value.Trim(),
                                }).ToList();
                    if (list.Count == 0) continue;
                    foreach (var item in list)
                    {
                        if (!Directory.Exists(_downloadFolder + item.Name))
                        {
                            Directory.CreateDirectory(_downloadFolder + item.Name);
                            if (_enableSQLServer)
                            {
                                using (SqlConnection connection = new SqlConnection($"server=localhost;database={DATABASE_NAME};Integrated Security=yes"))
                                using (SqlCommand sqlCmd = new SqlCommand($"insert into {ANIMATELIST_TABLE_NAME}(Name,DirPath) values ('{item.Name}','{_downloadFolder + item.Name}');", connection))
                                {
                                    connection.Open();
                                    sqlCmd.ExecuteScalar();
                                    connection.Close();
                                }
                            }
                        }
                        var torrentParams = new AddTorrentParams { SavePath = _downloadFolder + item.Name, Url = item.Link, UploadLimit = 10 * 1024, Name = item.FileName };
                        var handle = _session.AddTorrent(torrentParams);
                        _dic.Add(torrentParams, handle);
                        Console.WriteLine($"{item.Title} start downloading...");
                    }
                }
            }).Wait();
        }

        private static bool CheckExists(XElement item)
        {
            var title = Regex.Match(item.Descendants().Where(node => node.Name == "title").FirstOrDefault()?.Value, REGEX_PATTERN).Groups[1].Value.Trim();
            var fileName = Regex.Match(item.Descendants().Where(node => node.Name == "title").FirstOrDefault()?.Value, REGEX_PATTERN).Groups[2].Value;
            return Directory.Exists(_downloadFolder + title) ? Directory.GetFiles(_downloadFolder + title, fileName).Count() == 0 : true;
        }
    }
}
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
using System.Xml.Linq;

namespace TorrentConsole
{
    internal class Program
    {
        private static Timer _timer;
        private static Session _session;
        private static Dictionary<AddTorrentParams, TorrentHandle> _dic;

        private static void _timer_Elapsed(object sender, ElapsedEventArgs e)
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

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            throw new NotImplementedException();
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
            _timer = new Timer(TimeSpan.FromSeconds(5d).TotalMilliseconds);
            _timer.Elapsed += _timer_Elapsed;
            _timer.Start();
            Console.WriteLine("please set the delay interval(in minutes):");
            double interval = 30d;
            while (!double.TryParse(Console.ReadLine(), out interval))
            {
                Console.WriteLine("error, please try again:");
            }
            Console.WriteLine("running...");
            Task.Run(async () =>
            {
                while (true)
                {
                    var rssStr = "";
                    try
                    {
                        using (var client = new HttpClient())
                            rssStr = await client.GetStringAsync("http://leopard-raws.org/rss.php");
                    }
                    catch (HttpRequestException) { continue; }
                    catch (WebException) { continue; }
                    if (string.IsNullOrEmpty(rssStr)) continue;
                    SqlConnection connection = new SqlConnection("server=localhost;database=AnimateDataBase;Integrated Security=yes");
                    await connection.OpenAsync();
                    DataTable dataTable = new DataTable();
                    using (SqlDataAdapter sqladapter = new SqlDataAdapter("select DirPath,RegexPattern from AnimateList", connection))
                        sqladapter.Fill(dataTable);
                    connection.Close();
                    connection.Dispose();
                    connection = null;
                    //\[Leopard-Raws\](.+) - ([0-9]+) RAW
                    XDocument doc = XDocument.Parse(rssStr);
                    var list = (from item in doc.Descendants()
                                from dbitem in dataTable.Select()
                                where item.Name == "item"
                                //find the item
                                && Regex.IsMatch(item.Descendants().Where(node => node.Name == "title").FirstOrDefault().Value, dbitem.ItemArray[1].ToString())
                                //check the item if exist
                                && new DirectoryInfo(dbitem.ItemArray[0].ToString()).GetFiles().Where(file => Path.GetFileNameWithoutExtension(file.FullName) == Regex.Match(item.Descendants().Where(node => node.Name == "title").FirstOrDefault()?.Value, dbitem.ItemArray[1].ToString()).Groups[1].Value).FirstOrDefault() == null
                                //check the item if is downloading
                                && _dic.Where(download => download.Key.Url == item.Descendants().Where(node => node.Name == "link").FirstOrDefault()?.Value).Count() == 0
                                select new
                                {
                                    Title = item.Descendants().Where(node => node.Name == "title").FirstOrDefault()?.Value,
                                    Link = item.Descendants().Where(node => node.Name == "link").FirstOrDefault()?.Value,
                                    DirPath = dbitem.ItemArray[0].ToString(),
                                    FileName = Regex.Match(item.Descendants().Where(node => node.Name == "title").FirstOrDefault()?.Value, dbitem.ItemArray[1].ToString()).Groups[1].Value,
                                }).ToList();
                    dataTable.Dispose();
                    dataTable = null;
                    if (list.Count == 0) continue;
                    Parallel.ForEach(list, item =>
                    {
                        var torrentParams = new AddTorrentParams { SavePath = item.DirPath, Url = item.Link, UploadLimit = 10 * 1024, Name = item.FileName };
                        var handle = _session.AddTorrent(torrentParams);
                        _dic.Add(torrentParams, handle);
                        Console.WriteLine($"{item.Title} start downloading...");
                    });
                    await Task.Delay(TimeSpan.FromMinutes(interval));
                }
            }).Wait();
        }
    }

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
}
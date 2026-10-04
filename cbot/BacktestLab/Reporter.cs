// ส่งผล backtest ไป Edge Function "lab" — ส่งไม่ได้จะเก็บไว้ใน Documents\BacktestLab\pending แล้วส่งซ้ำตอนรันครั้งหน้า
//
// ค่าตั้งต่อเครื่อง: Documents\BacktestLab\config.json
//   { "apiUrl": "https://<project-ref>.supabase.co/functions/v1/lab", "ingestKey": "<INGEST_KEY>" }
// หรือ environment variables BACKTESTLAB_API_URL / BACKTESTLAB_INGEST_KEY

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using cAlgo.API;

namespace cAlgo.Robots
{
    public static class Reporter
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BacktestLab");

        private static string PendingFolder => Path.Combine(Folder, "pending");

        private class Config
        {
            public string ApiUrl { get; set; }
            public string IngestKey { get; set; }
        }

        private static Config LoadConfig(Robot bot)
        {
            var cfg = new Config();
            var file = Path.Combine(Folder, "config.json");
            if (File.Exists(file))
            {
                try
                {
                    cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(file),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? cfg;
                }
                catch (Exception e) { bot.Print("config.json error: {0}", e.Message); }
            }
            cfg.ApiUrl = Environment.GetEnvironmentVariable("BACKTESTLAB_API_URL") ?? cfg.ApiUrl;
            cfg.IngestKey = Environment.GetEnvironmentVariable("BACKTESTLAB_INGEST_KEY") ?? cfg.IngestKey;

            if (string.IsNullOrWhiteSpace(cfg.ApiUrl) || string.IsNullOrWhiteSpace(cfg.IngestKey))
            {
                bot.Print("Missing config. Create {0} with apiUrl and ingestKey (see SETUP.md).", file);
                return null;
            }
            cfg.ApiUrl = cfg.ApiUrl.TrimEnd('/');
            return cfg;
        }

        public static void Send(Robot bot, object report)
        {
            var json = JsonSerializer.Serialize(report);
            var cfg = LoadConfig(bot);

            if (cfg != null && TryPost(bot, cfg, json, out var response))
            {
                bot.Print("Sent to lab: {0}", response);
                return;
            }
            SavePending(bot, json);
        }

        public static void RetryPending(Robot bot)
        {
            if (!Directory.Exists(PendingFolder)) return;
            var files = Directory.GetFiles(PendingFolder, "*.json");
            if (files.Length == 0) return;

            var cfg = LoadConfig(bot);
            if (cfg == null) return;

            bot.Print("Retrying {0} pending report(s)...", files.Length);
            foreach (var f in files)
            {
                if (TryPost(bot, cfg, File.ReadAllText(f), out var response))
                {
                    File.Delete(f);
                    bot.Print("  sent {0}: {1}", Path.GetFileName(f), response);
                }
                else break; // ยังส่งไม่ได้ ไว้รอบหน้า
            }
        }

        private static bool TryPost(Robot bot, Config cfg, string json, out string response)
        {
            response = null;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, cfg.ApiUrl + "/ingest"))
                {
                    req.Headers.Add("x-ingest-key", cfg.IngestKey);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    var res = Task.Run(() => Http.SendAsync(req)).GetAwaiter().GetResult();
                    response = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (res.IsSuccessStatusCode) return true;
                    bot.Print("Lab responded {0}: {1}", (int)res.StatusCode, response);
                    return false;
                }
            }
            catch (Exception e)
            {
                bot.Print("Send failed: {0}", e.Message);
                return false;
            }
        }

        private static void SavePending(Robot bot, string json)
        {
            try
            {
                Directory.CreateDirectory(PendingFolder);
                var file = Path.Combine(PendingFolder, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json");
                File.WriteAllText(file, json);
                bot.Print("Saved for retry: {0}", file);
            }
            catch (Exception e) { bot.Print("Could not save pending report: {0}", e.Message); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MP4Levels
{
    /// <summary>
    /// Downloads links (YouTube etc.) as mp3 into the song folder with yt-dlp, one at a time, in the background.
    /// </summary>
    sealed class Downloader
    {
        readonly string folder;
        readonly Func<string> findYtDlp, findFfmpeg;
        readonly Queue<string> queue = new Queue<string>();
        readonly object gate = new object();
        bool running;

        /// <summary>Set when a download finished; the plugin rescans the folder on the main thread.</summary>
        public volatile bool RescanRequested;
        volatile string status;
        long statusExpiresTicks;

        static readonly Regex Percent = new Regex(@"\[download\]\s+([\d.]+)%", RegexOptions.Compiled);

        public Downloader(string folder, Func<string> findYtDlp, Func<string> findFfmpeg)
        {
            this.folder = folder;
            this.findYtDlp = findYtDlp;
            this.findFfmpeg = findFfmpeg;
        }

        /// <summary>Text to show on screen, or null.</summary>
        public string Status
        {
            get
            {
                string s = status;
                if (s == null) return null;
                lock (gate) if (!running && DateTime.UtcNow.Ticks > Interlocked.Read(ref statusExpiresTicks)) return null;
                return s;
            }
        }

        void SetStatus(string s, double seconds = 0)
        {
            status = s;
            Interlocked.Exchange(ref statusExpiresTicks, DateTime.UtcNow.AddSeconds(seconds).Ticks);
        }

        public void Enqueue(string clipboard)
        {
            string url = (clipboard ?? "").Trim();
            if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                || url.Contains(" ") || url.Contains("\n"))
            {
                SetStatus("YouTube download: copy a video link first, then press the download key.", 6);
                return;
            }
            lock (gate)
            {
                if (queue.Contains(url)) return;
                queue.Enqueue(url);
                int waiting = queue.Count + (running ? 1 : 0);
                if (running)
                {
                    SetStatus($"YouTube download: queued ({waiting} in line)");
                    return;
                }
                running = true;
            }
            new Thread(Run) { IsBackground = true, Name = "MP4Levels-download" }.Start();
        }

        void Run()
        {
            while (true)
            {
                string url;
                int left;
                lock (gate)
                {
                    if (queue.Count == 0) { running = false; return; }
                    url = queue.Dequeue();
                    left = queue.Count;
                }
                try { Download(url, left); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Download failed for " + url + ": " + e);
                    SetStatus("<color=#ff8080>YouTube download failed: " + e.Message + "</color>", 15);
                }
            }
        }

        void Download(string url, int left)
        {
            string ytdlp = findYtDlp();
            if (ytdlp == null)
                throw new Exception("yt-dlp not found. Install it once with:  winget install yt-dlp  (then restart the game)");
            string ffmpeg = findFfmpeg();

            bool playlist = url.IndexOf("/playlist?", StringComparison.OrdinalIgnoreCase) >= 0;
            string outTemplate = Path.Combine(folder, "%(title)s.%(ext)s");
            var args = new StringBuilder();
            args.Append(playlist ? "--yes-playlist " : "--no-playlist ");
            args.Append("-x --audio-format mp3 --audio-quality 0 --windows-filenames --no-mtime --no-overwrites ");
            args.Append("--encoding utf-8 --newline --progress --no-simulate ");
            args.Append("--print before_dl:\"TITLE:%(title)s\" --print after_move:\"FILE:%(filepath)s\" ");
            if (ffmpeg != null) args.Append("--ffmpeg-location \"").Append(Path.GetDirectoryName(ffmpeg)).Append("\" ");
            args.Append("-o \"").Append(outTemplate).Append("\" ");
            args.Append("\"").Append(url.Replace("\"", "")).Append("\"");

            var psi = new ProcessStartInfo(ytdlp, args.ToString())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PATH"] = FreshPath(ytdlp, ffmpeg);

            string title = "link";
            int done = 0;
            string lastError = null;
            string suffix = left > 0 ? $"  (+{left} queued)" : "";
            SetStatus($"YouTube download: starting...{suffix}");

            void OnLine(string line)
            {
                if (string.IsNullOrEmpty(line)) return;
                if (line.StartsWith("TITLE:")) { title = line.Substring(6).Trim(); SetStatus($"YouTube download: {title}...{suffix}"); return; }
                if (line.StartsWith("FILE:")) { done++; Plugin.Log.LogInfo("Downloaded " + line.Substring(5)); return; }
                var m = Percent.Match(line);
                if (m.Success) { SetStatus($"YouTube download: {title} - {m.Groups[1].Value}%{suffix}"); return; }
                if (line.Contains("ExtractAudio")) { SetStatus($"YouTube download: {title} - converting to mp3...{suffix}"); return; }
                if (line.StartsWith("ERROR:")) lastError = line.Substring(6).Trim();
            }

            using (var p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => OnLine(e.Data);
                p.ErrorDataReceived += (s, e) => OnLine(e.Data);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(30 * 60 * 1000)) { try { p.Kill(); } catch { } throw new Exception("timed out"); }
                p.WaitForExit();
                if (p.ExitCode != 0 && done == 0)
                {
                    string msg = lastError ?? $"yt-dlp exited with code {p.ExitCode}";
                    if (msg.Length > 220) msg = msg.Substring(0, 220) + "...";
                    throw new Exception(msg + "  (an outdated yt-dlp often causes this: winget upgrade yt-dlp)");
                }
            }

            if (done == 0)
            {
                SetStatus($"YouTube download: '{title}' is already in your songs.", 6);
                return;
            }
            SetStatus(done == 1 ? $"YouTube download: got '{title}' - charting it now..." : $"YouTube download: got {done} songs - charting...", 6);
            RescanRequested = true;
        }

        /// <summary>
        /// The game inherits PATH from Steam, which may predate installing yt-dlp and its helpers (Deno is needed
        /// for YouTube). Rebuild PATH from the registry and add the tool folders + any WinGet Deno install.
        /// </summary>
        static string FreshPath(string ytdlp, string ffmpeg)
        {
            var parts = new List<string>();
            void add(string v) { if (!string.IsNullOrEmpty(v)) parts.AddRange(v.Split(';')); }
            add(Path.GetDirectoryName(ytdlp));
            if (ffmpeg != null) add(Path.GetDirectoryName(ffmpeg));
            add(Environment.GetEnvironmentVariable("PATH"));
            try { add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)); } catch { }
            try { add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)); } catch { }
            try
            {
                string pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages");
                if (Directory.Exists(pk))
                    foreach (var d in Directory.GetDirectories(pk, "*Deno*"))
                    {
                        var deno = Directory.GetFiles(d, "deno.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (deno != null) add(Path.GetDirectoryName(deno));
                    }
            }
            catch { }
            return string.Join(";", parts.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }
}

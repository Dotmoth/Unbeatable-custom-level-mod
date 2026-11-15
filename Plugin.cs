using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Rhythm;
using UnityEngine;

namespace MP4Levels
{
    public enum VideoMode { Fullscreen, Corner, Off }

    public sealed class CustomSong
    {
        public string Name;        // key used by the game ("Name/Difficulty" paths)
        public string SourcePath;  // the mp4 the player dropped in
        public string AudioPath;   // extracted wav that FMOD plays
        public bool HasVideo;
        public readonly Dictionary<string, string> Charts = new Dictionary<string, string>();
        public readonly Dictionary<string, TextAsset> Assets = new Dictionary<string, TextAsset>();
    }

    [BepInPlugin(Guid, "MP4 Levels", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dotmoth.unbeatable.mp4levels";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        /// <summary>Songs that are registered with the game, keyed by song name. Main thread only.</summary>
        internal static readonly Dictionary<string, CustomSong> Ready = new Dictionary<string, CustomSong>();

        static readonly HashSet<string> VideoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi" };
        static readonly HashSet<string> AudioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".opus" };

        internal ConfigEntry<string> CfgFolder, CfgFfmpeg, CfgYtDlp;
        internal ConfigEntry<KeyCode> CfgRescanKey, CfgDownloadKey;
        internal ConfigEntry<int> CfgOffset, CfgMaxMinutes;
        internal ConfigEntry<float> CfgDensity;
        internal ConfigEntry<bool> CfgFlips, CfgHolds;
        internal ConfigEntry<VideoMode> CfgVideoMode;
        internal ConfigEntry<float> CfgVideoOpacity, CfgCornerSize;

        string folder, cacheRoot;
        readonly object queueLock = new object();
        readonly List<CustomSong> pending = new List<CustomSong>();
        volatile bool working;
        volatile string status;
        float statusUntil;
        GUIStyle statusStyle;
        Downloader downloader;

        void Awake()
        {
            Instance = this;
            Log = Logger;

            CfgFolder = Config.Bind("General", "Folder", "CustomSongs", "Folder (inside the game folder) that is scanned for songs (.mp3 etc.) and videos.");
            CfgFfmpeg = Config.Bind("General", "FfmpegPath", "", "Full path to ffmpeg.exe. Leave empty to search the folder above, the plugin folder, PATH and WinGet.");
            CfgRescanKey = Config.Bind("General", "RescanKey", KeyCode.F5, "Press to rescan the folder without restarting the game.");
            CfgDownloadKey = Config.Bind("Download", "DownloadKey", KeyCode.F6, "Copy a YouTube link, then press this in-game to download it as a song.");
            CfgYtDlp = Config.Bind("Download", "YtDlpPath", "", "Full path to yt-dlp.exe. Leave empty to search the song folder, the plugin folder, PATH and WinGet.");
            CfgOffset = Config.Bind("Charts", "OffsetMs", 0, "Shifts every generated note (ms). Positive = later. Changing it regenerates charts.");
            CfgDensity = Config.Bind("Charts", "Density", 1.0f, new ConfigDescription("Note density multiplier for every difficulty.", new AcceptableValueRange<float>(0.3f, 2.0f)));
            CfgFlips = Config.Bind("Charts", "SideFlips", true, "Generate side-switch markers.");
            CfgHolds = Config.Bind("Charts", "Holds", true, "Generate hold notes over sustained sounds.");
            CfgMaxMinutes = Config.Bind("Charts", "MaxLengthMinutes", 15, "Files longer than this are skipped.");
            CfgVideoMode = Config.Bind("Video", "Mode", VideoMode.Fullscreen, "Fullscreen = translucent video over the stage, Corner = picture-in-picture, Off = audio only.");
            CfgVideoOpacity = Config.Bind("Video", "FullscreenOpacity", 0.3f, new ConfigDescription("Opacity of the fullscreen video.", new AcceptableValueRange<float>(0.05f, 1f)));
            CfgCornerSize = Config.Bind("Video", "CornerSize", 0.3f, new ConfigDescription("Width of the corner video as a fraction of the screen.", new AcceptableValueRange<float>(0.1f, 0.6f)));

            folder = Path.Combine(Paths.GameRootPath, CfgFolder.Value);
            cacheRoot = Path.Combine(folder, "_cache");
            Directory.CreateDirectory(folder);
            WriteReadme();

            string pluginDir = Path.GetDirectoryName(Info.Location);
            downloader = new Downloader(folder,
                () => ToolLocator.Find("yt-dlp.exe", "*yt-dlp*", CfgYtDlp.Value, folder, pluginDir),
                () => FfmpegLocator.Find(CfgFfmpeg.Value, folder, pluginDir));

            new Harmony(Guid).PatchAll(typeof(Patches));
            Scan();
        }

        void Update()
        {
            List<CustomSong> got = null;
            lock (queueLock)
                if (pending.Count > 0) { got = new List<CustomSong>(pending); pending.Clear(); }
            if (got != null) Register(got);

            if (Input.GetKeyDown(CfgRescanKey.Value)) Scan();
            if (Input.GetKeyDown(CfgDownloadKey.Value)) downloader.Enqueue(GUIUtility.systemCopyBuffer);
            if (downloader.RescanRequested && !working) { downloader.RescanRequested = false; Scan(); }
        }

        void OnGUI()
        {
            string s = status;
            if (!working && Time.unscaledTime > statusUntil) s = null;
            string d = downloader?.Status;
            if (d != null) s = s == null ? d : d + "\n" + s;
            if (s == null) return;
            if (statusStyle == null)
                statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true, richText = true };
            var r = new Rect(14, 12, Mathf.Min(900, Screen.width - 28), 200);
            var shadow = new Rect(r.x + 2, r.y + 2, r.width, r.height);
            var c = GUI.color;
            GUI.color = Color.black; GUI.Label(shadow, s, statusStyle);
            GUI.color = c; GUI.Label(r, s, statusStyle);
        }

        // ------------------------------------------------------------------ scanning (background thread)

        internal void Scan()
        {
            if (working) return;
            var files = Directory.GetFiles(folder)
                .Where(f => VideoExts.Contains(Path.GetExtension(f)) || AudioExts.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0)
            {
                ShowStatus("MP4 Levels: put .mp3 files in <b>" + CfgFolder.Value + "</b> and press " + CfgRescanKey.Value + ".", 8);
                return;
            }
            working = true;
            var settings = new GeneratorSettings
            {
                OffsetMs = CfgOffset.Value, Density = CfgDensity.Value, Flips = CfgFlips.Value, Holds = CfgHolds.Value,
            };
            string ffConfigured = CfgFfmpeg.Value;
            int maxMinutes = CfgMaxMinutes.Value;
            var t = new Thread(() => Work(files, settings, ffConfigured, maxMinutes)) { IsBackground = true, Name = "MP4Levels" };
            t.Start();
        }

        void Work(List<string> files, GeneratorSettings settings, string ffConfigured, int maxMinutes)
        {
            var errors = new List<string>();
            int ok = 0;
            try
            {
                string ffmpeg = null;
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Count; i++)
                {
                    string file = files[i];
                    string name = UniqueName(Path.GetFileNameWithoutExtension(file), used);
                    status = $"MP4 Levels: preparing {i + 1}/{files.Count} - {name}";
                    try
                    {
                        var song = LoadOrBuild(file, name, settings, ffConfigured, maxMinutes, ref ffmpeg);
                        lock (queueLock) pending.Add(song);
                        ok++;
                    }
                    catch (Exception e)
                    {
                        Log.LogError($"{Path.GetFileName(file)}: {e}");
                        errors.Add($"{Path.GetFileName(file)}: {e.Message}");
                    }
                }
            }
            finally
            {
                var sb = new StringBuilder($"MP4 Levels: {ok} song{(ok == 1 ? "" : "s")} ready - Arcade song list.");
                foreach (var e in errors.Take(4)) sb.Append("\n<color=#ff8080>").Append(e).Append("</color>");
                status = sb.ToString();
                statusUntil = float.MaxValue; // set properly on the main thread
                working = false;
                pendingStatusSeconds = errors.Count > 0 ? 20 : 8;
            }
        }

        volatile int pendingStatusSeconds;

        void LateUpdate()
        {
            int secs = pendingStatusSeconds;
            if (secs > 0) { pendingStatusSeconds = 0; statusUntil = Time.unscaledTime + secs; }
        }

        void ShowStatus(string s, float seconds) { status = s; statusUntil = Time.unscaledTime + seconds; }

        CustomSong LoadOrBuild(string file, string name, GeneratorSettings st, string ffConfigured, int maxMinutes, ref string ffmpeg)
        {
            var fi = new FileInfo(file);
            string dir = Path.Combine(cacheRoot, SafeDirName(name));
            Directory.CreateDirectory(dir);
            string wav = Path.Combine(dir, "audio.wav");
            string stampPath = Path.Combine(dir, "stamp.txt");
            string stamp = string.Join("|", "v" + ChartGenerator.Version, fi.Length, fi.LastWriteTimeUtc.Ticks,
                st.OffsetMs, st.Density.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), st.Flips, st.Holds);

            bool fresh = File.Exists(stampPath) && File.ReadAllText(stampPath) == stamp && File.Exists(wav)
                         && ChartGenerator.Difficulties.All(d => File.Exists(Path.Combine(dir, d + ".osu")));
            if (!fresh)
            {
                if (ffmpeg == null) ffmpeg = FfmpegLocator.Find(ffConfigured, folder, Path.GetDirectoryName(Info.Location));
                if (ffmpeg == null)
                    throw new Exception("ffmpeg.exe not found. Put ffmpeg.exe in the " + CfgFolder.Value + " folder or set FfmpegPath in BepInEx/config/" + Guid + ".cfg");

                status = $"MP4 Levels: extracting audio - {name}";
                ExtractAudio(ffmpeg, file, wav);

                status = $"MP4 Levels: charting - {name}";
                var audio = WavReader.ReadMono(wav);
                if (audio.Duration > maxMinutes * 60)
                    throw new Exception($"longer than {maxMinutes} minutes (see MaxLengthMinutes)");
                var meta = MetaFromName(name);
                var charts = ChartGenerator.Generate(audio, meta, st, out var an);
                foreach (var c in charts) File.WriteAllText(Path.Combine(dir, c.Difficulty + ".osu"), c.Text);
                File.WriteAllText(stampPath, stamp);
                Log.LogInfo($"Charted '{name}': {an.Bpm:0.0} BPM, {an.TimingPoints.Count} timing point(s), "
                            + string.Join(", ", charts.Select(c => $"{c.Difficulty} {c.NoteCount} notes Lv{c.Level}")));
            }
            else Log.LogInfo($"Using cached charts for '{name}' ({dir})");

            var song = new CustomSong { Name = name, SourcePath = file, AudioPath = wav, HasVideo = VideoExts.Contains(fi.Extension) };
            foreach (var d in ChartGenerator.Difficulties)
                song.Charts[d] = File.ReadAllText(Path.Combine(dir, d + ".osu"));
            return song;
        }

        static void ExtractAudio(string ffmpeg, string input, string wavOut)
        {
            string tmp = wavOut + ".part.wav";
            var psi = new ProcessStartInfo(ffmpeg,
                $"-y -hide_banner -loglevel error -i \"{input}\" -vn -map 0:a:0 -ac 2 -ar 44100 -c:a pcm_s16le \"{tmp}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            var err = new StringBuilder();
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                p.OutputDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                if (!p.WaitForExit(10 * 60 * 1000)) { try { p.Kill(); } catch { } throw new Exception("ffmpeg timed out"); }
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(tmp))
                {
                    string msg = err.ToString().Trim();
                    if (msg.Contains("matches no streams") || msg.Contains("does not contain any stream")) msg = "the file has no audio track";
                    throw new Exception("ffmpeg failed: " + (msg.Length > 300 ? msg.Substring(0, 300) : msg));
                }
            }
            if (File.Exists(wavOut)) File.Delete(wavOut);
            File.Move(tmp, wavOut);
        }

        // "(Official Music Video)", "[Lyrics]", "(HD)" ... tags that YouTube titles carry
        static readonly System.Text.RegularExpressions.Regex TitleJunk = new System.Text.RegularExpressions.Regex(
            @"\s*[\(\[][^\)\]]*\b(official|lyrics?|audio|video|visuali[sz]er|hd|hq|4k|mv|m/v|clip officiel)\b[^\)\]]*[\)\]]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static SongMeta MetaFromName(string name)
        {
            string clean = TitleJunk.Replace(name, "").Trim();
            if (clean.Length == 0) clean = name;
            int dash = clean.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash < clean.Length - 3)
                return new SongMeta { Artist = clean.Substring(0, dash).Trim(), Title = clean.Substring(dash + 3).Trim() };
            return new SongMeta { Title = clean, Artist = "Custom song" };
        }

        static string UniqueName(string raw, HashSet<string> used)
        {
            string n = raw.Replace('/', '-').Replace('\\', '-').Trim();
            if (n.Length == 0) n = "Untitled";
            string c = n; int k = 2;
            while (!used.Add(c)) c = $"{n} ({k++})";
            return c;
        }

        static string SafeDirName(string n)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch, '_');
            return n.TrimEnd('.', ' ');
        }

        // ------------------------------------------------------------------ registering with the game (main thread)

        static readonly FieldInfo F_IndexSongs = AccessTools.Field(typeof(BeatmapIndex), "songs");
        static readonly FieldInfo F_IndexSongDict = AccessTools.Field(typeof(BeatmapIndex), "_songs");
        static readonly FieldInfo F_IndexSongNames = AccessTools.Field(typeof(BeatmapIndex), "_songNames");
        static readonly FieldInfo F_IndexDiffs = AccessTools.Field(typeof(BeatmapIndex), "difficulties");
        static readonly FieldInfo F_IndexDiffSet = AccessTools.Field(typeof(BeatmapIndex), "_difficulties");
        static readonly FieldInfo F_SongBeatmaps = AccessTools.Field(typeof(BeatmapIndex.Song), "beatmaps");
        static readonly FieldInfo F_SongBeatmapDict = AccessTools.Field(typeof(BeatmapIndex.Song), "_beatmaps");
        static readonly FieldInfo F_SongDiffs = AccessTools.Field(typeof(BeatmapIndex.Song), "_difficulties");
        static readonly FieldInfo F_MenuSongsInc = AccessTools.Field(typeof(WhiteLabelMainMenu), "songsInc");
        static readonly FieldInfo F_WrapCount = AccessTools.Field(typeof(WrapCounter), "count");

        static readonly HashSet<BeatmapIndex.Song> OurSongs = new HashSet<BeatmapIndex.Song>();
        static BeatmapIndex injectedIndex;
        static int injectedGen = -1, gen;

        void Register(List<CustomSong> songs)
        {
            var index = BeatmapIndex.defaultIndex;
            var dict = index != null ? (Dictionary<string, BeatmapIndex.Song>)F_IndexSongDict.GetValue(index) : null;
            foreach (var s in songs)
            {
                // never shadow one of the game's own songs
                if (dict != null && dict.TryGetValue(s.Name, out var existing) && !OurSongs.Contains(existing))
                    s.Name += " (MP4)";
                foreach (var kv in s.Charts)
                {
                    var ta = new TextAsset(kv.Value) { name = s.Name + "/" + kv.Key, hideFlags = HideFlags.DontUnloadUnusedAsset };
                    s.Assets[kv.Key] = ta;
                }
                if (Ready.TryGetValue(s.Name, out var old))
                    foreach (var ta in old.Assets.Values) if (ta) Destroy(ta);
                Ready[s.Name] = s;
            }
            gen++;
            if (index != null) EnsureInjected(index);
            RefreshOpenMenus();
        }

        internal static void EnsureInjected(BeatmapIndex index)
        {
            if (index == injectedIndex && injectedGen == gen) return;
            var list = (List<BeatmapIndex.Song>)F_IndexSongs.GetValue(index);
            var dict = (Dictionary<string, BeatmapIndex.Song>)F_IndexSongDict.GetValue(index);
            var names = (List<string>)F_IndexSongNames.GetValue(index);
            if (list == null || dict == null || names == null) return;

            string stage = list.Where(s => !OurSongs.Contains(s) && !string.IsNullOrEmpty(s.stageScene))
                               .GroupBy(s => s.stageScene).OrderByDescending(g => g.Count()).Select(g => g.Key)
                               .FirstOrDefault() ?? "TrainStationRhythm";

            var diffs = (List<string>)F_IndexDiffs.GetValue(index);
            var diffSet = (HashSet<string>)F_IndexDiffSet.GetValue(index);
            foreach (var d in ChartGenerator.Difficulties)
            {
                if (diffs != null && !diffs.Contains(d)) diffs.Add(d);
                diffSet?.Add(d);
            }

            foreach (var cs in Ready.Values)
            {
                if (dict.TryGetValue(cs.Name, out var existing))
                {
                    if (!OurSongs.Contains(existing)) continue;
                    list.Remove(existing);
                    OurSongs.Remove(existing);
                }
                var song = new BeatmapIndex.Song(cs.Name) { stageScene = stage };
                var bl = (List<BeatmapInfo>)F_SongBeatmaps.GetValue(song);
                var bd = new Dictionary<string, BeatmapInfo>();
                var dl = new List<string>();
                foreach (var d in ChartGenerator.Difficulties)
                {
                    if (!cs.Assets.TryGetValue(d, out var ta)) continue;
                    var info = new BeatmapInfo(ta, d);
                    bl.Add(info); bd[d] = info; dl.Add(d);
                }
                F_SongBeatmapDict.SetValue(song, bd);
                F_SongDiffs.SetValue(song, dl);
                list.Add(song);
                dict[cs.Name] = song;
                if (!names.Contains(cs.Name)) names.Add(cs.Name);
                OurSongs.Add(song);
            }
            Log.LogInfo($"Song index now has {names.Count} songs ({Ready.Count} from MP4 Levels, stage '{stage}')");
            injectedIndex = index;
            injectedGen = gen;
        }

        internal static void AddSongsToMenu(WhiteLabelMainMenu menu)
        {
            if (menu == null || menu.songs == null) return;
            foreach (var name in Ready.Keys)
                if (!menu.songs.Contains(name)) menu.songs.Add(name);
        }

        static void RefreshOpenMenus()
        {
            foreach (var menu in FindObjectsOfType<WhiteLabelMainMenu>())
            {
                AddSongsToMenu(menu);
                try
                {
                    object counter = F_MenuSongsInc.GetValue(menu);
                    F_WrapCount.SetValue(counter, menu.songs.Count);
                    F_MenuSongsInc.SetValue(menu, counter);
                }
                catch (Exception e) { Log.LogWarning("Could not refresh the open song list: " + e.Message); }
            }
        }

        void WriteReadme()
        {
            string p = Path.Combine(folder, "README.txt");
            if (File.Exists(p)) return;
            File.WriteAllText(p,
                "MP4 Levels\r\n" +
                "==========\r\n\r\n" +
                "Drop .mp3 files (or .ogg/.wav/.flac/.m4a/.opus, or videos: .mp4/.mkv/.webm/.mov) into this folder.\r\n" +
                "Start the game (or press F5 in-game) and they appear at the end of the Arcade song list\r\n" +
                "with five auto-generated difficulties: Beginner, Easy, Normal, Hard, UNBEATABLE.\r\n\r\n" +
                "Name files \"Artist - Title.mp3\" to fill in the artist. Videos also play behind the level.\r\n\r\n" +
                "Needs ffmpeg.exe: either on your PATH, or copied into this folder.\r\n\r\n" +
                "YouTube: copy a video link, then press F6 in-game. The song is downloaded here as .mp3,\r\n" +
                "charted and added to the song list. Needs yt-dlp (install once: winget install yt-dlp).\r\n" +
                "Playlist links (youtube.com/playlist?list=...) download the whole playlist.\r\n\r\n" +
                "Generated audio + charts are cached in _cache\\<song>\\ . The .osu files there are plain\r\n" +
                "osu!mania-style text and can be hand-edited; they are only regenerated when the video changes\r\n" +
                "or chart settings change. Delete a song's _cache folder to force a re-chart.\r\n\r\n" +
                "Settings: BepInEx\\config\\" + Guid + ".cfg (note offset, density, video mode/opacity).\r\n");
        }
    }

    static class FfmpegLocator
    {
        public static string Find(string configured, string folder, string pluginDir) =>
            ToolLocator.Find("ffmpeg.exe", "*FFmpeg*", configured, folder, pluginDir);
    }

    static class ToolLocator
    {
        /// <summary>Finds an exe: configured path, song folder, plugin folder, PATH, common package managers, WinGet.</summary>
        public static string Find(string exe, string wingetPattern, string configured, string folder, string pluginDir)
        {
            string bare = Path.GetFileNameWithoutExtension(exe);
            var c = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured)) c.Add(configured.Trim().Trim('"'));
            c.Add(Path.Combine(folder, exe));
            if (pluginDir != null) c.Add(Path.Combine(pluginDir, exe));
            foreach (var path in PathEntries()) c.Add(Path.Combine(path, exe));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            c.Add(Path.Combine(local, @"Microsoft\WinGet\Links\" + exe));
            c.Add(@"C:\" + bare + @"\bin\" + exe);
            c.Add(@"C:\ProgramData\chocolatey\bin\" + exe);
            c.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"scoop\shims\" + exe));
            c.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), bare + @"\bin\" + exe));
            c.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), bare + @"\" + exe));
            foreach (var p in c)
                try { if (File.Exists(p)) return p; } catch { }

            try
            {
                string pk = Path.Combine(local, @"Microsoft\WinGet\Packages");
                if (Directory.Exists(pk))
                    foreach (var d in Directory.GetDirectories(pk, wingetPattern))
                    {
                        var hit = Directory.GetFiles(d, exe, SearchOption.AllDirectories).FirstOrDefault();
                        if (hit != null) return hit;
                    }
            }
            catch { }
            return null;
        }

        static IEnumerable<string> PathEntries()
        {
            var all = new List<string>();
            void add(string v) { if (!string.IsNullOrEmpty(v)) all.AddRange(v.Split(';')); }
            add(Environment.GetEnvironmentVariable("PATH"));
            try { add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)); } catch { }
            try { add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)); } catch { }
            return all.Select(s => s.Trim().Trim('"')).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }

    [HarmonyPatch]
    static class Patches
    {
        // Our songs live in the game's BeatmapIndex asset; re-add them whenever the asset is (re)loaded.
        [HarmonyPatch(typeof(BeatmapIndex), "get_defaultIndex")]
        [HarmonyPostfix]
        static void DefaultIndex(BeatmapIndex __result)
        {
            if (__result != null && Plugin.Ready.Count > 0) Plugin.EnsureInjected(__result);
        }

        // The arcade menu has a hard-coded song list; append ours before it builds its counters.
        [HarmonyPatch(typeof(WhiteLabelMainMenu), "Start")]
        [HarmonyPrefix]
        static void MenuStart(WhiteLabelMainMenu __instance) => Plugin.AddSongsToMenu(__instance);

        // Song select skips locked songs; ours are always unlocked.
        [HarmonyPatch(typeof(DialogueVariables), "GetSongUnlockedState")]
        [HarmonyPostfix]
        static void Unlocked(string songKey, ref bool __result)
        {
            if (songKey != null && Plugin.Ready.ContainsKey(songKey)) __result = true;
        }

        // Both the menu preview and gameplay load music through this; play our wav from disk instead of the FMOD table.
        [HarmonyPatch(typeof(RhythmTracker), "PrepareInstance")]
        [HarmonyPrefix]
        static void PrepareInstance(ref PlaySource source, ref string key)
        {
            if (source == PlaySource.FromTable && key != null && Plugin.Ready.TryGetValue(key, out var s))
            {
                source = PlaySource.FromFile;
                key = s.AudioPath;
            }
        }

        // Show the video while a custom song is being played.
        [HarmonyPatch(typeof(RhythmController), "Start")]
        [HarmonyPostfix]
        static void RhythmStart(RhythmController __instance)
        {
            try
            {
                var p = Plugin.Instance;
                string key = __instance.parser != null ? __instance.parser.audioKey : null;
                if (p == null || key == null || !Plugin.Ready.TryGetValue(key, out var s) || !s.HasVideo) return;
                if (p.CfgVideoMode.Value == VideoMode.Off) return;
                var go = new GameObject("MP4Levels Video");
                go.transform.SetParent(__instance.transform, false);
                go.AddComponent<VideoOverlay>().Init(__instance, s.SourcePath, p.CfgVideoMode.Value, p.CfgVideoOpacity.Value, p.CfgCornerSize.Value);
            }
            catch (Exception e) { Plugin.Log.LogError("Video setup failed: " + e); }
        }
    }
}

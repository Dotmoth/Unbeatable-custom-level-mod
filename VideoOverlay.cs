using System;
using Rhythm;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace MP4Levels
{
    /// <summary>Plays the source video on a screen-space canvas, slaved to the song's timeline.</summary>
    public class VideoOverlay : MonoBehaviour
    {
        RhythmController ctrl;
        VideoPlayer vp;
        RenderTexture rt;
        RawImage img;
        AspectRatioFitter fitter;
        bool prepared, failed, started;
        float nextSeek;

        public void Init(RhythmController controller, string path, VideoMode mode, float opacity, float cornerSize)
        {
            ctrl = controller;

            var canvasGo = new GameObject("MP4Levels Video Canvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -500; // below the game's HUD canvases

            var imgGo = new GameObject("Video");
            imgGo.transform.SetParent(canvasGo.transform, false);
            img = imgGo.AddComponent<RawImage>();
            img.raycastTarget = false;
            img.enabled = false;
            fitter = imgGo.AddComponent<AspectRatioFitter>();
            var r = img.rectTransform;
            if (mode == VideoMode.Corner)
            {
                img.color = new Color(1, 1, 1, 0.95f);
                r.anchorMin = r.anchorMax = r.pivot = new Vector2(1, 1);
                r.anchoredPosition = new Vector2(-24, -24);
                r.sizeDelta = new Vector2(Screen.width * cornerSize, Screen.width * cornerSize * 9 / 16f);
                fitter.aspectMode = AspectRatioFitter.AspectMode.WidthControlsHeight;
            }
            else
            {
                img.color = new Color(1, 1, 1, opacity);
                r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
                r.offsetMin = r.offsetMax = Vector2.zero;
                fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            }
            fitter.aspectRatio = 16 / 9f;

            vp = gameObject.AddComponent<VideoPlayer>();
            vp.playOnAwake = false;
            vp.source = VideoSource.Url;
            vp.url = path;
            vp.audioOutputMode = VideoAudioOutputMode.None;
            vp.isLooping = false;
            vp.skipOnDrop = true;
            vp.waitForFirstFrame = true;
            vp.renderMode = VideoRenderMode.RenderTexture;
            vp.errorReceived += (p, msg) =>
            {
                failed = true;
                if (img) img.enabled = false;
                Plugin.Log.LogWarning("Video playback error (the song still works without it): " + msg);
            };
            vp.prepareCompleted += OnPrepared;
            vp.Prepare();
        }

        void OnPrepared(VideoPlayer p)
        {
            int w = (int)p.width, h = (int)p.height;
            if (w <= 0 || h <= 0) { w = 1280; h = 720; }
            rt = new RenderTexture(w, h, 0);
            rt.Create();
            p.targetTexture = rt;
            img.texture = rt;
            fitter.aspectRatio = w / (float)h;
            prepared = true;
        }

        void Update()
        {
            if (!prepared || failed || ctrl == null) return;
            var tracker = ctrl.songTracker;
            if (tracker == null) return;

            double target = tracker.TimelinePosition / 1000.0;
            bool running = JeffBezosController.isPlayingBeatmap && ctrl.song.isPlaying && !ctrl.song.gameOver
                           && Time.timeScale > 0 && target >= 0 && target < vp.length;
            if (running)
            {
                vp.playbackSpeed = Mathf.Max(0.05f, Time.timeScale * tracker.Pitch);
                if (!vp.isPlaying)
                {
                    vp.time = target;
                    vp.Play();
                    started = true;
                }
                else if (Math.Abs(vp.time - target) > 0.2 && Time.unscaledTime >= nextSeek)
                {
                    vp.time = target;
                    nextSeek = Time.unscaledTime + 1.5f;
                }
            }
            else if (vp.isPlaying) vp.Pause();

            img.enabled = started;
        }

        void OnDestroy()
        {
            if (rt != null) { rt.Release(); Destroy(rt); }
        }
    }
}

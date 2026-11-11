using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace OpenPlane.Mod
{
    /// <summary>
    /// Developer filming for the self-test (config Developer/SelfTestVideo). Cameras of its own (two, so two takeoffs at once
    /// can both be filmed) follow one plane each and save their frames (1920x1080 JPG, about 30 a second) to
    /// BepInEx/OpenPlane-video/&lt;clip&gt;/, with a clip.txt that gives the real length, so the frames can be made into a video
    /// that plays at the right speed. The game's own view is untouched. Requests queue up; takeoff runs go first, and a
    /// request that waited too long is dropped.
    /// </summary>
    internal sealed class SelfTestFilm
    {
        internal enum Kind { Chase, Orbit, Flyby, Cockpit, Takeoff, Climb }

        internal sealed class Request
        {
            public Aircraft Plane = null!;
            public string Label = "";
            public Kind Kind;
            public float Seconds;
            public float Deadline = float.MaxValue;
            public int Priority;
            /// <summary>For a takeoff: the runway direction (the plane's heading when it started its run).</summary>
            public Vector3 Along;
        }

        private sealed class Rig
        {
            public Camera Cam = null!;
            public RenderTexture Rt = null!;
            public Texture2D Tex = null!;
            public bool Busy;
        }

        private const int Width = 1920, Height = 1080, Fps = 30, Rigs = 2;
        private readonly List<Request> _queue = new List<Request>();
        private readonly List<Rig> _rigs = new List<Rig>();
        private int _clips, _frames, _dropped, _writeErrors;
        private bool _encodeOnMainThread;
        private BlockingCollection<(byte[] data, string path)>? _writes;
        private readonly ConcurrentBag<byte[]> _buffers = new ConcurrentBag<byte[]>();
        private readonly List<Thread> _writers = new List<Thread>();
        private UnityEngine.Experimental.Rendering.GraphicsFormat _format;
        private int _pending;

        public bool Stop;
        public bool Idle => _queue.Count == 0 && _rigs.All(r => !r.Busy);

        public static string Folder => string.IsNullOrWhiteSpace(Plugin.SelfTestVideoFolder.Value)
            ? Path.Combine(BepInEx.Paths.BepInExRootPath, "OpenPlane-video") : Plugin.SelfTestVideoFolder.Value.Trim();

        public void Add(Request r)
        {
            if (r.Plane == null) return;
            _queue.Add(r);
        }

        /// <summary>One loop per camera; each films queued requests until <see cref="Stop"/> is set and the queue is empty.</summary>
        public IEnumerable<IEnumerator> Loops()
        {
            Prepare();
            return _rigs.Select(Loop).ToList();
        }

        private IEnumerator Loop(Rig rig)
        {
            while (!Stop || _queue.Count > 0)
            {
                _queue.RemoveAll(r => r.Plane == null || Time.time > r.Deadline);
                var next = _queue.OrderByDescending(r => r.Priority).FirstOrDefault();
                if (next == null) { yield return null; continue; }
                _queue.Remove(next);
                rig.Busy = true;
                yield return Film(rig, next);
                rig.Busy = false;
            }
        }

        /// <summary>Waits for the frames still being written, then reports what was filmed.</summary>
        public string Finish()
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            while (Volatile.Read(ref _pending) > 0 && DateTime.UtcNow < until) Thread.Sleep(50);
            _writes?.CompleteAdding();
            foreach (var t in _writers) t.Join(5000);
            foreach (var rig in _rigs)
            {
                if (rig.Rt != null) { rig.Rt.Release(); UnityEngine.Object.Destroy(rig.Rt); }
                if (rig.Cam != null) UnityEngine.Object.Destroy(rig.Cam.gameObject);
            }
            _rigs.Clear();
            return $"video: {_clips} clips, {_frames} frames in {Folder}" + (_dropped > 0 ? $" ({_dropped} frames dropped: writing fell behind)" : "")
                   + (_writeErrors > 0 ? $" ({_writeErrors} frames couldn't be written)" : "");
        }

        // ------------------------------------------------------------------ one clip

        private IEnumerator Film(Rig rig, Request r)
        {
            var a = r.Plane;
            if (a == null) yield break;
            var (center, size) = SelfTest.Frame(a);
            var localCenter = a.transform.InverseTransformPoint(center);
            // A takeoff filmed after the plane already left the ground becomes a climb-out chase.
            var kind = r.Kind;
            if (kind == Kind.Takeoff && RadarAlt(a) > 12f) kind = Kind.Climb;
            float seconds = kind == r.Kind ? r.Seconds : Mathf.Min(r.Seconds, 8f);
            string dir = Path.Combine(Folder, $"{++_clips:000} {SelfTest.Safe(r.Label)} - {kind.ToString().ToLowerInvariant()}");
            try { Directory.CreateDirectory(dir); } catch (Exception e) { Plugin.Log.LogWarning("[SelfTest] video folder: " + e.Message); yield break; }

            Vector3 heading = Heading(a);
            Vector3 fixedAt = Vector3.zero;
            if (kind == Kind.Flyby)
            {
                var v = a.rb != null && a.rb.velocity.sqrMagnitude > 25f ? a.rb.velocity : a.transform.forward * 100f;
                var side = Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(v, Vector3.up).normalized);
                float off = size * 1.2f + 12f;
                fixedAt = center + v * (seconds * 0.45f) + side * off - Vector3.up * (off * 0.25f);
            }
            else if (kind == Kind.Takeoff)
            {
                var along = Vector3.ProjectOnPlane(r.Along.sqrMagnitude > 0.01f ? r.Along : a.transform.forward, Vector3.up).normalized;
                var side = Vector3.Cross(Vector3.up, along);
                // Beside the runway, a little ahead of where the run starts, at eye height; the side with a clear view.
                fixedAt = center + along * 140f + side * (size + 28f) + Vector3.up * 2.5f;
                var other = center + along * 140f - side * (size + 28f) + Vector3.up * 2.5f;
                if (SelfTest.Blocked(a, center, fixedAt) && !SelfTest.Blocked(a, center, other)) fixedAt = other;
            }

            var cam = rig.Cam;
            float start = Time.unscaledTime, next = start, last = start;
            int frames = 0;
            float fov = cam.fieldOfView, near = cam.nearClipPlane;
            while (Time.unscaledTime - start < seconds && !(a == null))
            {
                yield return new WaitForEndOfFrame();
                if (a == null) break;
                float now = Time.unscaledTime;
                heading = Vector3.Slerp(heading, Heading(a), 1f - Mathf.Exp(-Time.unscaledDeltaTime * 1.5f));
                if (now < next) continue;
                next += 1f / Fps;
                if (next < now - 0.1f) next = now;
                float t = Mathf.Clamp01((now - start) / Mathf.Max(0.1f, seconds));
                var c = a.transform.TransformPoint(localCenter);
                try
                {
                    Pose(cam, kind, a, c, size, heading, fixedAt, t);
                    Capture(rig, Path.Combine(dir, $"f{frames + 1:00000}.jpg"));
                    frames++;
                    last = now;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[SelfTest] filming {r.Label} failed: {e.Message}"); break; }
            }
            cam.fieldOfView = fov; cam.nearClipPlane = near;
            _frames += frames;
            try
            {
                File.WriteAllText(Path.Combine(dir, "clip.txt"),
                    $"label={r.Label}\nshot={kind.ToString().ToLowerInvariant()}\nframes={frames}\nseconds={Math.Max(0.01, last - start + 1.0 / Fps):0.000}\n" +
                    $"fps={(frames > 1 ? (frames - 1) / Math.Max(0.01, last - start) : Fps):0.00}\n");
            }
            catch { }
        }

        private static void Pose(Camera cam, Kind kind, Aircraft a, Vector3 c, float size, Vector3 heading, Vector3 fixedAt, float t)
        {
            var up = Vector3.up;
            Vector3 Around(float azimuth, float elevation, float dist)
            {
                var d = Quaternion.AngleAxis(azimuth, up) * heading;
                d = Vector3.Slerp(d, up, elevation / 90f).normalized;
                return c + d * dist;
            }
            switch (kind)
            {
                case Kind.Cockpit:
                {
                    var eye = a.cockpitViewPoint != null ? a.cockpitViewPoint : (a.cockpit != null ? a.cockpit.transform : a.transform);
                    cam.fieldOfView = 72f; cam.nearClipPlane = 0.02f;
                    cam.transform.position = eye.position;
                    cam.transform.rotation = Quaternion.LookRotation(a.transform.forward, a.transform.up) * Quaternion.Euler(7f, 0, 0);
                    return;
                }
                case Kind.Flyby:
                case Kind.Takeoff:
                {
                    cam.nearClipPlane = 0.5f;
                    cam.transform.position = fixedAt;
                    cam.transform.LookAt(c, up);
                    float dist = Mathf.Max(1f, Vector3.Distance(fixedAt, c));
                    // A camera operator's zoom: the plane fills about half the width until it is close.
                    cam.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(size * 0.6f / dist) * Mathf.Rad2Deg, 1.5f, 55f);
                    return;
                }
                case Kind.Orbit:
                    cam.fieldOfView = 40f; cam.nearClipPlane = 0.5f;
                    cam.transform.position = Around(Mathf.Lerp(35f, 165f, Smooth(t)), Mathf.Lerp(10f, 6f, t), size * 1.35f + 5f);
                    break;
                case Kind.Climb:
                    cam.fieldOfView = 42f; cam.nearClipPlane = 0.5f;
                    cam.transform.position = Around(Mathf.Lerp(160f, 195f, Smooth(t)), Mathf.Lerp(3f, 9f, t), size * 1.3f + 5f);
                    break;
                default:   // chase
                    cam.fieldOfView = 40f; cam.nearClipPlane = 0.5f;
                    cam.transform.position = Around(Mathf.Lerp(205f, 140f, Smooth(t)), Mathf.Lerp(13f, 7f, t), size * 1.25f + 4f);
                    break;
            }
            // Never under the ground (low passes, takeoffs).
            if (Physics.Raycast(cam.transform.position + up * 200f, Vector3.down, out var hit, 200.5f, ~0, QueryTriggerInteraction.Ignore)
                && hit.collider.GetComponentInParent<Aircraft>() != a && hit.point.y + 1.5f > cam.transform.position.y)
                cam.transform.position = new Vector3(cam.transform.position.x, hit.point.y + 1.5f, cam.transform.position.z);
            cam.transform.LookAt(c, up);
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        private static Vector3 Heading(Aircraft a)
        {
            var v = a.rb != null ? a.rb.velocity : Vector3.zero;
            var f = Vector3.ProjectOnPlane(v.sqrMagnitude > 25f ? v : a.transform.forward, Vector3.up);
            return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        }

        private static float RadarAlt(Aircraft a)
        {
            try { return a.radarAlt; } catch { return 999f; }
        }

        // ------------------------------------------------------------------ cameras and frame writing

        private void Prepare()
        {
            if (_rigs.Count > 0) return;
            Directory.CreateDirectory(Folder);
            for (int i = 0; i < Rigs; i++)
            {
                var rig = new Rig { Cam = SelfTest.MakeCamera("OpenPlane film camera " + i) };
                rig.Rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                rig.Cam.targetTexture = rig.Rt;
                rig.Tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                _format = rig.Tex.graphicsFormat;
                _rigs.Add(rig);
            }
            _writes = new BlockingCollection<(byte[], string)>(120);
            for (int i = 0; i < 4; i++)
            {
                var th = new Thread(WriteFrames) { IsBackground = true, Name = "OpenPlane frame writer " + i };
                th.Start();
                _writers.Add(th);
            }
        }

        private void Capture(Rig rig, string path)
        {
            rig.Cam.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = rig.Rt;
            rig.Tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            RenderTexture.active = previous;
            if (_encodeOnMainThread)
            {
                rig.Tex.Apply(false);
                File.WriteAllBytes(path, rig.Tex.EncodeToJPG(90));
                return;
            }
            var raw = rig.Tex.GetRawTextureData<byte>();
            if (!_buffers.TryTake(out var buf) || buf.Length != raw.Length) buf = new byte[raw.Length];
            raw.CopyTo(buf);
            Interlocked.Increment(ref _pending);
            // Waits briefly when the writers fall behind (a missing frame would cut the clip short when it is encoded).
            if (!_writes!.TryAdd((buf, path), 3000)) { Interlocked.Decrement(ref _pending); _buffers.Add(buf); _dropped++; }
        }

        private void WriteFrames()
        {
            foreach (var (data, path) in _writes!.GetConsumingEnumerable())
            {
                try
                {
                    var jpg = ImageConversion.EncodeArrayToJPG(data, _format, Width, Height, 0, 90);
                    File.WriteAllBytes(path, jpg);
                }
                catch (Exception e)
                {
                    // Encoding off the main thread isn't allowed here: the rest is encoded on the main thread.
                    _encodeOnMainThread = true;
                    Interlocked.Increment(ref _writeErrors);
                    Plugin.Log.LogWarning("[SelfTest] frame writer: " + e.Message);
                }
                finally
                {
                    _buffers.Add(data);
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
    }
}

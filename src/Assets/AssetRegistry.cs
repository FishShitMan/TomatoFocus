using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using TomatoFocus.Core;

namespace TomatoFocus.Assets
{
    /// <summary>
    /// 图片接口：assets/ 目录下的 PNG / JPG / ICO 可逐个替换程序化绘制。
    /// 缺失或加载失败时自动回退到代码绘制，因此没有资源文件也能完整运行。
    /// 支持热重载（保存文件后无需重启）。
    /// </summary>
    internal sealed class AssetRegistry
    {
        /// <summary>预留的槽位（可在 assets/manifest.json 中声明，或直接放同名文件）。</summary>
        public static readonly string[] KnownSlots =
        {
            "icon.tomato", "icon.leaf", "icon.tray",
            "icon.btn.start", "icon.btn.pause", "icon.btn.stop", "icon.btn.reset",
            "icon.nav.prev", "icon.nav.next", "icon.settings", "icon.achievements", "icon.rewards",
            "icon.calendar", "icon.close", "icon.minimize",
            "medal.bronze", "medal.silver", "medal.gold", "medal.rainbow",
            "title.badge", "bg.texture", "art.empty"
        };

        private static AssetRegistry _default;
        public static AssetRegistry Default
        {
            get
            {
                if (_default == null) _default = Load();
                return _default;
            }
        }

        private readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher _watcher;
        private volatile bool _dirty;

        public int SlotCount { get { return KnownSlots.Length; } }
        public string Directory { get; private set; }

        public static AssetRegistry Load()
        {
            var reg = new AssetRegistry();
            reg.Directory = AppPaths.AssetsDir;
            reg.ReadManifest();
            reg.StartWatch();
            return reg;
        }

        private void ReadManifest()
        {
            _declared.Clear();
            try
            {
                string manifest = Path.Combine(Directory, "manifest.json");
                if (File.Exists(manifest))
                {
                    var root = JsonObj.Parse(File.ReadAllText(manifest));
                    var slots = root.Obj("slots");
                    foreach (string key in slots.Keys())
                    {
                        string file = slots.Obj(key).Str("file");
                        if (!string.IsNullOrEmpty(file)) _declared[key] = file;
                    }
                }
            }
            catch (Exception ex) { Log.Error("AssetRegistry.ReadManifest", ex); }
        }

        private void StartWatch()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return;
                _watcher = new FileSystemWatcher(Directory);
                _watcher.IncludeSubdirectories = false;
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                FileSystemEventHandler onChange = delegate { _dirty = true; };
                _watcher.Created += onChange;
                _watcher.Changed += onChange;
                _watcher.Deleted += onChange;
                _watcher.Renamed += delegate { _dirty = true; };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Error("AssetRegistry.StartWatch", ex); }
        }

        /// <summary>由渲染循环定期调用；资源变化时清空缓存。</summary>
        public bool Pump()
        {
            if (!_dirty) return false;
            _dirty = false;
            ClearCache();
            ReadManifest();
            return true;
        }

        private void ClearCache()
        {
            foreach (var b in _cache.Values) { try { b.Dispose(); } catch { } }
            _cache.Clear();
        }

        /// <summary>取槽位图片；没有则返回 null（调用方回退程序化绘制）。</summary>
        public Bitmap Get(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return null;
            Pump();
            Bitmap cached;
            if (_cache.TryGetValue(slot, out cached)) return cached;

            string file = null;
            if (_declared.TryGetValue(slot, out file) && !string.IsNullOrEmpty(file))
            {
                string p = Path.IsPathRooted(file) ? file : Path.Combine(Directory, file);
                if (File.Exists(p)) file = p; else file = null;
            }
            if (file == null)
            {
                // 约定：assets/<slot>.png 直接生效
                foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".ico", ".bmp" })
                {
                    string p = Path.Combine(Directory, slot + ext);
                    if (File.Exists(p)) { file = p; break; }
                }
            }

            Bitmap bmp = null;
            if (file != null)
            {
                try
                {
                    using (var img = Image.FromFile(file))
                        bmp = new Bitmap(img);
                }
                catch (Exception ex)
                {
                    Log.Error("AssetRegistry.Get(" + slot + ")", ex);
                    bmp = null;
                }
            }
            _cache[slot] = bmp;
            return bmp;
        }

        public void Dispose()
        {
            try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } } catch { }
            ClearCache();
        }
    }
}

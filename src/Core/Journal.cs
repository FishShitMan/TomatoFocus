using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 崩溃恢复用的"计时签到簿"：固定 1 KB（2 槽 × 512 B）的 <c>session.journal</c>。
    ///
    /// 为什么不用 data.json：
    ///   ① data.json 会随会话记录增长，为了记"这一轮还剩几秒"去重写整个历史数据不划算；
    ///   ② 这里是每秒一次的写入，必须足够小、足够便宜；
    ///   ③ 它只是"临时存档"，**从不参与**日历、笔记、钱包的存取，删掉它不会影响任何历史数据。
    ///
    /// 抗撕裂：两槽交替写，每槽带序号与校验和。读的时候取"校验通过且序号最大"的那一槽，
    /// 于是"刚写一半就断电"最坏只会丢掉最后一次写入，而不会读出一个半截文件。
    /// </summary>
    internal static class Journal
    {
        /// <summary>单槽字节数。</summary>
        public const int SlotSize = 512;
        /// <summary>槽数（交替写）。</summary>
        public const int SlotCount = 2;
        /// <summary>整份存档的固定大小：1 KB。</summary>
        public const int FileBytes = SlotSize * SlotCount;

        private const uint Magic = 0x314A4654;   // "TFJ1"
        private const int Version = 1;
        private const int HeaderSize = 18;       // magic(4) + version(2) + seq(4) + len(4) + hash(4)

        /// <summary>一次计时签到快照。字段刻意保持极少，整条记录约 120 B。</summary>
        public sealed class Snapshot
        {
            /// <summary>focusing / paused / break / pausedBreak。</summary>
            public string Phase = "";
            public int PlannedSec;
            public int BreakSec;
            public int ElapsedSec;
            public string Preset = "25";
            public DateTime StartedLocal = DateTime.MinValue;
            public int TaskRound;
            public DateTime SavedLocal = DateTime.MinValue;

            /// <summary>写入序号（读回时用来在两槽之间取新）。</summary>
            public uint Seq;
        }

        public static string FilePath { get { return Path.Combine(AppPaths.DataDir, "session.journal"); } }

        private static int _seq = -1;   // -1 = 本进程还没写过，需要先从文件里继承序号

        /// <summary>写一次快照。失败只返回 false，绝不抛异常影响计时。</summary>
        public static bool Write(Snapshot s)
        {
            if (s == null) return false;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                s.SavedLocal = DateTime.Now;

                byte[] text = Encoding.UTF8.GetBytes(Encode(s));
                if (text.Length > SlotSize - HeaderSize) return false;

                if (_seq < 0)
                {
                    // 继承文件里已有的最大序号：否则重启后序号从 1 重来，
                    // 读回时反而会选中上一轮留下的旧槽。
                    var prev = Read();
                    _seq = prev == null ? 0 : (int)prev.Seq;
                }
                _seq++;
                uint seq = (uint)_seq;
                int slot = (int)(seq % SlotCount);

                byte[] buf = new byte[SlotSize];
                PutU32(buf, 0, Magic);
                PutU16(buf, 4, Version);
                PutU32(buf, 6, seq);
                PutU32(buf, 10, (uint)text.Length);
                PutU32(buf, 14, Hash(text));
                Buffer.BlockCopy(text, 0, buf, HeaderSize, text.Length);

                using (var fs = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                {
                    if (fs.Length != FileBytes) fs.SetLength(FileBytes);
                    fs.Position = (long)slot * SlotSize;
                    fs.Write(buf, 0, SlotSize);
                    fs.Flush(false);   // 交给系统缓存：进程崩溃/被杀不会丢，断电靠双槽 + 校验兜底
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>读回最近一次有效快照；文件不存在、两槽都坏、版本不符时返回 null。</summary>
        public static Snapshot Read()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                byte[] all = File.ReadAllBytes(FilePath);
                Snapshot best = null;
                for (int i = 0; i < SlotCount; i++)
                {
                    var s = ParseSlot(all, i * SlotSize);
                    if (s == null) continue;
                    if (best == null || s.Seq > best.Seq) best = s;
                }
                return best;
            }
            catch (Exception) { return null; }
        }

        /// <summary>删除存档（完成 / 停止 / 过期时调用）。</summary>
        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch (Exception) { }
            _seq = -1;
        }

        public static bool Exists
        {
            get { try { return File.Exists(FilePath); } catch (Exception) { return false; } }
        }

        // --- 编解码 --------------------------------------------------------
        private static string Encode(Snapshot s)
        {
            var sb = new StringBuilder(160);
            sb.Append("phase=").Append(s.Phase ?? "").Append('\n');
            sb.Append("planned=").Append(s.PlannedSec).Append('\n');
            sb.Append("break=").Append(s.BreakSec).Append('\n');
            sb.Append("elapsed=").Append(s.ElapsedSec).Append('\n');
            sb.Append("preset=").Append(s.Preset ?? "25").Append('\n');
            sb.Append("started=").Append(s.StartedLocal.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("round=").Append(s.TaskRound).Append('\n');
            sb.Append("saved=").Append(s.SavedLocal.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        private static Snapshot ParseSlot(byte[] all, int offset)
        {
            try
            {
                if (all.Length < offset + SlotSize) return null;
                if (GetU32(all, offset) != Magic) return null;
                if (GetU16(all, offset + 4) != Version) return null;
                uint seq = GetU32(all, offset + 6);
                uint len = GetU32(all, offset + 10);
                if (len == 0 || len > SlotSize - HeaderSize) return null;
                uint hash = GetU32(all, offset + 14);

                byte[] text = new byte[len];
                Buffer.BlockCopy(all, offset + HeaderSize, text, 0, (int)len);
                if (Hash(text) != hash) return null;

                var s = new Snapshot();
                s.Seq = seq;
                foreach (string line in Encoding.UTF8.GetString(text).Split('\n'))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq);
                    string v = line.Substring(eq + 1);
                    switch (k)
                    {
                        case "phase": s.Phase = v; break;
                        case "planned": int.TryParse(v, out s.PlannedSec); break;
                        case "break": int.TryParse(v, out s.BreakSec); break;
                        case "elapsed": int.TryParse(v, out s.ElapsedSec); break;
                        case "preset": s.Preset = v; break;
                        case "started": s.StartedLocal = ParseDate(v); break;
                        case "round": int.TryParse(v, out s.TaskRound); break;
                        case "saved": s.SavedLocal = ParseDate(v); break;
                    }
                }
                if (string.IsNullOrEmpty(s.Phase)) return null;
                return s;
            }
            catch (Exception) { return null; }
        }

        private static DateTime ParseDate(string v)
        {
            DateTime d;
            if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d)) return d;
            return DateTime.MinValue;
        }

        // --- 小工具 --------------------------------------------------------
        private static uint Hash(byte[] data)
        {
            uint h = 2166136261;                       // FNV-1a 32：够快，够查错
            for (int i = 0; i < data.Length; i++) { h ^= data[i]; h *= 16777619; }
            return h;
        }

        private static void PutU32(byte[] b, int i, uint v)
        {
            b[i] = (byte)(v & 0xFF); b[i + 1] = (byte)((v >> 8) & 0xFF);
            b[i + 2] = (byte)((v >> 16) & 0xFF); b[i + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static void PutU16(byte[] b, int i, int v)
        {
            b[i] = (byte)(v & 0xFF); b[i + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static uint GetU32(byte[] b, int i)
        {
            return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
        }

        private static int GetU16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
    }
}

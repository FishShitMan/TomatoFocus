using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 程序化音效：实时合成 PCM 波形并封装成内存 WAV 播放，不依赖任何音频文件。
    /// 直接用 winmm 的 PlaySound 异步播放（SND_MEMORY | SND_ASYNC）：
    /// 系统保证同一时刻只有一段声音，**新的一次播放会立刻打断上一段**，
    /// 因此快速切换试听时不会排队、不会叠音；同一音色在 200ms 内的重复请求会被合并。
    /// </summary>
    internal static class Sound
    {
        private const int SampleRate = 22050;
        private const double Gain = 9000.0;

        /// <summary>最近一次被请求的音色 id（供测试与自检观察）。</summary>
        public static string LastPlayedId;

        /// <summary>实际入队播放的次数（被去重合并的请求不计入）。</summary>
        public static int PlayCount;

        private static readonly List<string> RecentIds = new List<string>();

        // --- 去重 ---------------------------------------------------------
        private const double DedupeSeconds = 0.20;
        private static string _lastReqId = "";
        private static DateTime _lastReqUtc = DateTime.MinValue;

        // --- 音色定义 -----------------------------------------------------

        /// <summary>一个音符：频率与包络，以及可选的滑音 / FM / 颤音 / 噪声瞬态。</summary>
        private sealed class Note
        {
            public double Freq;
            public double GlideTo;          // > 0 时从 Freq 线性滑到 GlideTo
            public double StartMs;
            public double DurMs;
            public double Amp = 1.0;
            public double AttackMs = 4.0;
            public double Decay = 7.5;      // 指数衰减系数（越大越短促）
            public int Partials = 3;        // 叠加的谐波数量（1 = 纯正弦）
            public double FmRatio;          // FM 调制比（> 0 时启用）→ 铃音金属感
            public double FmIndex;
            public double VibHz;            // 颤音频率（> 0 时启用）
            public double VibDepth;
            public double Noise;            // 噪声瞬态占比 0..1 → 叩击/水声感
        }

        /// <summary>一个音色 = 若干音符 + 尾部留白。</summary>
        private sealed class Clip
        {
            public Note[] Notes = new Note[0];
            public double TailMs = 120;
        }

        private static Note N(double freq, double startMs, double durMs, double amp, double decay, int partials, double attackMs)
        {
            var n = new Note();
            n.Freq = freq; n.StartMs = startMs; n.DurMs = durMs; n.Amp = amp;
            n.Decay = decay; n.Partials = partials; n.AttackMs = attackMs;
            return n;
        }

        /// <summary>
        /// 音色库。五个可装备提示音刻意在「音高走向 / 时长 / 泛音亮度 / 起音」上彼此拉开，
        /// 保证闭眼也能分辨：默认=中性双音、清脆=长铃音、轻柔=低吟、水滴=下滑音、木叩=无音高瞬态。
        /// </summary>
        private static Dictionary<string, Clip> BuildBank()
        {
            var b = new Dictionary<string, Clip>(StringComparer.Ordinal);

            // 默认：温和双音 C5→G5，起音柔和、泛音少，最中性
            b["default"] = new Clip
            {
                TailMs = 120,
                Notes = new[]
                {
                    N(523.25,   0, 220, 0.55, 6.5, 2, 9),
                    N(783.99, 150, 300, 0.50, 6.0, 2, 9)
                }
            };

            // 清脆：三音上行 A5-D6-G6 + FM 铃音泛音 + 长衰减，明显更亮更长
            var c1 = N(880.00, 0, 700, 0.50, 2.2, 2, 2); c1.FmRatio = 3.5; c1.FmIndex = 1.35;
            var c2 = N(1174.66, 140, 760, 0.42, 2.0, 2, 2); c2.FmRatio = 3.5; c2.FmIndex = 1.20;
            var c3 = N(1567.98, 280, 820, 0.36, 1.8, 2, 2); c3.FmRatio = 3.5; c3.FmIndex = 1.05;
            b["chime"] = new Clip { TailMs = 240, Notes = new[] { c1, c2, c3 } };

            // 轻柔：低音下行、慢起音、带轻微颤音，整体音量最小
            var s1 = N(659.25, 0, 380, 0.30, 3.2, 1, 70); s1.VibHz = 5.0; s1.VibDepth = 0.004;
            var s2 = N(523.25, 300, 480, 0.28, 3.0, 1, 70); s2.VibHz = 5.0; s2.VibDepth = 0.004;
            b["soft"] = new Clip { TailMs = 120, Notes = new[] { s1, s2 } };

            // 水滴：单音快速下滑 + 一点水声噪声
            var d1 = N(1600, 0, 190, 0.60, 15.0, 2, 1); d1.GlideTo = 720; d1.Noise = 0.05;
            b["drop"] = new Clip { TailMs = 120, Notes = new[] { d1 } };

            // 木叩：极短、低音、强噪声瞬态，几乎没有音高感
            var w1 = N(220, 0, 110, 0.60, 42, 5, 0.5); w1.Noise = 0.30;
            var w2 = N(160, 0, 140, 0.35, 30, 3, 0.8); w2.Noise = 0.18;
            b["wood"] = new Clip { TailMs = 120, Notes = new[] { w1, w2 } };

            // 成就：四音上行琶音 + 末尾闪音，最亮、最有仪式感
            var u4 = N(2093.00, 330, 620, 0.44, 2.6, 2, 3); u4.FmRatio = 2.0; u4.FmIndex = 0.9;
            b["unlock"] = new Clip
            {
                TailMs = 140,
                Notes = new[]
                {
                    N(1046.50,   0, 260, 0.42, 5.0, 2, 4),
                    N(1318.51, 110, 280, 0.40, 4.8, 2, 4),
                    N(1567.98, 220, 300, 0.38, 4.6, 2, 4),
                    u4
                }
            };

            // 休息结束、回到专注：温和上行三音
            b["start"] = new Clip
            {
                TailMs = 120,
                Notes = new[]
                {
                    N(523.25,   0, 180, 0.50, 6.0, 2, 6),
                    N(659.25, 120, 200, 0.48, 5.4, 2, 6),
                    N(880.00, 240, 420, 0.45, 3.4, 2, 6)
                }
            };

            // 一般休息提示：下行两音
            b["rest"] = new Clip
            {
                TailMs = 120,
                Notes = new[]
                {
                    N(783.99,   0, 220, 0.45, 5.0, 2, 6),
                    N(587.33, 180, 380, 0.42, 3.6, 2, 6)
                }
            };

            return b;
        }

        private static readonly Dictionary<string, Clip> Bank = BuildBank();

        /// <summary>全部音色 id（供测试枚举）。</summary>
        public static string[] Ids
        {
            get
            {
                var list = new List<string>(Bank.Keys);
                list.Sort(StringComparer.Ordinal);
                return list.ToArray();
            }
        }

        // --- 播放 ---------------------------------------------------------

        /// <summary>请求播放一个音色；enabled 为 false 时完全静默。</summary>
        public static void Play(string id, bool enabled)
        {
            if (!enabled || string.IsNullOrEmpty(id)) return;

            // 请求本身始终记账（LastPlayedId 表示"最后一次请求的音色"），
            // 只对极短时间内的同一音色做合并，避免同一次事件被触发两遍而听成叠音。
            LastPlayedId = id;

            lock (Lock)
            {
                DateTime now = DateTime.UtcNow;
                if (id == _lastReqId && (now - _lastReqUtc).TotalSeconds < DedupeSeconds) return;
                _lastReqId = id;
                _lastReqUtc = now;

                PlayCount++;
                RecentIds.Add(id);
                if (RecentIds.Count > 32) RecentIds.RemoveAt(0);

                try { StartPlayback(BuildWav(PcmOf(id))); }
                catch (Exception) { }
            }
        }

        /// <summary>最近若干次实际播放的音色（供测试断言"一次结算只响一次"）。</summary>
        public static string[] Recent()
        {
            lock (Lock) return RecentIds.ToArray();
        }

        /// <summary>清空播放记录（测试用）。</summary>
        public static void ClearRecent()
        {
            lock (Lock)
            {
                RecentIds.Clear();
                PlayCount = 0;
                LastPlayedId = null;
                _lastReqId = "";
                _lastReqUtc = DateTime.MinValue;
            }
        }

        private static readonly object Lock = new object();

        // --- 播放（winmm）--------------------------------------------------
        private const uint SND_ASYNC = 0x0001;
        private const uint SND_NODEFAULT = 0x0002;
        private const uint SND_MEMORY = 0x0004;

        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
        private static extern bool PlaySound(IntPtr data, IntPtr hModule, uint flags);

        private static GCHandle _playingHandle;
        private static byte[] _playingData;

        /// <summary>
        /// 播放一段 WAV。PlaySound 的语义就是"同一时刻只有一段声音"：
        /// 本次调用会立即中断上一段（这正是快速切换试听时想要的效果）。
        /// 缓冲区必须固定住——系统在异步播放期间会持续读取它。
        /// </summary>
        private static void StartPlayback(byte[] wav)
        {
            var old = _playingHandle;
            _playingData = wav;                                  // 保持托管引用
            _playingHandle = GCHandle.Alloc(wav, GCHandleType.Pinned);
            try
            {
                PlaySound(_playingHandle.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
            }
            catch (Exception) { }
            if (old.IsAllocated)
            {
                try { old.Free(); } catch (Exception) { }        // 旧声音已被打断，可以安全释放
            }
        }

        /// <summary>停止当前播放（退出时用）。</summary>
        public static void StopAll()
        {
            lock (Lock)
            {
                try
                {
                    if (_playingHandle.IsAllocated) { _playingHandle.Free(); }
                    _playingHandle = default(GCHandle);
                    _playingData = null;
                    PlaySound(IntPtr.Zero, IntPtr.Zero, 0);      // NULL = 停止
                }
                catch (Exception) { }
            }
        }

        // --- 合成 ---------------------------------------------------------

        /// <summary>合成指定音色的 PCM（供测试比对波形差异）。</summary>
        public static short[] PcmOf(string id)
        {
            Clip clip;
            if (id == null || !Bank.TryGetValue(id, out clip)) clip = Bank["default"];
            return BuildPcm(clip);
        }

        /// <summary>生成指定音色的 WAV 字节（供测试校验文件头与长度自洽）。</summary>
        public static byte[] WavOf(string id)
        {
            return BuildWav(PcmOf(id));
        }

        private static short[] BuildPcm(Clip clip)
        {
            double endMs = 0;
            foreach (var n in clip.Notes) endMs = Math.Max(endMs, n.StartMs + n.DurMs);
            int total = (int)(SampleRate * (endMs + clip.TailMs) / 1000.0);
            if (total <= 0) total = 1;

            var acc = new double[total];
            var rnd = new Random(20260910);        // 固定种子：噪声可复现，便于测试比对

            foreach (var n in clip.Notes)
            {
                int i0 = (int)(SampleRate * n.StartMs / 1000.0);
                int len = (int)(SampleRate * n.DurMs / 1000.0);
                for (int i = 0; i < len; i++)
                {
                    int idx = i0 + i;
                    if (idx < 0 || idx >= total) continue;

                    double t = i / (double)SampleRate;
                    double u = i / (double)Math.Max(1, len);          // 0..1 归一化进度
                    double freq = n.GlideTo > 0 ? n.Freq + (n.GlideTo - n.Freq) * u : n.Freq;
                    if (n.VibHz > 0) freq *= 1.0 + n.VibDepth * Math.Sin(2 * Math.PI * n.VibHz * t);

                    double phase = 2 * Math.PI * freq * t;
                    double v;
                    if (n.FmRatio > 0)
                    {
                        // FM：调制指数随时间衰减，得到"叮"的金属泛音
                        double mod = n.FmIndex * Math.Exp(-t * 6.0) * Math.Sin(phase * n.FmRatio);
                        v = Math.Sin(phase + mod) + 0.35 * Math.Sin(2 * phase + mod);
                    }
                    else
                    {
                        v = Math.Sin(phase);
                        for (int h = 2; h <= n.Partials; h++) v += Math.Sin(phase * h) * (0.5 / h);
                    }
                    if (n.Noise > 0)
                        v = v * (1 - n.Noise) + (rnd.NextDouble() * 2 - 1) * n.Noise;

                    double attack = n.AttackMs <= 0 ? 1.0 : Math.Min(1.0, t * 1000.0 / n.AttackMs);
                    double release = Math.Min(1.0, (1.0 - u) / 0.18);  // 收尾淡出，杜绝爆音
                    if (release < 0) release = 0;
                    acc[idx] += v * Math.Exp(-t * n.Decay) * attack * release * n.Amp;
                }
            }

            double peak = 0;
            foreach (double v in acc) { double a = Math.Abs(v); if (a > peak) peak = a; }
            double norm = peak > 0.95 ? 0.95 / peak : 1.0;    // 只做防削波，保留音色间的音量差异

            var pcm = new short[total];
            for (int i = 0; i < total; i++)
            {
                double v = acc[i] * norm * Gain;
                if (v > 32000) v = 32000;
                if (v < -32000) v = -32000;
                pcm[i] = (short)v;
            }
            return pcm;
        }

        private static byte[] BuildWav(short[] pcm)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int dataBytes = pcm.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { 'W', 'A', 'V', 'E' });
                w.Write(new[] { 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);                 // PCM
                w.Write((short)1);                 // mono
                w.Write(SampleRate);
                w.Write(SampleRate * 2);           // byte rate
                w.Write((short)2);                 // block align
                w.Write((short)16);                // bits
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);
                // 整块拷贝，避免逐个采样调用 BinaryWriter（一次播音能省下 1~2ms 的 UI 线程开销）
                var pcmBytes = new byte[dataBytes];
                Buffer.BlockCopy(pcm, 0, pcmBytes, 0, dataBytes);
                w.Write(pcmBytes);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}

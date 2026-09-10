using System;
using System.Collections.Generic;
using System.IO;
using System.Media;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 程序化音效：实时合成 PCM 波形并封装成内存 WAV 播放，不依赖任何音频文件。
    /// </summary>
    internal static class Sound
    {
        private const int SampleRate = 22050;

        /// <summary>最近一次触发的音色（供测试与自检观察）。</summary>
        public static string LastPlayedId;

        private static readonly Dictionary<string, double[]> Bank = new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            { "chime", new[] { 880.0, 1174.66, 1567.98 } },   // A5 D6 G6
            { "soft",  new[] { 659.25, 783.99 } },            // E5 G5
            { "start", new[] { 523.25, 659.25, 880.0 } },     // C5 E5 A5
            { "rest",  new[] { 783.99, 587.33 } }             // G5 D5
        };

        public static void Play(string id, bool enabled)
        {
            if (!enabled) return;
            LastPlayedId = id;
            try
            {
                double[] notes;
                if (!Bank.TryGetValue(id, out notes)) notes = Bank["chime"];
                byte[] wav = BuildWav(notes);
                // 在后台线程同步播放，保证音效完整且不阻塞界面
                var thread = new System.Threading.Thread(delegate ()
                {
                    try
                    {
                        using (var ms = new MemoryStream(wav))
                        using (var player = new SoundPlayer(ms))
                            player.PlaySync();
                    }
                    catch { }
                });
                thread.IsBackground = true;
                thread.Start();
            }
            catch { /* 音频设备不可用时静默忽略 */ }
        }

        private static byte[] BuildWav(double[] notes)
        {
            double noteSec = 0.13;
            int noteSamples = (int)(SampleRate * noteSec);
            int total = noteSamples * notes.Length + SampleRate / 4;
            var pcm = new short[total];

            for (int n = 0; n < notes.Length; n++)
            {
                double freq = notes[n];
                for (int i = 0; i < noteSamples; i++)
                {
                    double t = i / (double)SampleRate;
                    double env = Math.Exp(-t * 7.5);                       // 指数衰减
                    double attack = Math.Min(1.0, i / (SampleRate * 0.004)); // 4ms 起音
                    double v = Math.Sin(2 * Math.PI * freq * t) * 0.55
                             + Math.Sin(2 * Math.PI * freq * 2 * t) * 0.14
                             + Math.Sin(2 * Math.PI * freq * 3 * t) * 0.06;
                    int idx = n * noteSamples + i;
                    if (idx < total) pcm[idx] = (short)(v * env * attack * 12000);
                }
            }

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
                foreach (short s in pcm) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}

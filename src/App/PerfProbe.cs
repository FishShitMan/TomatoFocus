using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using TomatoFocus.Core;

namespace TomatoFocus.App
{
    /// <summary>
    /// 渲染性能自检（命令行 <c>--perf 秒数</c>）。
    /// 依次跑三段真实场景——空闲 / 动画（60fps 档）/ 计时中（20fps 档）——
    /// 统计每段的真实帧间隔分布、单帧绘制耗时与进程 CPU 占比。
    /// 报告写入数据目录下的 perf.txt（可用 --perf-out 指定），因为主程序是 winexe、没有控制台。
    /// </summary>
    internal sealed class PerfProbe
    {
        /// <summary>当前探针（null 表示未启用）。</summary>
        public static PerfProbe Active;

        /// <summary>报告输出路径（由命令行 --perf-out 指定，默认数据目录下的 perf.txt）。</summary>
        public static string OutputPath;

        /// <summary>诊断计数：渲染泵投递次数 / 帧消息处理次数。</summary>
        public static int Posts;
        public static int Frames;

        /// <summary>诊断：泵循环轮数、累计睡眠毫秒、单次最大睡眠毫秒。</summary>
        public static int PumpIters;
        public static double PumpSleepMs;
        public static int PumpMaxSleepMs;

        public static void NoteSleep(int ms)
        {
            PumpIters++;
            PumpSleepMs += ms;
            if (ms > PumpMaxSleepMs) PumpMaxSleepMs = ms;
        }

        private static readonly string[] PhaseNames = { "空闲(250ms档)", "动画(60fps档)", "计时(20fps档)" };

        private readonly double _phaseSeconds;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<double>[] _intervals = { new List<double>(), new List<double>(), new List<double>() };
        private readonly List<double>[] _draws = { new List<double>(), new List<double>(), new List<double>() };
        private readonly double[] _cpuPercent = new double[3];
        private readonly string[] _sections = new string[3];

        private int _phase;
        private double _phaseStart;
        private double _lastPaint = -1;
        private bool _started;
        private TimeSpan _phaseCpuStart;
        private double _phaseWallStart;

        /// <summary>上一帧长动画的触发时刻（探针自己按节奏补触发，保证该阶段一直在动）。</summary>
        public double LastCelebrate { get; set; }

        public PerfProbe(double seconds)
        {
            _phaseSeconds = Math.Max(0.8, seconds / 3.0);
            LastCelebrate = -99;
            _phaseCpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            _phaseWallStart = 0;
            PerfCounters.Enabled = true;
            PerfCounters.Reset();
        }

        public int Phase { get { return _phase; } }
        public bool Finished { get { return _phase >= 3; } }
        public double PhaseSeconds { get { return _phaseSeconds; } }

        /// <summary>最近一次使用的帧间隔（诊断用）。</summary>
        public int LastTargetMs { get { return _lastTargetMs; } }
        private int _lastTargetMs;

        public void NoteTarget(int ms) { _lastTargetMs = ms; }

        /// <summary>每帧由渲染循环调用；跨阶段时回调 onPhase（1=动画段 2=计时段）。</summary>
        public void Tick(Action<int> onPhase)
        {
            if (_phase >= 3) return;
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _phaseStart >= _phaseSeconds)
            {
                ClosePhase(now);
                _phase++;
                if (_phase < 3 && onPhase != null) onPhase(_phase);
            }
        }

        private void ClosePhase(double now)
        {
            if (_phase >= 3) return;
            if (!_started) return;
            double wall = now - _phaseWallStart;
            if (wall > 0.05)
            {
                var cpu = Process.GetCurrentProcess().TotalProcessorTime - _phaseCpuStart;
                _cpuPercent[_phase] = cpu.TotalMilliseconds / (wall * 1000.0) * 100.0;
            }
            _phaseStart = now;
            _phaseWallStart = now;
            _phaseCpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            _lastPaint = -1;
            _sections[_phase] = PerfCounters.Dump();
            PerfCounters.Reset();
        }

        /// <summary>由 OnPaint 调用：记录一帧的绘制耗时（不含最终位图 blit）。</summary>
        public void RecordPaint(double ms)
        {
            if (_phase >= 3) return;
            if (!_started)
            {
                // 第一帧到达才开始计时：跳过窗口初始化与首帧预热
                _started = true;
                _clock.Restart();
                _phaseStart = 0;
                _phaseWallStart = 0;
                _phaseCpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            }
            double now = _clock.Elapsed.TotalSeconds;
            if (_lastPaint >= 0) _intervals[_phase].Add((now - _lastPaint) * 1000.0);
            _lastPaint = now;
            _draws[_phase].Add(ms);
            PerfCounters.AddFrame(ms);
        }

        /// <summary>把统计写入文件；同时尝试写到控制台（若被附着）。</summary>
        public void WriteReport(string path)
        {
            ClosePhase(_clock.Elapsed.TotalSeconds);
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("=== 渲染性能自检（每段 " + _phaseSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " 秒）===");
            sb.AppendLine("泵投递=" + Posts + " 帧处理=" + Frames + " 目标间隔=" + _lastTargetMs + "ms");
            sb.AppendLine("泵循环=" + PumpIters + " 轮 累计睡眠=" + PumpSleepMs.ToString("0") + "ms 单次最大睡眠=" + PumpMaxSleepMs + "ms");
            sb.AppendLine();
            for (int i = 0; i < 3; i++)
            {
                var iv = _intervals[i];
                var dr = _draws[i];
                if (iv.Count == 0) { sb.AppendLine("[" + PhaseNames[i] + "] 未采到帧"); continue; }

                var sorted = new List<double>(iv);
                sorted.Sort();
                double avg = 0; foreach (double v in iv) avg += v; avg /= iv.Count;
                double p95 = sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.95))];
                double max = sorted[sorted.Count - 1];

                double dAvg = 0, dMax = 0;
                foreach (double v in dr) { dAvg += v; if (v > dMax) dMax = v; }
                dAvg /= dr.Count;

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "[{0}] 帧数={1} 平均帧间隔={2:0.0}ms p95={3:0.0}ms 最大={4:0.0}ms | 绘制 平均={5:0.00}ms 最大={6:0.00}ms | 等效fps={7:0.0} | CPU={8:0.00}%",
                    PhaseNames[i], iv.Count, avg, p95, max, dAvg, dMax, 1000.0 / Math.Max(0.001, avg), _cpuPercent[i]));
                if (!string.IsNullOrEmpty(_sections[i]))
                    sb.Append(_sections[i]);
            }
            sb.AppendLine();

            string text = sb.ToString();
            try { File.WriteAllText(path, text, new UTF8Encoding(false)); } catch { }
            try { Console.Write(text); } catch { }
            try { Log.Info("perf report -> " + path + text.Replace("\r", "").Replace("\n", " | ")); } catch { }
        }
    }
}

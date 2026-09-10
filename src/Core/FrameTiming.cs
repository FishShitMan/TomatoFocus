using System;

namespace TomatoFocus.Core
{
    /// <summary>
    /// 帧时间策略。空闲时为了省电会把 UI 定时器降到 250ms，
    /// 但若把 250ms 直接喂给缓动函数，动画会在第一帧就跳完（表现为"动效丢失"）。
    /// 因此动画步进必须钳制。
    /// </summary>
    internal static class FrameTiming
    {
        /// <summary>动画单帧最大推进量（1/30 秒）。</summary>
        public const double MaxAnimationStep = 1.0 / 30.0;

        /// <summary>把真实帧间隔钳制成动画步进。</summary>
        public static double ClampAnimationDelta(double realDt)
        {
            if (realDt <= 0 || double.IsNaN(realDt)) return 1.0 / 120.0;
            return realDt > MaxAnimationStep ? MaxAnimationStep : realDt;
        }

        /// <summary>交互后的高帧率持续时间（秒）。</summary>
        public const double InteractionBurstSeconds = 1.2;

        // --- 帧档位（毫秒）-------------------------------------------------
        /// <summary>突发动效：悬停 / 按压 / 提示 / 粒子 / 完成动效。</summary>
        public const int BurstMs = 16;
        /// <summary>常驻动效（勋章扫光 / 配色循环）。走"勋章快路径"后单帧只有约 4ms，30fps 很轻松。</summary>
        public const int SlowMs = 33;
        /// <summary>无操作多久之后把常驻动效降档（秒）。</summary>
        public const double AmbientIdleAfterSeconds = 10.0;
        /// <summary>降档后的常驻帧间隔（约 6fps）：没人看的时候不烧 CPU。</summary>
        public const int AmbientIdleMs = 160;

        /// <summary>按"距上次输入操作的秒数"取常驻档间隔：活跃 30fps，空闲约 6fps。</summary>
        public static int AmbientMs(double idleSeconds)
        {
            return idleSeconds < AmbientIdleAfterSeconds ? SlowMs : AmbientIdleMs;
        }
        /// <summary>计时进行中：进度环与时钟每秒才变一次。</summary>
        public const int RunningMs = 50;
        /// <summary>完全空闲：没有重绘需求。</summary>
        public const int IdleMs = 250;
    }
}

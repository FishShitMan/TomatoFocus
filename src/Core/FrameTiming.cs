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
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;

namespace TomatoFocus.Render
{
    /// <summary>缓动函数。</summary>
    internal static class Ease
    {
        public static float Linear(float t) { return t; }

        public static float OutCubic(float t)
        {
            float u = 1 - t; return 1 - u * u * u;
        }

        public static float InOutCubic(float t)
        {
            return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;
        }

        public static float OutBack(float t)
        {
            const float c1 = 1.70158f, c3 = c1 + 1;
            float u = t - 1;
            return 1 + c3 * u * u * u + c1 * u * u;
        }

        public static float OutElastic(float t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            const float c4 = (float)(2 * Math.PI / 3);
            return (float)(Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * c4) + 1);
        }

        public static float OutQuad(float t) { return 1 - (1 - t) * (1 - t); }
    }

    /// <summary>指数逼近的动画值（帧率无关）。</summary>
    internal sealed class Anim
    {
        public float Value;
        public float Target;
        public float Speed;      // 越大越快，约 8~20
        public float Min;
        public float Max;

        public Anim(float initial = 0f, float speed = 12f, float min = float.MinValue, float max = float.MaxValue)
        {
            Value = initial;
            Target = initial;
            Speed = speed;
            Min = min;
            Max = max;
        }

        public bool Settled { get { return Math.Abs(Target - Value) < 0.0005f; } }

        public void Set(float target) { Target = Clamp(target); }
        public void Jump(float value) { Value = Clamp(value); Target = Value; }

        private float Clamp(float v)
        {
            if (v < Min) return Min;
            if (v > Max) return Max;
            return v;
        }

        public void Update(double dt)
        {
            if (Settled) { Value = Target; return; }
            float k = (float)(1 - Math.Exp(-Speed * dt));
            Value += (Target - Value) * k;
            if (Math.Abs(Target - Value) < 0.0005f) Value = Target;
        }
    }

    /// <summary>一次性播放的时间轴（0→1）。</summary>
    internal sealed class Timeline
    {
        private double _t;
        public double Duration = 0.5;
        public bool Playing;
        public bool Loop;
        public Func<float, float> Easing = Ease.OutCubic;

        public float Raw { get { return Duration <= 0 ? 1f : (float)Math.Min(1.0, _t / Duration); } }
        public float Value { get { return Easing(Raw); } }
        public bool Finished { get { return !Loop && Raw >= 1f; } }

        public void Play(double duration = -1)
        {
            if (duration > 0) Duration = duration;
            _t = 0;
            Playing = true;
        }

        public void Update(double dt)
        {
            if (!Playing) return;
            _t += dt;
            if (Loop)
            {
                if (Duration > 0 && _t > Duration) _t -= Duration;
            }
            else if (_t >= Duration)
            {
                _t = Duration;
                Playing = false;
            }
        }
    }

    /// <summary>粒子（用于完成时的种子/星火飞散）。</summary>
    internal sealed class Particle
    {
        public float X, Y, Vx, Vy, Life, MaxLife, Size, Rot, RotSpeed;
        public Color Color;
        public int Shape;      // 0 圆 1 种子(椭圆) 2 星
    }

    internal sealed class ParticleSystem
    {
        private readonly List<Particle> _items = new List<Particle>();
        private readonly Random _rng = new Random(20260214);

        public int Count { get { return _items.Count; } }
        public bool Active { get { return _items.Count > 0; } }
        public IList<Particle> Items { get { return _items; } }

        public void Clear() { _items.Clear(); }

        /// <summary>完成番茄时从中心喷发。</summary>
        public void Burst(float cx, float cy, Color accent, Color leaf, int count = 46)
        {
            for (int i = 0; i < count; i++)
            {
                double a = _rng.NextDouble() * Math.PI * 2;
                double sp = 90 + _rng.NextDouble() * 260;
                var p = new Particle();
                p.X = cx; p.Y = cy;
                p.Vx = (float)(Math.Cos(a) * sp);
                p.Vy = (float)(Math.Sin(a) * sp) - 60f;
                p.MaxLife = 0.45f + (float)_rng.NextDouble() * 0.6f;   // 缩短寿命，避免长时间残留
                p.Life = p.MaxLife;
                p.Size = 2.4f + (float)_rng.NextDouble() * 4.2f;
                p.Rot = (float)(_rng.NextDouble() * Math.PI);
                p.RotSpeed = (float)((_rng.NextDouble() - 0.5) * 8);
                p.Color = _rng.NextDouble() < 0.28 ? leaf : accent;
                p.Shape = _rng.NextDouble() < 0.45 ? 1 : 0;
                _items.Add(p);
            }
        }

        public void Confetti(float x, float y, float width, Color[] colors, int count = 40)
        {
            for (int i = 0; i < count; i++)
            {
                var p = new Particle();
                p.X = x + (float)_rng.NextDouble() * width;
                p.Y = y - (float)_rng.NextDouble() * 20f;
                p.Vx = (float)((_rng.NextDouble() - 0.5) * 90);
                p.Vy = 40 + (float)_rng.NextDouble() * 120;
                p.MaxLife = 1.6f + (float)_rng.NextDouble() * 1.2f;
                p.Life = p.MaxLife;
                p.Size = 3f + (float)_rng.NextDouble() * 3f;
                p.Rot = (float)(_rng.NextDouble() * Math.PI);
                p.RotSpeed = (float)((_rng.NextDouble() - 0.5) * 10);
                p.Color = colors[_rng.Next(colors.Length)];
                p.Shape = 2;
                _items.Add(p);
            }
        }

        public void Update(double dt)
        {
            float d = (float)dt;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var p = _items[i];
                p.Life -= d;
                if (p.Life <= 0) { _items.RemoveAt(i); continue; }
                p.Vy += 420f * d;              // 重力
                p.Vx *= (float)(1 - 1.6 * dt); // 阻尼
                p.X += p.Vx * d;
                p.Y += p.Vy * d;
                p.Rot += p.RotSpeed * d;
            }
        }

        public void Draw(Painter pt)
        {
            foreach (var p in _items)
            {
                float a = p.Life / p.MaxLife;
                if (a > 1) a = 1;
                // 后段快速渐隐，透明度归零后不再绘制
                float fade = a < 0.45f ? a / 0.45f : 1f;
                int alpha = (int)(255 * fade * fade * fade);
                if (alpha <= 2) continue;
                var col = Color.FromArgb(alpha, p.Color);
                if (p.Shape == 0)
                {
                    pt.FillCircle(new PointF(p.X, p.Y), p.Size * (0.4f + 0.6f * a), col);
                }
                else if (p.Shape == 1)
                {
                    var st = pt.Raw.Save();
                    pt.Raw.TranslateTransform(p.X, p.Y);
                    pt.Raw.RotateTransform(p.Rot * 57.29578f);
                    using (var b = new SolidBrush(col))
                        pt.Raw.FillEllipse(b, -p.Size, -p.Size * 0.6f, p.Size * 2f, p.Size * 1.2f);
                    pt.Raw.Restore(st);
                }
                else
                {
                    var st = pt.Raw.Save();
                    pt.Raw.TranslateTransform(p.X, p.Y);
                    pt.Raw.RotateTransform(p.Rot * 57.29578f);
                    using (var b = new SolidBrush(col))
                        pt.Raw.FillRectangle(b, -p.Size / 2, -p.Size / 4, p.Size, p.Size / 2);
                    pt.Raw.Restore(st);
                }
            }
        }
    }
}

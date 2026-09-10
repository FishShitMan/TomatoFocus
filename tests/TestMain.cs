using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using TomatoFocus.Core;
using TomatoFocus.Render;
using TomatoFocus.Render.Art;
using TomatoFocus.Tray;
using TomatoFocus.Ui;

namespace TomatoFocus.Tests
{
    /// <summary>极简测试运行器（无需任何测试框架）。</summary>
    internal static class TestMain
    {
        private static int _pass;
        private static int _fail;
        private static string _group = "";

        public static int Main(string[] args)
        {
            string temp = Path.Combine(Path.GetTempPath(), "TomatoFocus.Tests." + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            AppPaths.OverrideDataDir(temp);

            try
            {
                I18n.Load(I18n.DefaultLang);

                TestTomatoMathCompleted();
                TestTomatoMathAborted();
                TestFormatting();
                TestWalletFragmentRules();
                TestMergedWhole();
                TestStats();
                TestJson();
                TestStoreRoundTrip();
                TestAchievementsAndRewards();
                TestHealthRules();
                TestI18n();
                TestTextCentering();
                TestTomatoBounds();
                TestRingStability();
                TestHoverFeedback();
                TestRenderTomato();
                TestRenderUiSnapshot();
                TestTrayIcon();
                TestAssetRegistry();
                TestLangKeys();
                TestUiInteractions();
                TestDrawerAndLayout();
                TestFrameTiming();
                TestMinimalModeWiring();
                TestHideCountdown();
                TestPolishRound();
                TestDebugAndClearData();
                TestTopBarAndPreviews();
                TestCustomAndDefaults();
                TestNotesAndBadge();
                TestCalendarAndWallet();
                TestTopBarSpacing();
                TestTrayAndMode();
                TestModeTint();
                TestTextWrapping();
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  [异常] " + ex);
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine("通过 " + _pass + " / 失败 " + _fail);
            return _fail == 0 ? 0 : 1;
        }

        private static void Group(string name)
        {
            _group = name;
            Console.WriteLine("[ " + name + " ]");
        }

        private static void Eq(string name, object actual, object expected)
        {
            if (Equals(actual, expected)) { _pass++; return; }
            _fail++;
            Console.WriteLine("  × " + _group + " / " + name + "：期望 " + expected + "，实际 " + actual);
        }

        private static void True(string name, bool condition, string detail = "")
        {
            if (condition) { _pass++; return; }
            _fail++;
            Console.WriteLine("  × " + _group + " / " + name + (string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）"));
        }

        // --- 计量 ----------------------------------------------------------
        private static void TestTomatoMathCompleted()
        {
            Group("TomatoMath 正常完成");
            Eq("5 分钟", TomatoMath.TenthsForCompleted(5 * 60), 2);
            Eq("15 分钟", TomatoMath.TenthsForCompleted(15 * 60), 6);
            Eq("25 分钟", TomatoMath.TenthsForCompleted(25 * 60), 10);
            Eq("30 分钟（1 完整 + 0.2 碎片）", TomatoMath.TenthsForCompleted(30 * 60), 12);
            Eq("50 分钟", TomatoMath.TenthsForCompleted(50 * 60), 20);
            Eq("60 分钟", TomatoMath.TenthsForCompleted(60 * 60), 24);
            Eq("自定 12 分钟（0.48 向下取整）", TomatoMath.TenthsForCompleted(12 * 60), 4);
            Eq("自定 26 分钟（余 1 分不进位）", TomatoMath.TenthsForCompleted(26 * 60), 10);
            Eq("0 秒", TomatoMath.TenthsForCompleted(0), 0);
        }

        private static void TestTomatoMathAborted()
        {
            Group("TomatoMath 提前中断（半收益）");
            Eq("4:59 不计数", TomatoMath.TenthsForAborted(299), 0);
            Eq("5:00", TomatoMath.TenthsForAborted(300), 1);
            Eq("10:00", TomatoMath.TenthsForAborted(600), 2);
            Eq("12:30", TomatoMath.TenthsForAborted(750), 2);
            Eq("15:00", TomatoMath.TenthsForAborted(900), 3);
            Eq("20:00", TomatoMath.TenthsForAborted(1200), 4);
            Eq("24:59 仍是半收益", TomatoMath.TenthsForAborted(1499), 4);
            Eq("30:00", TomatoMath.TenthsForAborted(1800), 6);
            Eq("60:00", TomatoMath.TenthsForAborted(3600), 12);
            // 等价性：floor(floor(x/150)/2) == floor(x/300)
            for (int s = 0; s < 5000; s += 37)
                if ((TomatoMath.TenthsForCompleted(s) / 2) != TomatoMath.TenthsForAborted(s))
                {
                    True("半收益等价性 @" + s, false, "两种算法不一致");
                    return;
                }
            _pass++;
        }

        private static void TestFormatting()
        {
            Group("格式化");
            Eq("0 格", TomatoMath.Format(0), "0.0");
            Eq("12 格", TomatoMath.Format(12), "1.2");
            Eq("碎片", TomatoMath.FormatFragment(4), "0.4");
            Eq("时钟 12:34", TomatoMath.FormatClock(754), "12:34");
            Eq("时钟 1:00:00", TomatoMath.FormatClock(3600), "1:00:00");
        }

        private static void TestWalletFragmentRules()
        {
            Group("钱包：碎片不可兑换、攒满进位");
            var w = new Wallet();
            w.TotalTenths = 14;                     // 1.4 颗
            Eq("完整颗", w.UsableWhole, 1);
            Eq("碎片", w.FragmentTenths, 4);
            True("可以兑换 1 颗", w.CanAfford(1));
            True("不能兑换 2 颗", !w.CanAfford(2));
            True("兑换成功", w.Spend(1));
            Eq("兑换后余额", w.UsableWhole, 0);
            Eq("碎片不受兑换影响", w.FragmentTenths, 4);
            w.TotalTenths += 6;                     // 0.6 碎片凑成 1 颗
            Eq("碎片进位后完整颗", w.UsableWhole, 1);
            Eq("进位后碎片", w.FragmentTenths, 0);
        }

        private static void TestMergedWhole()
        {
            Group("碎片拼合统计");
            var d = new AppData();
            for (int i = 0; i < 3; i++)
            {
                var rec = new SessionRecord();
                rec.StartedAt = DayKey.Stamp(DateTime.Now.AddMinutes(-i * 30));
                rec.FocusedSec = 15 * 60;
                rec.Tenths = TomatoMath.TenthsForCompleted(15 * 60);
                rec.Preset = "15";
                d.Sessions.Add(rec);
            }
            Store.RebuildDays(d);
            Eq("累计格数", d.Wallet.TotalTenths, 18);
            Eq("完整颗", d.Wallet.UsableWhole, 1);
            Eq("碎片", d.Wallet.FragmentTenths, 8);
            Eq("由碎片拼合的完整颗", Stats.MergedWhole(d), 1);
        }

        private static void TestStats()
        {
            Group("统计");
            var d = new AppData();
            var today = DateTime.Now.Date;
            for (int i = 0; i < 4; i++)
            {
                var rec = new SessionRecord();
                rec.StartedAt = DayKey.Stamp(today.AddDays(-i).AddHours(10));
                rec.FocusedSec = 25 * 60;
                rec.Tenths = 10;
                rec.Preset = "25";
                d.Sessions.Add(rec);
            }
            Store.RebuildDays(d);
            Eq("连续天数", Stats.Streak(d, today), 4);
            string key;
            Eq("单日最高", Stats.BestDayTenths(d, out key), 10);
            Eq("最长单次", Stats.LongestSessionSeconds(d), 1500);
            Eq("今日格数", Stats.TodayTenths(d), 10);
            Eq("完整完成数", Stats.CleanPomodoros(d), 4);
            Eq("最长连续天数", Stats.LongestStreak(d), 4);

            var rec2 = new SessionRecord();
            rec2.StartedAt = DayKey.Stamp(today.AddHours(23));
            rec2.FocusedSec = 600;
            rec2.Tenths = TomatoMath.TenthsForAborted(600);
            rec2.Aborted = true;
            d.Sessions.Add(rec2);
            Store.RebuildDays(d);
            Eq("中断计入当日", Stats.TodayTenths(d), 12);
            Eq("中断计数", Stats.AbortedCount(d), 1);
        }

        private static void TestJson()
        {
            Group("JSON");
            var w = new JsonWriter();
            w.BeginObject();
            w.Name("a").Value(1);
            w.Name("b").Value("带\"引号\"和\n换行");
            w.Name("c").BeginArray().Value(1.5).Value(true).Null().EndArray();
            w.Name("d").BeginObject().Name("x").Value(0).EndObject();
            w.EndObject();
            string text = w.ToString();
            var o = JsonObj.Parse(text);
            Eq("整数", o.Int("a"), 1);
            Eq("字符串转义", o.Str("b"), "带\"引号\"和\n换行");
            Eq("数组长度", o.Arr("c").Count, 3);
            Eq("嵌套对象", o.Obj("d").Int("x"), 0);
            var bad = Json.ParseOrNull("{ not json");
            True("非法 JSON 返回 null", bad == null);
        }

        private static void TestStoreRoundTrip()
        {
            Group("持久化");
            var d = new AppData();
            d.Settings.ThemeId = "night";
            d.Settings.CustomPresets.Add(45);
            d.Wallet.SpentWhole = 2;
            d.Counters["breaksTaken"] = 7;
            d.Rewards.Owned.Add("t_novice");
            d.Rewards.EquippedTitle = "t_novice";
            var rec = new SessionRecord();
            rec.Id = "abc";
            rec.StartedAt = "2026-02-14 09:30:00";
            rec.PlannedSec = 1500;
            rec.FocusedSec = 900;
            rec.Tenths = 3;
            rec.Preset = "25";
            rec.Aborted = true;
            d.Sessions.Add(rec);
            var rec2 = new SessionRecord();
            rec2.Id = "def";
            rec2.StartedAt = "2026-02-13 09:30:00";
            rec2.PlannedSec = 1500;
            rec2.FocusedSec = 1500;
            rec2.Tenths = 10;
            rec2.Preset = "25";
            d.Sessions.Add(rec2);
            var rec3 = new SessionRecord();
            rec3.Id = "ghi";
            rec3.StartedAt = "2026-02-12 09:30:00";
            rec3.PlannedSec = 1500;
            rec3.FocusedSec = 1500;
            rec3.Tenths = 10;
            rec3.Preset = "25";
            d.Sessions.Add(rec3);

            True("保存成功", Store.Save(d));
            Store.Save(d);   // 第二次保存会生成 .bak 备份
            var back = Store.Load();
            Eq("主题", back.Settings.ThemeId, "night");
            Eq("自定档位", back.Settings.CustomPresets.Count, 1);
            Eq("会话数", back.Sessions.Count, 3);
            Eq("中断标记如实保留", back.Sessions[0].Aborted, true);
            Eq("格数如实保留", back.Sessions[0].Tenths, 3);
            Eq("计数器", back.Counter("breaksTaken"), 7);
            Eq("已兑换", back.Wallet.SpentWhole, 2);
            Eq("装备称号", back.Rewards.EquippedTitle, "t_novice");
            Eq("重建日聚合", back.Days.Count, 3);

            // 损坏文件应自愈（回退到 .bak 备份）
            File.WriteAllText(AppPaths.DataFile, "{ 这不是 JSON");
            var recovered = Store.Load();
            Eq("损坏后从备份恢复", recovered.Sessions.Count, 3);
        }

        private static void TestAchievementsAndRewards()
        {
            Group("成就与奖励");
            var d = new AppData();
            var rec = new SessionRecord();
            rec.StartedAt = DayKey.Stamp(DateTime.Today.AddHours(12));
            rec.FocusedSec = 1500;
            rec.Tenths = 10;
            rec.Preset = "25";
            d.Sessions.Add(rec);
            Store.RebuildDays(d);
            var unlocked = Achievements.Evaluate(d, DateTime.Today.AddHours(12));
            True("解锁第一颗番茄", Contains(unlocked, "first"));
            True("解锁单次 25 分钟", Contains(unlocked, "long25"));
            True("白天不误触发晨型/夜猫", !Contains(unlocked, "early") && !Contains(unlocked, "night"));
            Eq("已解锁数量", Achievements.UnlockedCount(d), 2);

            var w = new Wallet();
            w.TotalTenths = 4;                      // 只有碎片
            d.Wallet = w;
            string err;
            True("碎片无法兑换", !Rewards.Redeem(d, "t_novice", out err));
            Eq("失败原因", err, I18n.T("reward.insufficient"));
            d.Wallet.TotalTenths = 60;              // 6 颗完整
            True("完整颗可兑换", Rewards.Redeem(d, "t_novice", out err));
            Eq("扣除完整颗", d.Wallet.UsableWhole, 1);
            Eq("碎片不受影响", d.Wallet.FragmentTenths, 0);
            Eq("装备称号", d.Rewards.EquippedTitle, "t_novice");
        }

        private static bool Contains(List<AchievementDef> list, string id)
        {
            foreach (var d in list) if (d.Id == id) return true;
            return false;
        }

        private static void TestHealthRules()
        {
            Group("健康关怀");
            int idx = -1;
            string p1 = HealthRules.NextPhrase(ref idx);
            string p2 = HealthRules.NextPhrase(ref idx);
            True("语句非空", !string.IsNullOrEmpty(p1));
            True("相邻语句不重复", p1 != p2, p1 + " / " + p2);
            True("夜间判定", HealthRules.IsNight(new DateTime(2026, 2, 14, 23, 0, 0)));
            True("白天不判夜间", !HealthRules.IsNight(new DateTime(2026, 2, 14, 14, 0, 0)));
        }

        private static void TestI18n()
        {
            Group("国际化");
            True("默认中文", I18n.Current == "zh-CN");
            True("标题非空", I18n.T("app.title") != "app.title");
            True("关怀语句 ≥ 40 条", I18n.List("phrases").Length >= 40, I18n.List("phrases").Length.ToString());
            Eq("缺失键回退为键名", I18n.T("no.such.key"), "no.such.key");
        }

        // --- 渲染 ----------------------------------------------------------
        /// <summary>扫描区域内与背景色不同的像素，得到墨迹包围盒。</summary>
        private static RectangleF InkBoundsOf(Bitmap bmp, RectangleF region, Color bg)
        {
            int x0 = (int)Math.Max(0, region.Left), y0 = (int)Math.Max(0, region.Top);
            int x1 = (int)Math.Min(bmp.Width - 1, region.Right), y1 = (int)Math.Min(bmp.Height - 1, region.Bottom);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (Math.Abs(c.R - bg.R) <= 10 && Math.Abs(c.G - bg.G) <= 10 && Math.Abs(c.B - bg.B) <= 10) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            if (minX > maxX) return RectangleF.Empty;
            return new RectangleF(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        private static void TestTextCentering()
        {
            Group("文本对齐（墨迹居中）");
            var bg = Color.FromArgb(255, 244, 244, 244);
            using (var bmp = new Bitmap(260, 130, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var pt = new Painter(g, 1f, Theme.ById("fresh"));
                g.Clear(Color.White);

                CheckCentered(bmp, pt, bg, "暂停", pt.F(16f, true));
                CheckCentered(bmp, pt, bg, "重置", pt.F(13f, true));
                CheckCentered(bmp, pt, bg, "25:00", pt.F(48f, true, Painter.MonoFamily));
                CheckCentered(bmp, pt, bg, "今日 2.6", pt.F(19f, true));
                CheckCentered(bmp, pt, bg, "Ag", pt.F(16f, true));

                // 按钮标签用的是「垂直居中 + 左对齐」变体
                CheckCenteredV(bmp, pt, bg, "开始", pt.F(15.5f, true));
                CheckCenteredV(bmp, pt, bg, "结束本轮", pt.F(13f, true));
                CheckCenteredV(bmp, pt, bg, "可用 12 颗", pt.F(13f, true));
            }
        }

        private static void CheckCenteredV(Bitmap bmp, Painter pt, Color bg, string text, Font font)
        {
            using (var g = Graphics.FromImage(bmp))
                g.Clear(Color.White);
            var box = new RectangleF(20, 20, 220, 90);
            float x = box.Left + 20f;
            using (var g = Graphics.FromImage(bmp))
            {
                pt.FillRound(box, 8f, bg);
                pt.TextCenterV(text, font, Color.Black, x, box);
            }
            var ink = InkBoundsOf(bmp, RectangleF.Inflate(box, -14, -14), bg);
            if (ink.IsEmpty) { True("文本已绘制 " + text, false); return; }
            float top = ink.Top - box.Top, bottom = box.Bottom - ink.Bottom;
            True("垂直居中(左对齐) " + text, Math.Abs(top - bottom) <= 2.5f,
                string.Format("上={0:0.0} 下={1:0.0}", top, bottom));
            True("左端对齐 " + text, Math.Abs(ink.Left - x) <= 2f,
                string.Format("左={0:0.0} 期望={1:0.0}", ink.Left, x));
        }

        private static void CheckCentered(Bitmap bmp, Painter pt, Color bg, string text, Font font)
        {
            using (var g = Graphics.FromImage(bmp))
                g.Clear(Color.White);
            var box = new RectangleF(20, 20, 220, 90);
            using (var g = Graphics.FromImage(bmp))
            {
                pt.FillRound(box, 8f, bg);
                pt.TextCenter(text, font, Color.Black, box);
            }
            // 扫描区域要避开圆角外的白底，否则圆角像素会被误判为墨迹
            var ink = InkBoundsOf(bmp, RectangleF.Inflate(box, -14, -14), bg);
            if (ink.IsEmpty) { True("文本已绘制 " + text, false); return; }
            float top = ink.Top - box.Top, bottom = box.Bottom - ink.Bottom;
            float left = ink.Left - box.Left, right = box.Right - ink.Right;
            True("垂直居中 " + text, Math.Abs(top - bottom) <= 2.5f,
                string.Format("上={0:0.0} 下={1:0.0}", top, bottom));
            True("水平居中 " + text, Math.Abs(left - right) <= 2.5f,
                string.Format("左={0:0.0} 右={1:0.0}", left, right));
        }

        private static void TestTomatoBounds()
        {
            Group("番茄图形边界");
            var bg = Color.White;
            foreach (float size in new[] { 16f, 22f, 34f, 90f, 300f })
            {
                using (var bmp = new Bitmap((int)size + 8, (int)size + 8, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(bg);
                    var pt = new Painter(g, 1f, Theme.ById("fresh"));
                    var box = new RectangleF(4, 4, size, size);
                    TomatoArt.DrawTomato(pt, box, 1f, true, pt.T.Accent, pt.T.AccentDark, pt.T.Leaf);
                    var ink = InkBoundsOf(bmp, new RectangleF(0, 0, bmp.Width, bmp.Height), bg);
                    bool inside = ink.Left >= box.Left - 1 && ink.Top >= box.Top - 1 &&
                                  ink.Right <= box.Right + 1 && ink.Bottom <= box.Bottom + 1;
                    True("番茄不超出给定区域 @" + size, inside,
                        string.Format("墨迹={0:0.0},{1:0.0},{2:0.0},{3:0.0} 区域={4:0.0},{5:0.0},{6:0.0},{7:0.0}",
                            ink.Left, ink.Top, ink.Right, ink.Bottom, box.Left, box.Top, box.Right, box.Bottom));
                    // 萼片应向上张开而不是偏向一侧：整体墨迹必须左右对称
                    float dx = (ink.Left + ink.Right) / 2f - (box.Left + box.Right) / 2f;
                    True("番茄左右对称 @" + size, Math.Abs(dx) <= 1.5f, "偏移 " + dx.ToString("0.0") + "px");
                }
            }
        }

        /// <summary>进度环与内部文字在计时过程中不得发生缩放。</summary>
        private static void TestRingStability()
        {
            Group("进度环稳定性");
            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.StartFocus(25, "25");

            using (var a = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var ga = Graphics.FromImage(a))
            using (var b = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var gb = Graphics.FromImage(b))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                var whole = new RectangleF(0, 0, 1040, 700);
                ui.Draw(ga);
                var inkA = InkBoundsOf(a, whole, app.CurrentTheme.Bg);
                for (int i = 0; i < 12; i++) ui.Update(1.0 / 60.0);   // 前进 0.2 秒
                ui.Draw(gb);
                var inkB = InkBoundsOf(b, whole, app.CurrentTheme.Bg);
                True("0.2 秒内画面范围不变", !inkA.IsEmpty && inkA.Equals(inkB), inkA + " vs " + inkB);
            }
        }

        /// <summary>悬停时必须有明显视觉反馈并出现手型光标。</summary>
        private static void TestHoverFeedback()
        {
            Group("悬停反馈");
            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.SetPresetMinutes(25, "25");

            using (var a = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var ga = Graphics.FromImage(a))
            using (var b = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var gb = Graphics.FromImage(b))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                ui.Draw(ga);
                RectangleF btn;
                if (!ui.TryGetHotspot("btnPrimary", out btn)) { True("找到主按钮", false); return; }

                // 悬停动画在 Draw 中按需创建，因此需要「绘制-推进」交替若干轮才能收敛
                ui.OnMouseMove(new PointF(20, 690), true);
                for (int i = 0; i < 40; i++) { ui.Draw(ga); ui.Update(1.0 / 60.0); }
                ui.Draw(ga);

                ui.OnMouseMove(Center(btn), true);
                for (int i = 0; i < 40; i++) { ui.Draw(gb); ui.Update(1.0 / 60.0); }
                ui.Draw(gb);

                True("悬停时启用点击光标", ui.HoverClickable);
                var region = RectangleF.Inflate(btn, 10, 10);
                int diff = 0, n = 0;
                for (int x = (int)region.Left; x < region.Right; x += 2)
                    for (int y = (int)region.Top; y < region.Bottom; y += 2)
                    {
                        n++;
                        var ca = a.GetPixel(x, y);
                        var cb = b.GetPixel(x, y);
                        if (Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B) > 18) diff++;
                    }
                True("悬停视觉变化明显", n > 0 && diff > n * 0.15, "变化像素 " + diff + "/" + n);
            }
        }

        /// <summary>针对已报缺陷的交互回归测试。</summary>
        private static void TestUiInteractions()
        {
            Group("界面交互回归");
            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.SetPresetMinutes(25, "25");

            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                RectangleF r;

                // ① 点击档位只切换时长，不自动开始
                ui.Draw(g);
                True("找到 5 分钟档位", ui.TryGetHotspot("preset5", out r));
                ui.ClickAt(Center(r));
                Eq("点击档位后仍为空闲", app.Timer.Phase, TimerPhase.Idle);
                Eq("档位已切换为 5 分钟", app.Timer.PlannedSeconds, 300);

                // ② 必须点“开始”才计时
                ui.Draw(g);
                True("找到开始按钮", ui.TryGetHotspot("btnPrimary", out r));
                ui.ClickAt(Center(r));
                Eq("点击开始后进入专注", app.Timer.Phase, TimerPhase.Focusing);
                app.Timer.Reset();

                // ③ 专注中切换档位不生效
                app.Timer.StartFocus(25, "25");
                ui.Draw(g);
                ui.TryGetHotspot("preset5", out r);
                ui.ClickAt(Center(r));
                Eq("专注中切换档位被忽略", app.Timer.PlannedSeconds, 1500);
                app.Timer.Reset();

                // ④ 自定档位就地输入，点击空白处关闭
                ui.Draw(g);
                True("找到自定档位", ui.TryGetHotspot("presetCustom", out r));
                ui.ClickAt(Center(r));
                True("进入就地输入", ui.IsCustomInputActive);
                ui.TypeDigits("45");
                Eq("输入内容", ui.CustomInputText, "45");
                ui.ClickAt(new PointF(520, 690));
                True("点击空白处关闭输入", !ui.IsCustomInputActive);
                Eq("取消后输入被清空", ui.CustomInputText, "");

                // ⑤ 回车确认后档位生效且不自动开始
                ui.Draw(g);
                ui.TryGetHotspot("presetCustom", out r);
                ui.ClickAt(Center(r));
                ui.TypeDigits("45");
                ui.OnKey(Keys.Enter);
                Eq("自定 45 分钟已生效", app.Timer.PlannedSeconds, 2700);
                Eq("自定后仍为空闲", app.Timer.Phase, TimerPhase.Idle);
                True("确认后退出输入态", !ui.IsCustomInputActive);

                // ⑥ 顶栏按钮互不重叠，汉堡菜单能打开抽屉并切换标签
                ui.Draw(g);
                RectangleF a1, a2;
                ui.TryGetHotspot("menu", out a1); ui.TryGetHotspot("min", out a2);
                True("菜单与最小化不重叠", a1.Right <= a2.Left);
                ui.TryGetHotspot("min", out a1); ui.TryGetHotspot("close", out a2);
                True("最小化与关闭不重叠", a1.Right <= a2.Left);
                ui.TryGetHotspot("close", out a2);
                True("关闭按钮离右边有间距", a2.Right <= 1040f - 16f, "right=" + a2.Right);

                ui.Draw(g);
                ui.TryGetHotspot("menu", out a1);
                ui.ClickAt(Center(a1));
                Eq("点击汉堡按钮打开菜单抽屉", ui.CurrentDrawer, "menu");
                Settle(ui, g);
                RectangleF tab;
                if (ui.TryGetHotspot("tabrewards", out tab))
                {
                    ui.ClickAt(Center(tab));
                    Eq("切换到奖励标签", ui.CurrentMenuTab, "rewards");
                }
                else True("找到奖励标签", false);
                ui.Draw(g);
                ui.TryGetHotspot("menu", out a1);
                ui.ClickAt(Center(a1));
                Eq("再次点击汉堡关闭抽屉", ui.CurrentDrawer, "");

                // ⑧ 空闲态不应出现「结束本轮」，专注态才出现
                ui.Draw(g);
                RectangleF dummy;
                True("空闲态没有结束按钮", !ui.TryGetHotspot("btnStop", out dummy));
                app.Timer.StartFocus(25, "25");
                Settle(ui, g);
                True("专注态出现结束按钮", ui.TryGetHotspot("btnStop", out dummy));
                app.Timer.Reset();

                // ⑦ 点击抽屉外区域关闭抽屉
                ui.SetDrawer("menu");
                ui.SetMenuTab("settings");
                ui.Draw(g);
                Eq("抽屉已打开", ui.CurrentDrawer, "menu");
                ui.ClickAt(new PointF(200, 400));
                Eq("点击空白关闭抽屉", ui.CurrentDrawer, "");
            }
        }

        private static PointF Center(RectangleF r)
        {
            return new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
        }

        /// <summary>绘制-推进交替若干帧，让动画收敛（抽屉滑入、按钮分裂等）。</summary>
        private static void Settle(UiRoot ui, Graphics g, int frames = 40)
        {
            for (int i = 0; i < frames; i++) { ui.Draw(g); ui.Update(1.0 / 60.0); }
            ui.Draw(g);
        }

        /// <summary>抽屉滚动、层级、按钮布局等本轮修复点的回归测试。</summary>
        private static void TestDrawerAndLayout()
        {
            Group("抽屉与布局回归");
            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.SetPresetMinutes(25, "25");

            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                RectangleF r;

                // ① 结束本轮 → 确认条必须出现
                app.Timer.StartFocus(25, "25");
                Settle(ui, g);
                True("专注态有结束按钮", ui.TryGetHotspot("btnStop", out r));
                ui.ClickAt(Center(r));
                // 确认条是淡入动画，需要「绘制-推进」交替才能收敛
                for (int i = 0; i < 30; i++) { ui.Draw(g); ui.Update(1.0 / 60.0); }
                ui.Draw(g);
                True("点击结束本轮后进入确认态", ui.IsConfirming);
                True("确认条已出现", ui.TryGetHotspot("cfYes", out r));
                ui.ClickAt(Center(r));                       // 点「继续专注」
                ui.Draw(g);
                True("取消后退出确认态", !ui.IsConfirming);
                app.Timer.Reset();

                // ② 抽屉滚轮：能滚动且会触发重绘
                ui.SetDrawer("menu");
                ui.SetMenuTab("achievements");
                ui.Draw(g);
                True("抽屉已打开", ui.CurrentDrawer == "menu");
                Settle(ui, g, 30);
                ui.OnMouseMove(new PointF(800, 400), true);
                ui.TakeDirty();                              // 清掉标记
                float before = ui.DrawerScroll;
                ui.OnMouseWheel(-120);
                True("滚轮改变滚动位置", ui.DrawerScroll > before, before + " -> " + ui.DrawerScroll);
                True("滚轮会触发重绘", ui.TakeDirty());

                // ③ 滚动条可拖动
                ui.Draw(g);
                True("存在滚动条热区", ui.TryGetHotspot("scrollBar", out r));
                float mid = ui.DrawerScroll;
                ui.OnMouseDown(new PointF(Center(r).X, r.Top + r.Height * 0.8f));
                ui.OnMouseMove(new PointF(Center(r).X, r.Top + r.Height * 0.95f), true);
                ui.OnMouseUp(new PointF(Center(r).X, r.Top + r.Height * 0.95f));
                True("拖动滚动条生效", ui.DrawerScroll > mid, mid + " -> " + ui.DrawerScroll);
                ui.SetDrawer("");

                // ④a 奖励窗：点分类标签必须切换分类并触发重绘
                ui.SetDrawer("menu");
                ui.SetMenuTab("rewards");
                ui.SetRewardCategory("title");
                ui.Draw(g);
                RectangleF chip;
                if (ui.TryGetHotspot("rcmedal", out chip))
                {
                    ui.TakeDirty();
                    ui.ClickAt(Center(chip));
                    Eq("分类已切换", ui.RewardCategory, "medal");
                    True("切换分类会触发重绘", ui.TakeDirty());
                }
                else True("找到奖章分类标签", false);
                ui.SetDrawer("");

                // ④b 成就窗滚到底后，标题栏关闭按钮仍必须可点（内容不能抢点击）
                ui.SetDrawer("menu");
                ui.SetMenuTab("achievements");
                ui.Draw(g);
                ui.OnMouseMove(new PointF(800, 400), true);
                for (int i = 0; i < 40; i++) ui.OnMouseWheel(-120);
                ui.Draw(g);
                True("成就窗已滚到底", ui.DrawerScroll > 0);
                True("关闭按钮仍在", ui.TryGetHotspot("drawerClose", out r));
                ui.ClickAt(Center(r));
                Eq("滚到底后仍能点关闭按钮", ui.CurrentDrawer, "");

                // ④c 内容裁剪：滚动到底后，内容热区不得越出内容区
                ui.SetDrawer("menu");
                ui.SetMenuTab("settings");
                ui.Draw(g);
                ui.OnMouseMove(new PointF(800, 400), true);
                for (int i = 0; i < 60; i++) ui.OnMouseWheel(-120);
                ui.Draw(g);
                RectangleF body;
                True("找到内容区", ui.TryGetHotspot("drawerBg", out body));
                bool outside = false;
                foreach (var hs in ui.Hotspots)
                    if (hs.Id.StartsWith("tg", StringComparison.Ordinal) &&
                        (hs.Rect.Top < body.Top - 0.5f || hs.Rect.Bottom > body.Bottom + 0.5f)) outside = true;
                True("内容热区不越界", !outside, "body=" + body);
                ui.SetDrawer("");

                // ⑤ 余额不足时点兑换：不关抽屉，只给提示
                ui.SetDrawer("menu");
                ui.SetMenuTab("rewards");
                ui.SetRewardCategory("title");
                ui.Draw(g);
                RectangleF redeem;
                if (ui.TryGetHotspot("rwt_novice", out redeem))
                {
                    ui.ClickAt(Center(redeem));
                    Eq("余额不足点兑换不会关闭抽屉", ui.CurrentDrawer, "menu");
                }
                else True("找到兑换按钮", false);
                ui.SetDrawer("");

                // ⑥ 抽屉打开时快捷键不作用于主界面
                ui.SetDrawer("menu");
                ui.SetMenuTab("settings");
                ui.Draw(g);
                ui.OnKey(Keys.Space);
                Eq("抽屉内空格不会启动专注", app.Timer.Phase, TimerPhase.Idle);
                ui.SetDrawer("");

                // ⑦ 开始/结束按钮等宽（需先让分裂动画收敛）
                app.Timer.StartFocus(25, "25");
                for (int i = 0; i < 40; i++) { ui.Draw(g); ui.Update(1.0 / 60.0); }
                ui.Draw(g);
                RectangleF p1, p2;
                True("找到开始按钮", ui.TryGetHotspot("btnPrimary", out p1));
                True("找到结束按钮", ui.TryGetHotspot("btnStop", out p2));
                Eq("两个按钮宽度一致", (int)p1.Width, (int)p2.Width);
                Eq("两个按钮高度一致", (int)p1.Height, (int)p2.Height);
                Eq("暂停按钮宽度 140", (int)p1.Width, 140);
                app.Timer.Reset();

                // 空闲时开始按钮宽度 = 两个按钮宽度之和
                for (int i = 0; i < 40; i++) { ui.Draw(g); ui.Update(1.0 / 60.0); }
                ui.Draw(g);
                ui.TryGetHotspot("btnPrimary", out p1);
                Eq("空闲时开始按钮宽 292", (int)p1.Width, 292);
                True("空闲时没有结束按钮", !ui.TryGetHotspot("btnStop", out p2));

                // ⑧ 档位行与开始按钮同中心
                ui.Draw(g);
                RectangleF first, custom, primary;
                ui.TryGetHotspot("preset5", out first);
                ui.TryGetHotspot("presetCustom", out custom);
                ui.TryGetHotspot("btnPrimary", out primary);
                float rowCenter = (first.Left + custom.Right) / 2f;
                float btnCenter = primary.Left + primary.Width / 2f;
                True("档位行居中于按钮", Math.Abs(rowCenter - btnCenter) <= 3f,
                    "档位中心 " + rowCenter.ToString("0.0") + " vs 按钮中心 " + btnCenter.ToString("0.0"));

                // ⑨ 视觉重心上移
                True("时间环中心上移", ui.RingCenter.Y < 290f, "Y=" + ui.RingCenter.Y.ToString("0.0"));

                // ⑩ 分裂/合并全过程：两个按钮绝不重叠、绝不越出配对区
                app.Timer.StartFocus(25, "25");
                Settle(ui, g);
                bool overlap = false, overflow = false;
                for (int i = 0; i < 40; i++)
                {
                    ui.Draw(g);
                    RectangleF pa, pb;
                    if (ui.TryGetHotspot("btnPrimary", out pa) && ui.TryGetHotspot("btnStop", out pb))
                    {
                        if (pa.Right + 11f > pb.Left) overlap = true;
                        if (pb.Right > pa.Left + 292.5f) overflow = true;
                    }
                    ui.Update(1.0 / 60.0);
                }
                True("合并过程按钮不重叠", !overlap);
                True("合并过程不越出配对区", !overflow);

                app.Timer.Reset();
                overlap = false; overflow = false;
                for (int i = 0; i < 40; i++)
                {
                    ui.Draw(g);
                    RectangleF pa, pb;
                    if (ui.TryGetHotspot("btnPrimary", out pa) && ui.TryGetHotspot("btnStop", out pb))
                    {
                        if (pa.Right + 11f > pb.Left) overlap = true;
                        if (pb.Right > pa.Left + 292.5f) overflow = true;
                    }
                    ui.Update(1.0 / 60.0);
                }
                True("合并回单按钮过程也不重叠", !overlap && !overflow);

                // ⑪ 抽屉内容不得与滚动条重叠
                ui.SetDrawer("menu");
                ui.SetMenuTab("settings");
                Settle(ui, g, 20);
                RectangleF track;
                if (ui.TryGetHotspot("scrollBar", out track))
                {
                    bool hitBar = false;
                    foreach (var hs in ui.Hotspots)
                        if (hs.Id.StartsWith("tg", StringComparison.Ordinal) && hs.Rect.Right > track.Left + 0.5f) hitBar = true;
                    True("内容不与滚动条重叠", !hitBar, "滚动条左边=" + track.Left);
                }
                else True("设置页出现滚动条", false);
                ui.SetDrawer("");
            }
        }

        /// <summary>模式配色、主按钮文案与秒级节拍的回归测试。</summary>
        private static void TestModeTint()
        {
            Group("模式配色与节拍");
            var app = new AppState();
            app.Data = new AppData();
            Store.Normalize(app.Data);
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.SetPresetMinutes(25, "25");
            app.Data.Settings.BreakCustomMinutes = 0;

            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                Settle(ui, g, 20);

                // ① 专注档：主按钮与时间环为红色系
                int focusRed = CountReddish(bmp, 180, 900, 560, 640);
                True("专注档主按钮为红色系", focusRed > 400, "红色像素 " + focusRed);

                // ② 切到休息档：相关元素渐变为时间环同款绿色
                RectangleF seg;
                ui.TryGetHotspot("modeBreak", out seg);
                ui.ClickAt(Center(seg));
                Settle(ui, g, 40);
                Eq("已切到休息模式", ui.CurrentMode, "break");
                int brkRed = CountReddish(bmp, 180, 900, 560, 640);
                int brkGreen = CountGreenish(bmp, 180, 900, 560, 640);
                True("休息档主按钮变成绿色系", brkGreen > 400, "绿色像素 " + brkGreen);
                True("休息档不再有红色主按钮", brkRed < focusRed / 3, "红 " + brkRed + " vs " + focusRed);

                // 进度环的弧度也应是绿色：用 30 秒短休息推进出可见弧长后再取样
                app.Timer.StartBreak(30);
                Eq("已开始休息", app.Timer.Phase, TimerPhase.Break);
                System.Threading.Thread.Sleep(1600);     // 让单调时钟累计真实进度
                app.Timer.Update();
                ui.Draw(g);
                int ringGreen = CountGreenish(bmp, 300, 380, 88, 122);
                True("进度环弧度同步为绿色", ringGreen > 20, "绿色像素 " + ringGreen + "，进度 " + app.Timer.Progress.ToString("0.00"));
                app.Timer.Reset();
                Settle(ui, g, 30);

                // ③ 休息档主按钮文案仍为「开始」
                Settle(ui, g, 20);
                RectangleF primary;
                ui.TryGetHotspot("btnPrimary", out primary);
                string expect = I18n.T("focus.start");
                var font = new Painter(g, 1f, app.CurrentTheme).F(15.5f, true);
                float tw = new Painter(g, 1f, app.CurrentTheme).TextWidth(expect, font);
                int ink = CountWhiteInk(bmp, primary);
                True("休息档主按钮仍有文字", ink > 40, "白字像素 " + ink + "（期望文案「" + expect + "」宽 " + tw.ToString("0") + "）");
                Eq("不再有单独的「开始休息」文案", I18n.T("focus.startBreak"), "focus.startBreak");

                // ④ 默认主题名称
                Eq("默认主题已改名", I18n.T("rw.th_fresh"), "默认主题");

                // ⑤ 秒级节拍：每秒触发一次
                int ticks = 0;
                EventHandler h = delegate { ticks++; };
                app.ClockTick += h;
                app.Tick();
                app.Tick();
                True("同一秒内只触发一次", ticks <= 1, "次数 " + ticks);
                app.ClockTick -= h;
            }
        }

        private static int CountReddish(Bitmap bmp, int x0, int x1, int y0, int y1)
        {
            int n = 0;
            for (int x = x0; x < x1; x += 2)
                for (int y = y0; y < y1; y += 2)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.R > 180 && c.R - c.G > 70 && c.R - c.B > 70) n++;
                }
            return n;
        }

        private static int CountGreenish(Bitmap bmp, int x0, int x1, int y0, int y1)
        {
            int n = 0;
            for (int x = x0; x < x1; x += 2)
                for (int y = y0; y < y1; y += 2)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.G > 120 && c.G - c.R > 40 && c.G - c.B > 25) n++;
                }
            return n;
        }

        private static int CountWhiteInk(Bitmap bmp, RectangleF r)
        {
            int n = 0;
            for (int x = (int)r.Left; x < r.Right; x++)
                for (int y = (int)r.Top; y < r.Bottom; y++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.R > 235 && c.G > 235 && c.B > 235) n++;
                }
            return n;
        }
        /// <summary>托盘与专注休息模式的回归测试。</summary>
        private static void TestTrayAndMode()
        {
            var app = new AppState();
            app.Data = new AppData();
            Store.Normalize(app.Data);
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");

            // ① 自定值与档位的持久化往返
            app.Data.Settings.CustomMinutes = 45;
            app.Data.Settings.BreakCustomMinutes = 12;
            app.Data.Settings.LastPresetMinutes = 45;
            app.Data.Settings.LastPresetId = "custom";
            True("保存成功", Store.Save(app.Data));
            var back = Store.Load();
            Eq("自定值已持久化", back.Settings.CustomMinutes, 45);
            Eq("休息自定值已持久化", back.Settings.BreakCustomMinutes, 12);
            Eq("上次档位已持久化", back.Settings.LastPresetMinutes, 45);
            Eq("上次档位标识已持久化", back.Settings.LastPresetId, "custom");

            // ② 启动时恢复上次档位
            var app2 = new AppState();
            app2.Load();
            Eq("启动恢复上次档位", app2.Timer.PlannedSeconds, 45 * 60);
            Eq("档位标识也恢复", app2.Timer.Preset, "custom");

            // ③ 托盘菜单：预构建且含关键项
            using (var menu = new System.Windows.Forms.ContextMenuStrip())
            {
                int n = Tray.TrayController.BuildMenuForTest(app2, menu);
                True("菜单已预构建且非空", n >= 6, "项数 " + n);
            }

            app.Timer.SetPresetMinutes(25, "25");
            app.Data.Settings.BreakCustomMinutes = 0;      // 复位，便于验证默认 5 分钟
            app.Data.Settings.BreakSeconds = 300;
            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                ui.Draw(g);

                // ④ 徽章与汉堡按钮不再重叠
                RectangleF menuBtn, medal;
                ui.TryGetHotspot("menu", out menuBtn);
                True("徽章与菜单按钮留出间距", ui.WalletBadgeRect.Right <= menuBtn.Left - 8f,
                    "徽章右=" + ui.WalletBadgeRect.Right.ToString("0") + " 按钮左=" + menuBtn.Left.ToString("0"));

                // ⑤ 模式切换：空闲时可切到休息并开始休息
                RectangleF segBreak, segFocus;
                Eq("默认处于专注模式", ui.CurrentMode, "focus");
                True("存在休息段", ui.TryGetHotspot("modeBreak", out segBreak));
                True("存在专注段", ui.TryGetHotspot("modeFocus", out segFocus));
                ui.ClickAt(Center(segBreak));
                Eq("已切到休息模式", ui.CurrentMode, "break");
                Eq("休息模式默认选中 5 分钟", ui.BreakPresetMinutes, 5);

                Settle(ui, g, 20);
                RectangleF primary;
                ui.TryGetHotspot("btnPrimary", out primary);
                ui.ClickAt(Center(primary));
                Eq("空闲时点主按钮开始休息", app.Timer.Phase, TimerPhase.Break);
                Eq("休息时长取自所选档位", app.Timer.RemainingSeconds, 300);

                // ⑥ 计时中禁止切换模式
                ui.Draw(g);
                ui.TryGetHotspot("modeFocus", out segFocus);
                ui.ClickAt(Center(segFocus));
                Eq("休息进行中无法切回专注", ui.CurrentMode, "break");
                app.Timer.Reset();
                ui.Draw(g);
                ui.TryGetHotspot("modeFocus", out segFocus);
                ui.ClickAt(Center(segFocus));
                Eq("空闲后可切回专注", ui.CurrentMode, "focus");

                // ⑦ 休息模式的档位与时间环预览
                ui.Draw(g);
                ui.TryGetHotspot("modeBreak", out segBreak);
                ui.ClickAt(Center(segBreak));
                Eq("切回休息", ui.CurrentMode, "break");
                ui.Draw(g);
                True("休息档位含 10 分钟", ui.TryGetHotspot("preset10", out medal));
                ui.ClickAt(Center(medal));
                Eq("休息档位改为 10 分钟", ui.BreakPresetMinutes, 10);
                ui.Draw(g);
                Eq("时间环预览跟随休息档位", ui.PreviewMinutes, 10);
                ui.TryGetHotspot("modeFocus", out segFocus);
                ui.ClickAt(Center(segFocus));
                Eq("回到专注模式", ui.CurrentMode, "focus");
            }
        }
        /// <summary>顶栏间距与休息时长的回归测试。</summary>
        private static void TestTopBarSpacing()
        {
            var app = new AppState();
            app.Data = new AppData();
            Store.Normalize(app.Data);
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.SetPresetMinutes(25, "25");
            app.DebugAddTomatoes(12);

            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                ui.Draw(g);

                // ① 可用番茄徽章不得压住汉堡按钮
                RectangleF menuBtn;
                True("找到菜单按钮", ui.TryGetHotspot("menu", out menuBtn));
                True("徽章在菜单按钮左侧", ui.WalletBadgeRect.Right <= menuBtn.Left - 8f,
                    "徽章右=" + ui.WalletBadgeRect.Right.ToString("0") + " 按钮左=" + menuBtn.Left.ToString("0"));
                True("徽章宽度合理", ui.WalletBadgeRect.Width > 60f && ui.WalletBadgeRect.Width < 260f,
                    "宽 " + ui.WalletBadgeRect.Width.ToString("0"));

                // ③ 休息中修改休息时长，不得改变进行中的倒计时
                app.Timer.StartBreak(300);
                int before = app.Timer.RemainingSeconds;
                Eq("休息倒计时初始 300 秒", before, 300);
                ui.SetDrawer("menu");
                ui.SetMenuTab("settings");
                Settle(ui, g, 20);
                RectangleF bk;
                if (ui.TryGetHotspot("bk15", out bk))
                {
                    ui.ClickAt(Center(bk));
                    Eq("设置里的休息时长已更新", app.Data.Settings.BreakSeconds, 900);
                    Eq("进行中的休息不受影响", app.Timer.RemainingSeconds, before);
                }
                else True("找到休息时长选项", false);
                ui.SetDrawer("");
                app.Timer.Reset();

                // ⑥ 长按提示文案已移除（键不存在时 T 返回键名本身）
                Eq("重复的长按提示已删除", I18n.T("settings.clearData.holdHint"), "settings.clearData.holdHint");
            }

            // ② 显示窗口即退出极简模式
            int fired = 0;
            app.MinimalModeChanged += delegate { fired++; };
            app.SetMinimalMode(true);
            True("已进入极简模式", app.Data.Settings.MinimalMode);
            app.ExitMinimalMode();
            True("显示窗口后退出极简", !app.Data.Settings.MinimalMode);
            True("触发了状态变化事件", fired >= 2, "次数 " + fired);
            app.ExitMinimalMode();
            Eq("已退出时不再重复触发", fired, 2);

            // ④ 关怀语句不含具体身份名词
            string[] banned = { "同事", "同学", "朋友", "领导", "老板", "家人", "妈妈", "爸爸" };
            var phrases = I18n.List("phrases");
            True("关怀语句数量充足", phrases.Length >= 40, "条数 " + phrases.Length);
            bool hit = false;
            foreach (string p in phrases)
                foreach (string w in banned)
                    if (p.Contains(w)) { hit = true; Console.WriteLine("      含身份词: " + p); }
            True("关怀语句不含身份名词", !hit);

            // ⑤ 奖励页分类顺序：主题在提示音右侧
            var order = Rewards.CategoryOrder;
            int iSound = Array.IndexOf(order, "sound");
            int iTheme = Array.IndexOf(order, "theme");
            True("存在提示音与主题分类", iSound >= 0 && iTheme >= 0);
            True("主题排在提示音右侧", iTheme > iSound, "sound@" + iSound + " theme@" + iTheme);
        }
        /// <summary>日历与钱包的回归测试。</summary>
        private static void TestCalendarAndWallet()
        {
            var app = new AppState();
            app.Data = new AppData();
            Store.Normalize(app.Data);
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.SetPresetMinutes(25, "25");
            app.DebugAddTomatoes(12);

            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                ui.Draw(g);

                // ① 日历单元格绝不越界到笔记区
                RectangleF notes;
                True("找到笔记栏位", ui.TryGetHotspot("notesArea", out notes));
                bool overlapped = false;
                foreach (var hs in ui.Hotspots)
                    if (hs.Id.StartsWith("day", StringComparison.Ordinal) && hs.Rect.Bottom > notes.Top - 8f)
                        overlapped = true;
                True("日历没有越界盖到笔记", !overlapped);

                // 窄窗口下同样不越界
                ui.Bounds = new RectangleF(0, 0, 900, 620);
                ui.Draw(g);
                ui.TryGetHotspot("notesArea", out notes);
                overlapped = false;
                foreach (var hs in ui.Hotspots)
                    if (hs.Id.StartsWith("day", StringComparison.Ordinal) && hs.Rect.Bottom > notes.Top - 8f)
                        overlapped = true;
                True("窄窗口下日历仍不越界", !overlapped);
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
            }

            // ② 正常完成最低 0.1 颗；中断公式不变（可为 0）
            Eq("1 分钟完成 = 0.1", TomatoMath.TenthsForCompleted(60), 1);
            Eq("1 秒完成 = 0.1", TomatoMath.TenthsForCompleted(1), 1);
            Eq("0 秒 = 0", TomatoMath.TenthsForCompleted(0), 0);
            Eq("5 分钟完成仍是 0.2", TomatoMath.TenthsForCompleted(300), 2);
            Eq("25 分钟完成仍是 1.0", TomatoMath.TenthsForCompleted(1500), 10);
            Eq("中断 4:59 仍为 0", TomatoMath.TenthsForAborted(299), 0);
            Eq("中断 5:00 仍为 0.1", TomatoMath.TenthsForAborted(300), 1);

            // ③ 可用番茄含小数位；兑换仍按整颗
            var w = new Wallet();
            w.TotalTenths = 124;      // 12.4 颗
            w.SpentWhole = 3;
            Eq("可用格数", w.UsableTenths, 94);
            Eq("可用整颗", w.UsableWhole, 9);
            Eq("格式化", TomatoMath.Format(w.UsableTenths), "9.4");

            // ④ 文案不再出现「碎片」「完整」
            string[] banned = { "wallet.usablePrefix", "wallet.usableSuffix", "wallet.fragmentHint", "wallet.fragmentProgress",
                                "reward.available", "reward.wholeHint", "reward.insufficient", "menu.todayInfo",
                                "ach.merged10.name", "ach.merged10.desc" };
            bool badWord = false;
            foreach (string key in banned)
            {
                string text = I18n.T(key);
                if (text.Contains("碎片") || text.Contains("完整")) { badWord = true; Console.WriteLine("      含禁用词: " + key + " = " + text); }
            }
            True("相关文案不含「碎片」「完整」", !badWord);

            // ⑤ 版本号来自程序集且与 AssemblyInfo 一致
            True("版本号已更新", AppInfo.Version != "1.0" && AppInfo.Version.Length > 0, "版本 " + AppInfo.Version);
            Eq("版本号与程序集一致", AppInfo.Version, "1.1.5");
        }
        /// <summary>笔记与红点的回归测试。</summary>
        private static void TestNotesAndBadge()
        {
            Group("笔记与红点");
            var app = new AppState();
            app.Data = new AppData();
            Store.Normalize(app.Data);
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.SetPresetMinutes(25, "25");

            // ① 健康依据每条都必须能折行且不越界
            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var measure = new Painter(g, 1f, app.CurrentTheme);
                var font = measure.F(10.5f);
                float width = 396f - 32f;      // 抽屉内容宽度
                string[] keys = { "health.source.who", "health.source.cn", "health.source.diet", "health.source.aoa",
                                  "health.rule.sedentary", "health.rule.weekly", "health.rule.water", "health.rule.eye", "health.rule.sleep" };
                bool overflow = false;
                foreach (string key in keys)
                {
                    var lines = measure.WrapLines(I18n.T(key), font, width - 14f);
                    if (lines.Count == 0) overflow = true;
                    foreach (var line in lines)
                        if (measure.Measure(line, font).Width > width - 10f) overflow = true;
                }
                True("健康依据全部可折行且不越界", !overflow);

                // ② 笔记栏位存在，输入后写入草稿
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                ui.Draw(g);
                RectangleF notes;
                True("存在笔记栏位", ui.TryGetHotspot("notesArea", out notes));
                ui.ClickAt(Center(notes));
                True("点击后进入输入态", ui.IsNoteFocused);
                ui.OnChar('测');
                ui.OnChar('试');
                Eq("字符写入草稿", app.Data.Settings.NoteDraft, "测试");
                ui.OnKey(Keys.Enter);
                ui.OnKey(Keys.Back);
                Eq("回车与退格生效", app.Data.Settings.NoteDraft, "测试");
                ui.OnKey(Keys.Escape);
                True("Esc 退出输入态", !ui.IsNoteFocused);

                // ③ 结算时笔记随会话保存，草稿清空
                app.Data.Settings.NoteDraft = "今天状态不错";
                app.Timer.StartFocus(25, "25");
                app.DebugCompleteTimer();
                Eq("会话记录了笔记", app.Data.Sessions[app.Data.Sessions.Count - 1].Note, "今天状态不错");
                Eq("结算后草稿清空", app.Data.Settings.NoteDraft, "");

                // ④ 笔记草稿可持久化
                app.Data.Settings.NoteDraft = "写一半的草稿";
                Store.Save(app.Data);
                var back = Store.Load();
                Eq("草稿已持久化", back.Settings.NoteDraft, "写一半的草稿");
                Eq("会话笔记已持久化", back.Sessions[back.Sessions.Count - 1].Note, "今天状态不错");

                // ⑤ 汉堡红点：可兑换奖励出现 → 显示；点击后消失
                app.Data.Settings.NoteDraft = "";
                Eq("初始无可兑换奖励", Rewards.AffordableUnowned(app.Data), 0);
                app.DebugAddTomatoes(60);
                int affordable = Rewards.AffordableUnowned(app.Data);
                True("现在有可兑换奖励", affordable > 0, "数量 " + affordable);
                Eq("红点未读", app.Data.Settings.RewardBadgeSeen, 0);

                ui.Draw(g);
                int dotX = 1040 - 142 + 36 - 8, dotY = 12 + 8;
                var dot = bmp.GetPixel(dotX, dotY);
                True("汉堡右上角出现红点", dot.R > 180 && dot.G < 140 && dot.B < 130,
                    "RGB=" + dot.R + "," + dot.G + "," + dot.B);

                RectangleF menuBtn;
                ui.TryGetHotspot("menu", out menuBtn);
                ui.ClickAt(Center(menuBtn));
                Eq("点击后记录已读数量", app.Data.Settings.RewardBadgeSeen, affordable);
                ui.SetDrawer("");
                ui.Draw(g);
                var after = bmp.GetPixel(dotX, dotY);
                True("红点已消失", !(after.R > 180 && after.G < 140 && after.B < 130),
                    "RGB=" + after.R + "," + after.G + "," + after.B);

                // 再增加可兑换数量 → 红点重新出现
                app.DebugAddTomatoes(300);
                True("可兑换数量增加", Rewards.AffordableUnowned(app.Data) > affordable);
                ui.Draw(g);
                var again = bmp.GetPixel(dotX, dotY);
                True("红点重新出现", again.R > 180 && again.G < 140 && again.B < 130,
                    "RGB=" + again.R + "," + again.G + "," + again.B);
            }

            // ⑥ 托盘菜单可以独立构建且非空
            using (var menu = new System.Windows.Forms.ContextMenuStrip())
            {
                int n = Tray.TrayController.BuildMenuForTest(app, menu);
                True("托盘菜单项非空", n >= 6, "项数 " + n);
                bool hasStart = false, hasQuit = false, hasMinimal = false;
                foreach (System.Windows.Forms.ToolStripItem it in menu.Items)
                {
                    if (it.Text == I18n.T("menu.start")) hasStart = true;
                    if (it.Text == I18n.T("menu.quit")) hasQuit = true;
                    if (it.Text == I18n.T("menu.minimal")) hasMinimal = true;
                }
                True("含开始项", hasStart);
                True("含退出项", hasQuit);
                True("含极简模式项", hasMinimal);
            }
        }
        /// <summary>自定挡位与默认项的回归测试。</summary>
        private static void TestCustomAndDefaults()
        {
            Group("自定与默认项");
            var app = new AppState();
            app.Data = new AppData();
            Store.Normalize(app.Data);
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.SetPresetMinutes(25, "25");

            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                ui.Draw(g);

                // ① 输入自定时间：不新增挡位、栏内保留数字
                RectangleF chip;
                True("找到自定栏位", ui.TryGetHotspot("presetCustom", out chip));
                float chipW0 = chip.Width;
                ui.ClickAt(Center(chip));
                ui.TypeDigits("45");
                ui.Draw(g);                                  // 让环重新绘制一次
                Eq("输入时环跟随变化", ui.PreviewMinutes, 45);
                ui.OnKey(Keys.Enter);
                Eq("自定值已记住", app.Data.Settings.CustomMinutes, 45);
                Eq("没有新增挡位", app.Data.Settings.CustomPresets.Count, 0);
                Eq("档位已设为 45 分钟", app.Timer.PlannedSeconds, 2700);
                ui.Draw(g);
                RectangleF chip2;
                ui.TryGetHotspot("presetCustom", out chip2);
                True("自定栏位变宽以容纳数字", chip2.Width > chipW0, chipW0 + " -> " + chip2.Width);
                True("未输入时环回到当前档位", ui.PreviewMinutes == 45);

                // ③ 默认主题与默认音效
                True("存在默认主题", Rewards.ById("th_fresh") != null);
                True("存在默认音效", Rewards.ById("sn_default") != null);
                True("默认主题已拥有", app.Data.Rewards.IsOwned("th_fresh"));
                True("默认音效已拥有", app.Data.Rewards.IsOwned("sn_default"));
                Eq("默认音效已装备", app.Data.Rewards.EquippedSound, "sn_default");
                Eq("默认主题已装备", app.Data.Rewards.EquippedTheme, "th_fresh");

                // 点击已装备项不再取消装备
                app.Data.Rewards.Owned.Add("th_sakura");
                Rewards.Equip(app.Data, Rewards.ById("th_sakura"));
                ui.SetDrawer("menu");
                ui.SetMenuTab("rewards");
                ui.SetRewardCategory("theme");
                Settle(ui, g, 20);
                RectangleF btn;
                True("找到已装备主题按钮", ui.TryGetHotspot("rwth_sakura", out btn));
                ui.ClickAt(Center(btn));
                Eq("再次点击不会取消装备", app.Data.Rewards.EquippedTheme, "th_sakura");
                ui.SetDrawer("");

                // ④ 抽屉自动滚动 + 长按确认
                ui.SetDrawer("menu");
                ui.SetMenuTab("settings");
                Settle(ui, g, 20);
                ui.OnMouseMove(new PointF(800, 400), true);
                for (int i = 0; i < 80; i++) ui.OnMouseWheel(-120);   // 先滚到危险操作区
                Settle(ui, g, 10);
                True("找到清除数据按钮", ui.TryGetHotspot("btnClear", out btn));
                float before = ui.DrawerScroll;
                ui.ClickAt(Center(btn));
                for (int i = 0; i < 60; i++) { ui.Draw(g); ui.Update(1.0 / 60.0, 1.0 / 60.0); }
                True("点击后自动向下滚动", ui.DrawerScroll > before, before + " -> " + ui.DrawerScroll);
                True("滚动到内容底部", Math.Abs(ui.DrawerScroll - ui.DrawerMaxScroll) < 2f);

                // 一级确认 → 长按按钮出现
                Settle(ui, g, 10);
                True("找到继续按钮", ui.TryGetHotspot("clearYes", out btn));
                ui.ClickAt(Center(btn));
                Settle(ui, g, 10);
                RectangleF hold;
                True("出现长按按钮", ui.TryGetHotspot("clearHold", out hold));

                // 长按推进
                ui.OnMouseDown(Center(hold));
                for (int i = 0; i < 30; i++) ui.Update(1.0 / 60.0, 1.0 / 60.0);
                float mid = ui.HoldProgress;
                True("按住时进度推进", mid > 0.2f, "进度 " + mid.ToString("0.00"));
                // 松手回收
                ui.OnMouseUp(Center(hold));
                for (int i = 0; i < 40; i++) ui.Update(1.0 / 60.0, 1.0 / 60.0);
                Eq("松手后进度归零", (int)(ui.HoldProgress * 100f), 0);
                True("未长按完不会清除数据", app.Data.Settings.CustomMinutes == 45);

                // 长按到满 → 执行清除
                app.DebugAddTomatoes(4);
                ui.Draw(g);
                ui.TryGetHotspot("clearHold", out hold);
                ui.OnMouseDown(Center(hold));
                for (int i = 0; i < 100; i++) { ui.Draw(g); ui.Update(1.0 / 60.0, 1.0 / 60.0); }
                ui.OnMouseUp(Center(hold));
                Eq("长按完成后数据被清除", app.Data.Sessions.Count, 0);
                Eq("设置仍然保留", app.Data.Settings.CustomMinutes, 45);
            }
        }

        /// <summary>长文案折行不应超出给定宽度。</summary>
        private static void TestTextWrapping()
        {
            Group("文案折行");
            using (var bmp = new Bitmap(200, 200))
            using (var g = Graphics.FromImage(bmp))
            {
                var pt = new Painter(g, 1f, Theme.ById("fresh"));
                var font = pt.F(11f);
                string text = I18n.T("settings.clearData.confirm1");
                var lines = pt.WrapLines(text, font, 200f);
                True("长文案会被折成多行", lines.Count >= 2, "行数 " + lines.Count);
                bool overflow = false;
                foreach (var line in lines)
                    if (pt.Measure(line, font).Width > 202f) overflow = true;
                True("每行宽度都不超限", !overflow);
                string joined = string.Join("", lines.ToArray());
                Eq("折行不丢字符", joined, text);
            }
        }
        /// <summary>顶栏栏位与试听试看的回归测试。</summary>
        private static void TestTopBarAndPreviews()
        {
            Group("栏位与试听试看");
            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.SetPresetMinutes(25, "25");

            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);

                // 先量出左侧标题文本块的右边界（取样区止于栏位左边界，避免把栏位自身算进去）
                ui.Draw(g);
                RectangleF medal, title;
                True("存在奖章栏位", ui.TryGetHotspot("slotMedal", out medal));
                True("存在称号栏位", ui.TryGetHotspot("slotTitle", out title));
                var blockRect = new RectangleF(58, 8, Math.Max(1f, medal.Left - 62f), 44);
                var blockInk = InkBoundsOf(bmp, blockRect, app.CurrentTheme.Surface);
                float blockRight = blockInk.IsEmpty ? 58f : blockInk.Right;

                // ① 两个栏位都不装备时也存在，且位置固定在文本块右侧
                True("栏位不与应用标题重叠", medal.Left >= blockRight + 8f,
                    "栏位左=" + medal.Left.ToString("0") + " 文本右=" + blockRight.ToString("0"));
                True("两个栏位互不重叠", medal.Right <= title.Left);

                // ② 两个栏位可同时显示，且位置不随对方是否存在而漂移
                float titleLeftBefore = title.Left;
                app.Data.Rewards.Owned.Add("m_gold");
                app.Data.Rewards.Owned.Add("t_farmer");
                Rewards.Equip(app.Data, Rewards.ById("m_gold"));
                Rewards.Equip(app.Data, Rewards.ById("t_farmer"));
                ui.Draw(g);
                RectangleF medal2, title2;
                True("装备后奖章栏位仍在", ui.TryGetHotspot("slotMedal", out medal2));
                True("装备后称号栏位仍在", ui.TryGetHotspot("slotTitle", out title2));
                Eq("称号栏位位置固定", (int)title2.Left, (int)titleLeftBefore);
                True("装备后仍不与应用标题重叠", medal2.Left >= blockRight + 8f);

                // ③ 装备提示音 → 立即试听
                app.Data.Rewards.Owned.Add("sn_soft");
                Sound.LastPlayedId = null;
                ui.SetDrawer("menu");
                ui.SetMenuTab("rewards");
                ui.SetRewardCategory("sound");
                Settle(ui, g, 20);
                RectangleF btn;
                if (ui.TryGetHotspot("rwsn_soft", out btn))
                {
                    ui.ClickAt(Center(btn));
                    Eq("装备提示音后触发试听", Sound.LastPlayedId, "soft");
                }
                else True("找到提示音装备按钮", false);

                // ④ 装备动效 → 立即播放动效
                app.Data.Rewards.Owned.Add("ef_confetti");
                ui.SetRewardCategory("effect");
                Settle(ui, g, 20);
                if (ui.TryGetHotspot("rwef_confetti", out btn))
                {
                    int before = ui.ParticleCount;
                    ui.ClickAt(Center(btn));
                    True("装备动效后立即播放", ui.ParticleCount > before,
                        before + " -> " + ui.ParticleCount);
                }
                else True("找到动效装备按钮", false);
                ui.SetDrawer("");

                // ⑤ 设置里打开声音开关 → 试听一次
                app.Data.Settings.Sound = false;
                ui.SetDrawer("menu");
                ui.SetMenuTab("settings");
                Settle(ui, g, 20);
                Sound.LastPlayedId = null;
                if (ui.TryGetHotspot("tgSound", out btn))
                {
                    ui.ClickAt(Center(btn));
                    True("打开音效开关会试听", Sound.LastPlayedId != null, "音色=" + Sound.LastPlayedId);
                }
                else True("找到音效开关", false);

                // ⑥ 抽屉内空白区域点击不应关闭抽屉
                RectangleF panel;
                True("找到面板热区", ui.TryGetHotspot("drawerPanel", out panel));
                ui.ClickAt(new PointF(panel.Left + 10f, 35f));           // 标题栏左侧空白
                Eq("点击标题栏空白不关闭抽屉", ui.CurrentDrawer, "menu");
                ui.ClickAt(new PointF(panel.Left + 6f, panel.Bottom - 6f));  // 左下内边距
                Eq("点击左下内边距不关闭抽屉", ui.CurrentDrawer, "menu");
                ui.ClickAt(new PointF(200, 400));                        // 面板外的遮罩
                Eq("点击面板外仍可关闭", ui.CurrentDrawer, "");
            }
        }

        /// <summary>本轮四项改善的回归测试。</summary>
        private static void TestPolishRound()
        {
            Group("界面打磨回归");
            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.SetPresetMinutes(25, "25");

            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);

                // ① 滚轮到界后不应继续变化（消除回弹）
                ui.SetDrawer("menu");
                ui.SetMenuTab("achievements");
                Settle(ui, g, 30);
                ui.OnMouseMove(new PointF(800, 400), true);
                for (int i = 0; i < 80; i++) ui.OnMouseWheel(-120);   // 远超内容高度
                float bottom = ui.DrawerScroll;
                True("能滚到底", bottom > 0f);
                Eq("已到底", (int)bottom, (int)ui.DrawerMaxScroll);
                ui.OnMouseWheel(-120);
                ui.OnMouseWheel(-120);
                Eq("到底后再滚不再变化", (int)ui.DrawerScroll, (int)bottom);
                ui.OnMouseWheel(120);
                True("还能向上滚回", ui.DrawerScroll < bottom);

                // ② 成就行的描述与进度条之间必须有空白间隔
                ui.SetDrawer("menu");
                ui.SetMenuTab("achievements");
                Settle(ui, g, 30);
                RectangleF bodyRect;
                True("找到内容区", ui.TryGetHotspot("drawerBg", out bodyRect));
                // 第一行位于内容区顶部说明文字之下
                var bands = InkBands(bmp, (int)bodyRect.Left + 70, (int)bodyRect.Right - 20,
                                     (int)bodyRect.Top + 30, (int)bodyRect.Top + 96, 230);
                True("第一行至少有 3 条墨迹带（名称/描述/进度条）", bands.Count >= 3, "条数 " + bands.Count);
                if (bands.Count >= 3)
                {
                    float gap = bands[2].Top - bands[1].Bottom;
                    True("描述与进度条之间留有间隔", gap >= 4f, "间隔 " + gap.ToString("0.0") + "px");
                }

                // ③ 今日卡片：保留 x/1.0 与进度条，移除右侧「碎片」文字
                ui.SetDrawer("");
                Settle(ui, g, 10);
                var card = new RectangleF(616, 80, 368, 96);
                var line2 = new RectangleF(card.Left + 18, card.Top + 46, card.Width - 36, 16);
                bool leftInk = !InkBoundsOf(bmp, new RectangleF(line2.Left, line2.Top, 120, 16), app.CurrentTheme.Surface).IsEmpty;
                bool rightInk = !InkBoundsOf(bmp, new RectangleF(line2.Left + 200, line2.Top, 150, 16), app.CurrentTheme.Surface).IsEmpty;
                True("保留左侧 x/1.0", leftInk);
                True("右侧「碎片」文字已移除", !rightInk);

                // ④ 自定时间气泡为浅色底
                RectangleF chip;
                if (ui.TryGetHotspot("presetCustom", out chip))
                {
                    ui.ClickAt(new PointF(chip.Left + chip.Width / 2f, chip.Top + chip.Height / 2f));
                    Settle(ui, g, 40);
                    // 气泡上内边距那一行（文字上方）应当是一段连续的浅色
                    int y = (int)(chip.Top - 38f);
                    int run = 0, best = 0;
                    for (int x = (int)(chip.Left - 40); x < (int)(chip.Right + 40); x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (c.R > 200 && c.G > 200 && c.B > 200) { run++; if (run > best) best = run; }
                        else run = 0;
                    }
                    True("气泡为浅色底", best >= 30, "最长浅色连续像素 " + best);
                }
                else True("找到自定档位", false);
            }
        }

        /// <summary>
        /// 按行扫描某区域，返回连续的"墨迹"纵向带。
        /// 判定标准：任一通道暗于 maxChannel（这样能同时识别文字、图标与进度条轨道，
        /// 而不会把卡片底色误判为墨迹）。
        /// </summary>
        private static List<RectangleF> InkBands(Bitmap bmp, int x0, int x1, int y0, int y1, int maxChannel)
        {
            var bands = new List<RectangleF>();
            bool inBand = false;
            int start = 0;
            for (int y = y0; y <= y1; y++)
            {
                bool ink = false;
                for (int x = x0; x < x1; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.R < maxChannel || c.G < maxChannel || c.B < maxChannel) { ink = true; break; }
                }
                if (ink && !inBand) { inBand = true; start = y; }
                else if (!ink && inBand) { inBand = false; bands.Add(new RectangleF(0, start, 1, y - start)); }
            }
            if (inBand) bands.Add(new RectangleF(0, start, 1, y1 - start + 1));
            return bands;
        }
        /// <summary>本轮三项改善的回归测试。</summary>
        private static void TestDebugAndClearData()
        {
            Group("调试与清除数据");
            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Data.Settings.ThemeId = "night";

            // ② 清除数据：两级确认流程
            app.DebugAddTomatoes(3);
            Eq("调试 +3 颗", app.Data.Wallet.UsableWhole, 3);
            app.DebugUnlockAllAchievements();
            Eq("成就全部解锁", Achievements.UnlockedCount(app.Data), Achievements.All.Count);
            app.DebugUnlockAllRewards();
            Eq("奖励全部解锁", app.Data.Rewards.Owned.Count, Rewards.All.Count);

            app.ClearAllData();
            Eq("清除后无会话", app.Data.Sessions.Count, 0);
            Eq("清除后钱包归零", app.Data.Wallet.TotalTenths, 0);
            Eq("清除后无成就", Achievements.UnlockedCount(app.Data), 0);
            Eq("清除后无奖励", app.Data.Rewards.Owned.Count, 0);
            Eq("清除后日聚合为空", app.Data.Days.Count, 0);
            Eq("设置被保留", app.Data.Settings.ThemeId, "night");

            // ③ 立即完成计时：走正常结算路径
            app.Timer.StartFocus(25, "25");
            app.DebugCompleteTimer();
            Eq("计时已结束", app.Timer.Phase, TimerPhase.Idle);
            Eq("完成计入一颗", app.Data.Wallet.UsableWhole, 1);

            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);

                // ③ 番茄 5 连击打开调试面板（4 次不打开）
                ui.Draw(g);
                RectangleF tomato;
                True("找到番茄图标热区", ui.TryGetHotspot("tomato", out tomato));
                for (int i = 0; i < 4; i++) { ui.ClickAt(Center(tomato)); ui.Update(1.0 / 60.0); }
                True("连点 4 次不打开", !ui.IsDebugPanelOpen);
                ui.ClickAt(Center(tomato));
                True("连点 5 次打开调试面板", ui.IsDebugPanelOpen);
                Settle(ui, g, 10);
                RectangleF dbg;
                True("面板含 +10 按钮", ui.TryGetHotspot("dbgAdd", out dbg));
                True("面板含解锁成就按钮", ui.TryGetHotspot("dbgAch", out dbg));
                True("面板含解锁奖励按钮", ui.TryGetHotspot("dbgRw", out dbg));
                True("面板含完成计时按钮", ui.TryGetHotspot("dbgFinish", out dbg));

                // 面板拦截背后点击：点遮罩即关闭
                ui.ClickAt(new PointF(40, 690));
                True("点击遮罩关闭面板", !ui.IsDebugPanelOpen);

                // ① 合并瞬间不画「结束」文字
                app.DismissReminder();          // 清掉上一步调试完成计时弹出的提醒卡片，避免遮挡取样区
                app.Timer.StartFocus(25, "25");
                Settle(ui, g, 40);
                app.Timer.Reset();
                ui.Draw(g);
                ui.Update(1.0 / 60.0);
                ui.Draw(g);
                RectangleF stop, primary;
                if (ui.TryGetHotspot("btnStop", out stop) && ui.TryGetHotspot("btnPrimary", out primary))
                {
                    // 合并刚开始：按钮仍有一定宽度，但文字必须已清除
                    // （只统计墨迹像素数：圆角抗锯齿只有几十个像素，文字会有上百个）
                    var region = new RectangleF(stop.Left + 6f, stop.Top + 6f, Math.Max(4f, stop.Width - 12f), stop.Height - 12f);
                    int inkCount = 0;
                    for (int x = (int)region.Left; x < region.Right; x++)
                        for (int y = (int)region.Top; y < region.Bottom; y++)
                        {
                            var c = bmp.GetPixel(x, y);
                            if (Math.Abs(c.R - app.CurrentTheme.Bg.R) > 10 || Math.Abs(c.G - app.CurrentTheme.Bg.G) > 10 || Math.Abs(c.B - app.CurrentTheme.Bg.B) > 10) inkCount++;
                        }
                    True("合并瞬间按钮内无文字墨迹", inkCount < 60, "墨迹像素 " + inkCount);
                }
                else True("合并起始帧仍能找到两个按钮", false);
            }
        }
        /// <summary>极简模式开关必须真的通知外壳。</summary>
        private static void TestMinimalModeWiring()
        {
            Group("极简模式接线");
            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(I18n.DefaultLang);

            int fired = 0;
            app.MinimalModeChanged += delegate { fired++; };
            app.SetMinimalMode(true);
            Eq("开启时触发事件", fired, 1);
            True("设置已写入", app.Data.Settings.MinimalMode);
            app.SetMinimalMode(true);
            Eq("重复开启不重复触发", fired, 1);
            app.SetMinimalMode(false);
            Eq("关闭时也触发", fired, 2);
            True("关闭后状态正确", !app.Data.Settings.MinimalMode);
        }

        /// <summary>开启「隐藏倒计时」后，环心不应再有倒计时数字。</summary>
        private static void TestHideCountdown()
        {
            Group("隐藏倒计时");
            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            app.Timer.StartFocus(25, "25");

            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                ui.Draw(g);                                  // 先画一帧，布局与 RingCenter 才有值
                var center = new RectangleF(ui.RingCenter.X - 45f, ui.RingCenter.Y - 30f, 90f, 60f);

                Settle(ui, g, 20);
                var withClock = InkBoundsOf(bmp, center, app.CurrentTheme.Bg);
                True("默认会显示倒计时", !withClock.IsEmpty && withClock.Height > 40f,
                    "墨迹高 " + withClock.Height.ToString("0"));

                app.Data.Settings.HideCountdown = true;
                Settle(ui, g, 20);
                var hidden = InkBoundsOf(bmp, center, app.CurrentTheme.Bg);
                True("隐藏后环心只剩状态文字", hidden.IsEmpty || hidden.Height < 40f,
                    "墨迹高 " + hidden.Height.ToString("0"));

                // 暂停态同样不显示时间
                app.Timer.Pause();
                Settle(ui, g, 20);
                var pausedInk = InkBoundsOf(bmp, center, app.CurrentTheme.Bg);
                True("暂停时也不显示时间", pausedInk.IsEmpty || pausedInk.Height < 40f,
                    "墨迹高 " + pausedInk.Height.ToString("0"));
            }
        }

        /// <summary>帧时间策略：空闲降帧时动画不能"一跳到底"。</summary>
        private static void TestFrameTiming()
        {
            Group("帧时间策略");
            Eq("250ms 帧间隔被钳制到 1/30", FrameTiming.ClampAnimationDelta(0.25), 1.0 / 30.0);
            Eq("16ms 帧间隔保持原值", FrameTiming.ClampAnimationDelta(0.016), 0.016);
            Eq("0 间隔回退到 1/120", FrameTiming.ClampAnimationDelta(0), 1.0 / 120.0);
            True("单帧最大推进不超过 33ms", FrameTiming.MaxAnimationStep <= 0.0334);

            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(I18n.DefaultLang);
            app.CurrentTheme = Theme.ById("fresh");
            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                ui.Draw(g);
                ui.TakeDirty();
                RectangleF r;
                ui.TryGetHotspot("menu", out r);
                ui.ClickAt(Center(r));
                True("点击后进入高帧率爆发期", ui.InteractionBurst > 0);
                True("点击后立即请求重绘", ui.TakeDirty());
                ui.Update(1.0 / 60.0, 1.0 / 60.0);
                True("爆发计时会递减", ui.InteractionBurst < FrameTiming.InteractionBurstSeconds);
            }
        }

        private static void TestRenderTomato()
        {
            Group("程序化渲染");
            using (var bmp = new Bitmap(200, 200, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                var pt = new Painter(g, 1f, Theme.ById("fresh"));
                TomatoArt.DrawTomato(pt, new RectangleF(20, 20, 160, 160), 1f, true, pt.T.Accent, pt.T.AccentDark, pt.T.Leaf);

                var center = bmp.GetPixel(100, 110);
                True("番茄中心为暖色", center.R > 150 && center.R > center.B && center.R > center.G,
                    "RGB=" + center.R + "," + center.G + "," + center.B);
                var corner = bmp.GetPixel(2, 2);
                True("背景未被污染", corner.R > 240 && corner.G > 240 && corner.B > 240);

                // 0 饱满度时不应出现番茄红
                using (var bmp2 = new Bitmap(200, 200, PixelFormat.Format32bppArgb))
                using (var g2 = Graphics.FromImage(bmp2))
                {
                    g2.Clear(Color.White);
                    var pt2 = new Painter(g2, 1f, Theme.ById("fresh"));
                    TomatoArt.DrawTenths(pt2, new RectangleF(20, 20, 160, 160), 0, false);
                    var c2 = bmp2.GetPixel(100, 120);
                    True("0 格时是暗底", c2.R < 230, "RGB=" + c2.R + "," + c2.G + "," + c2.B);
                }
            }
        }

        private static void TestRenderUiSnapshot()
        {
            Group("界面渲染");
            var app = new AppState();
            app.Data = new AppData();
            I18n.Load(app.Data.Settings.Lang);
            app.CurrentTheme = Theme.ById("fresh");
            using (var bmp = new Bitmap(1040, 700, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, 1040, 700);
                for (int i = 0; i < 30; i++) ui.Update(1.0 / 60.0);
                ui.Draw(g);
                int nonBg = 0;
                for (int x = 0; x < 1040; x += 13)
                    for (int y = 0; y < 700; y += 13)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (Math.Abs(c.R - app.CurrentTheme.Bg.R) > 6 || Math.Abs(c.G - app.CurrentTheme.Bg.G) > 6 || Math.Abs(c.B - app.CurrentTheme.Bg.B) > 6) nonBg++;
                    }
                True("界面已绘制内容", nonBg > 300, "非背景采样点 " + nonBg);
            }
        }

        private static void TestTrayIcon()
        {
            Group("托盘图标");
            foreach (TimerPhase phase in new[] { TimerPhase.Idle, TimerPhase.Focusing, TimerPhase.Paused, TimerPhase.Break })
            {
                using (var bmp = TrayIconArt.Render(32, phase, 0.5, Theme.ById("fresh")))
                {
                    bool opaque = false;
                    for (int x = 0; x < 32 && !opaque; x++)
                        for (int y = 0; y < 32 && !opaque; y++)
                            if (bmp.GetPixel(x, y).A > 40) opaque = true;
                    True("状态图标有内容 " + phase, opaque);
                }
            }
            var idle = TrayIconArt.StateColor(TimerPhase.Idle, Theme.ById("fresh"));
            var running = TrayIconArt.StateColor(TimerPhase.Focusing, Theme.ById("fresh"));
            var paused = TrayIconArt.StateColor(TimerPhase.Paused, Theme.ById("fresh"));
            True("未运行与专注中颜色不同", idle.ToArgb() != running.ToArgb());
            True("暂停色不同于专注色", paused.ToArgb() != running.ToArgb());

            using (var bmp = TrayIconArt.Render(32, TimerPhase.Focusing, 0.5, Theme.ById("fresh")))
            {
                var icon = IconFactory.FromBitmap(bmp);
                True("ICO 打包成功", icon != null && icon.Width == 32);
                if (icon != null) icon.Dispose();
            }
        }

        private static void TestAssetRegistry()
        {
            Group("图片接口");
            var reg = TomatoFocus.Assets.AssetRegistry.Load();
            True("缺失资源时回退程序化绘制", reg.Get("icon.tomato") == null);
            True("槽位清单非空", reg.SlotCount > 0);

            // 放入图片后应被加载（约定：assets/<槽位>.png）
            string dir = AppPaths.AssetsDir;
            string file = Path.Combine(dir, "icon.tomato.png");
            try
            {
                Directory.CreateDirectory(dir);
                using (var b = new Bitmap(12, 12))
                {
                    for (int x = 0; x < 12; x++)
                        for (int y = 0; y < 12; y++)
                            b.SetPixel(x, y, Color.FromArgb(255, 10, 200, 120));
                    b.Save(file, ImageFormat.Png);
                }
                var reg2 = TomatoFocus.Assets.AssetRegistry.Load();
                var img = reg2.Get("icon.tomato");
                True("放入图片后应加载", img != null && img.Width == 12);
                reg2.Dispose();
            }
            finally
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
            reg.Dispose();
        }

        /// <summary>文案键完整性：界面不应出现未翻译的键名。</summary>
        private static void TestLangKeys()
        {
            Group("文案键完整性");
            foreach (var def in Achievements.All)
            {
                True("成就名 " + def.Id, I18n.T(def.NameKey) != def.NameKey);
                True("成就描述 " + def.Id, I18n.T(def.DescKey) != def.DescKey);
            }
            foreach (var def in Rewards.All)
            {
                True("奖励名 " + def.Id, I18n.T(def.NameKey) != def.NameKey);
                True("奖励描述 " + def.Id, I18n.T(def.DescKey) != def.DescKey);
            }
            foreach (string cat in new[] { "title", "medal", "theme", "effect", "sound" })
                True("奖励分类 " + cat, I18n.T("reward.cat." + cat) != "reward.cat." + cat);

            string[] required =
            {
                "app.title", "focus.start", "focus.pause", "focus.resume", "focus.reset", "focus.custom",
                "focus.stop.confirm", "focus.stop.estimate", "focus.stop.none",
                "wallet.usable", "wallet.fragment", "wallet.fragmentHint",
                "today.tomatoes", "today.minutes", "today.empty",
                "cal.today", "cal.title", "cal.dayDetail", "cal.noRecord", "cal.preset", "cal.focused", "cal.aborted",
                "ach.title", "ach.progress", "ach.new",
                "reward.title", "reward.available", "reward.redeem", "reward.owned", "reward.equipped",
                "settings.title", "settings.autoStart", "settings.minimalMode", "settings.closeToTray",
                "tray.tip.idle", "tray.tip.running", "tray.tip.paused", "tray.tip.breaking",
                "menu.start", "menu.pause", "menu.resume", "menu.stop", "menu.preset", "menu.showWindow",
                "menu.minimal", "menu.autoStart", "menu.todayInfo", "menu.quit",
                "break.title", "break.take", "break.skip", "break.postpone", "break.done", "break.water", "break.eye",
                "health.rule.sedentary", "health.rule.water", "health.rule.eye",
                "health.source.who", "health.source.cn", "health.source.diet", "health.source.aoa"
            };
            foreach (string key in required)
                True("必备文案 " + key, I18n.T(key) != key);
        }
    }
}

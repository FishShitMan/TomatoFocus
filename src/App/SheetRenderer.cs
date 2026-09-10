using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using TomatoFocus.Core;
using TomatoFocus.Render;
using TomatoFocus.Ui;

namespace TomatoFocus.App
{
    /// <summary>
    /// 离屏渲染 UI 快照（用于人工验收与视觉回归，不弹窗）。
    /// 用法: TomatoFocus.exe --sheet &lt;输出目录&gt;
    /// </summary>
    internal static class SheetRenderer
    {
        public static int Render(string outDir)
        {
            Directory.CreateDirectory(outDir);
            I18n.Load(I18n.DefaultLang);

            var app = new AppState();
            app.Data = SampleData();
            I18n.Load(app.Data.Settings.Lang);
            app.CurrentTheme = Theme.ById(app.Data.Settings.ThemeId);
            app.Timer.SetPresetMinutes(25, "25");

            int count = 0;
            count += Shot(app, Path.Combine(outDir, "01-idle.png"), delegate { });
            count += Shot(app, Path.Combine(outDir, "02-running.png"), delegate
            {
                app.Timer.StartFocus(25, "25");
            });
            count += Shot(app, Path.Combine(outDir, "03-calendar-day.png"), delegate
            {
                LastUi.SetDrawer("day", DayKey.Today);
            });
            count += Shot(app, Path.Combine(outDir, "04-achievements.png"), delegate
            {
                LastUi.SetDrawer("menu");
                LastUi.SetMenuTab("achievements");
            });
            count += Shot(app, Path.Combine(outDir, "05-rewards.png"), delegate
            {
                LastUi.SetDrawer("menu");
                LastUi.SetMenuTab("rewards");
                LastUi.SetRewardCategory("title");
            });
            count += Shot(app, Path.Combine(outDir, "06-settings.png"), delegate
            {
                LastUi.SetDrawer("menu");
                LastUi.SetMenuTab("settings");
            });
            count += Shot(app, Path.Combine(outDir, "07-confirm.png"), delegate
            {
                app.Timer.StartFocus(25, "25");
                LastUi.SetDrawer("");
                LastUi.SetConfirmStop(true);
            });
            count += Shot(app, Path.Combine(outDir, "08-reminder.png"), delegate
            {
                LastUi.SetConfirmStop(false);
                app.Pending = new Reminder();
                app.Pending.Phrase = HealthRules.NextPhrase(ref _phraseIdx);
                app.Pending.BreakSeconds = 300;
            });
            count += Shot(app, Path.Combine(outDir, "09-night-theme.png"), delegate
            {
                app.Pending = null;
                app.Data.Settings.ThemeId = "night";
                app.CurrentTheme = Theme.ById("night");
            });
            count += Shot(app, Path.Combine(outDir, "10-year-view.png"), delegate
            {
                app.Data.Settings.ThemeId = "fresh";
                app.CurrentTheme = Theme.ById("fresh");
                LastUi.SetDrawer("");
                LastUi.SetYearView(true);
            });
            count += Shot(app, Path.Combine(outDir, "11-sakura.png"), delegate
            {
                LastUi.SetYearView(false);
                app.Data.Settings.ThemeId = "sakura";
                app.CurrentTheme = Theme.ById("sakura");
                LastUi.SetDrawer("menu");
                LastUi.SetMenuTab("rewards");
                LastUi.SetRewardCategory("medal");
            });
            count += Shot(app, Path.Combine(outDir, "12-custom-input.png"), delegate
            {
                app.Data.Settings.ThemeId = "fresh";
                app.CurrentTheme = Theme.ById("fresh");
                LastUi.SetDrawer("");
                app.Timer.Reset();                 // 自定输入仅在空闲态可用
                RectangleF r;
                if (LastUi.TryGetHotspot("presetCustom", out r))
                    LastUi.ClickAt(new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f));
            });
            count += Shot(app, Path.Combine(outDir, "13-hide-clock.png"), delegate
            {
                app.Data.Settings.HideCountdown = true;
                app.Timer.StartFocus(25, "25");
                LastUi.SetDrawer("");
            });
            count += Shot(app, Path.Combine(outDir, "14-debug-panel.png"), delegate
            {
                app.Data.Settings.HideCountdown = false;
                app.Timer.Reset();
                app.DismissReminder();
                LastUi.SetDrawer("");
                RectangleF t;
                if (LastUi.TryGetHotspot("tomato", out t))
                    for (int i = 0; i < 5; i++)
                        LastUi.ClickAt(new PointF(t.Left + t.Width / 2f, t.Top + t.Height / 2f));
            });
            count += Shot(app, Path.Combine(outDir, "15-clear-data.png"), delegate
            {
                LastUi.SetDrawer("menu");
                LastUi.SetMenuTab("settings");
                LastUi.SetClearStage(1);                         // 一级确认卡片
                LastUi.Draw(LastGraphics);
                LastUi.OnMouseMove(new PointF(800, 400), true);
                for (int i = 0; i < 60; i++) LastUi.OnMouseWheel(-120);   // 滚到危险操作区
            });
            count += Shot(app, Path.Combine(outDir, "16-clear-hold.png"), delegate            {
                LastUi.SetDrawer("menu");
                LastUi.SetMenuTab("settings");
                LastUi.SetClearStage(2);                         // 二级：长按确认
                LastUi.Draw(LastGraphics);
                LastUi.OnMouseMove(new PointF(800, 400), true);
                for (int i = 0; i < 60; i++) LastUi.OnMouseWheel(-120);
                LastUi.Draw(LastGraphics);
                RectangleF hold;
                if (LastUi.TryGetHotspot("clearHold", out hold))
                {
                    var hp = new PointF(hold.Left + hold.Width / 2f, hold.Top + hold.Height / 2f);
                    LastUi.OnMouseDown(hp);
                    for (int i = 0; i < 40; i++) LastUi.Update(1.0 / 60.0, 1.0 / 60.0);   // 长按到约 55%
                    LastUi.OnMouseUp(hp);   // 必须松手：否则后续收敛帧会继续计时并把样本数据真的清掉
                }
            });
            count += Shot(app, Path.Combine(outDir, "17-break-mode.png"), delegate
            {
                LastUi.SetDrawer("");
                app.Timer.Reset();
                app.DismissReminder();
                LastUi.Draw(LastGraphics);
                RectangleF seg;
                if (LastUi.TryGetHotspot("modeBreak", out seg))
                    LastUi.ClickAt(new PointF(seg.Left + seg.Width / 2f, seg.Top + seg.Height / 2f));
            });
            // 日历格悬停：实心色块 + 文字/番茄反色剪影
            count += Shot(app, Path.Combine(outDir, "18-calendar-hover.png"), delegate
            {
                LastUi.SetDrawer("");
                app.Timer.Reset();
                LastUi.Draw(LastGraphics);
                RectangleF cell;
                if (LastUi.TryGetHotspot("day" + DayKey.Today, out cell))
                    LastUi.OnMouseMove(new PointF(cell.Left + cell.Width / 2f, cell.Top + cell.Height / 2f), true);
            });
            // 自定输入态：尚未输入时时间环显示 00:00，此时点「开始」不动作
            count += Shot(app, Path.Combine(outDir, "19-custom-empty.png"), delegate
            {
                LastUi.SetDrawer("");
                app.Timer.Reset();
                LastUi.Draw(LastGraphics);
                ClickChip(LastUi, "presetCustom");
            });
            // 自定输入态：输入数字后时间环实时同步，回车或点开始即开始倒计时
            count += Shot(app, Path.Combine(outDir, "20-custom-typed.png"), delegate
            {
                LastUi.SetDrawer("");
                app.Timer.Reset();
                LastUi.Draw(LastGraphics);
                if (ClickChip(LastUi, "presetCustom")) LastUi.TypeDigits("40");
            });
            return count;
        }

        private static bool ClickChip(UiRoot ui, string id)
        {
            RectangleF r;
            if (!ui.TryGetHotspot(id, out r)) return false;
            ui.ClickAt(new PointF(r.Left + r.Width / 2f, r.Top + r.Height / 2f));
            return true;
        }

        private static int _phraseIdx = -1;
        private static UiRoot LastUi;
        private static Graphics LastGraphics;

        private static int Shot(AppState app, string path, Action setup)
        {
            const int W = 1040, H = 700;
            using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                var ui = new UiRoot(app);
                LastUi = ui;
                LastGraphics = g;
                ui.Scale = 1f;
                ui.Bounds = new RectangleF(0, 0, W, H);
                ui.Draw(g);                 // 先画一帧建立热区与动画对象
                setup();
                // 让动画收敛到终态
                for (int i = 0; i < 90; i++) { ui.Update(1.0 / 60.0); ui.Draw(g); }
                ui.Draw(g);
                bmp.Save(path, ImageFormat.Png);
            }
            Console.WriteLine("sheet -> " + path);
            return 1;
        }

        /// <summary>构造一份有代表性的示例数据（仅用于快照，不写入用户数据目录）。</summary>
        private static AppData SampleData()
        {
            var d = new AppData();
            var rng = new Random(20260214);
            var today = DateTime.Now.Date;

            for (int back = 44; back >= 0; back--)
            {
                var day = today.AddDays(-back);
                int sessions = rng.Next(0, 5);
                if (back == 0) sessions = 3;
                for (int i = 0; i < sessions; i++)
                {
                    var rec = new SessionRecord();
                    bool aborted = rng.NextDouble() < 0.18;
                    int planned = 25 * 60;
                    int focused = aborted ? (int)(planned * (0.3 + rng.NextDouble() * 0.6)) : planned;
                    if (rng.NextDouble() < 0.2) { planned = 15 * 60; focused = aborted ? (int)(planned * 0.7) : planned; }
                    if (rng.NextDouble() < 0.12) { planned = 5 * 60; focused = aborted ? (int)(planned * 0.8) : planned; }
                    rec.Id = Guid.NewGuid().ToString("N");
                    rec.StartedAt = DayKey.Stamp(day.AddHours(9 + i * 2).AddMinutes(rng.Next(0, 50)));
                    rec.PlannedSec = planned;
                    rec.FocusedSec = focused;
                    rec.Aborted = aborted;
                    rec.Preset = planned == 1500 ? "25" : planned == 900 ? "15" : "5";
                    rec.Tenths = TomatoMath.TenthsFor(focused, aborted);
                    if (rec.Tenths > 0)
                    {
                        if (rng.NextDouble() < 0.35) rec.Note = "完成了登录模块的重构，明天继续接口对接。";
                        d.Sessions.Add(rec);
                    }
                }
            }

            Store.RebuildDays(d);
            d.Wallet.SpentWhole = Math.Max(0, Math.Min(d.Wallet.UsableWhole, 3));
            d.Rewards.Owned.Add("t_farmer");
            d.Rewards.EquippedTitle = "t_farmer";
            d.Rewards.Owned.Add("th_sakura");
            d.Counters["breaksTaken"] = 23;
            d.Settings.NoteDraft = "先做接口对接，再补单元测试；记得给缓存层加上超时。";
            d.Settings.RewardBadgeSeen = 0;
            Achievements.Evaluate(d, DateTime.Now);
            return d;
        }
    }
}

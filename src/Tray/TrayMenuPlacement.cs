using System.Drawing;

namespace TomatoFocus.Tray
{
    /// <summary>
    /// 托盘右键菜单的定位规则：**以鼠标坐标为原点向 +X（右侧）展开**。
    /// 纯函数，便于单元测试；不依赖任何 WinForms 状态。
    /// </summary>
    internal static class TrayMenuPlacement
    {
        /// <summary>菜单左边缘与鼠标之间的间隙。</summary>
        public const int Gap = 8;

        /// <summary>
        /// 计算菜单左上角位置。
        /// 横向：默认放在鼠标右侧；右侧空间不足时才回退到贴工作区右边缘。
        /// 纵向：与鼠标对齐；下方空间不足时上移（托盘在屏幕底部时的常规表现）。
        /// </summary>
        public static Point Place(Point cursor, Size menu, Rectangle workArea)
        {
            int x = cursor.X + Gap;
            if (x + menu.Width > workArea.Right) x = workArea.Right - menu.Width;
            if (x < workArea.Left) x = workArea.Left;

            int y = cursor.Y;
            if (y + menu.Height > workArea.Bottom) y = workArea.Bottom - menu.Height;
            if (y < workArea.Top) y = workArea.Top;

            return new Point(x, y);
        }
    }
}

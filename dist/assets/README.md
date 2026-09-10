# assets —— 图片替换接口

本程序的所有图形默认由**代码程序化生成**，不依赖任何图片文件。
如果你想换成自己的素材，把文件放进这个目录即可，**无需重新编译**。

## 两种用法

**方式一（推荐）**：文件名 = 槽位名。

```
assets/icon.tomato.png
assets/medal.gold.png
```

**方式二**：在 `manifest.json` 的 `slots` 里声明文件名。

```json
{ "slots": { "icon.tomato": { "file": "我的番茄.png" } } }
```

## 规则

- 支持格式：PNG / JPG / ICO / BMP
- 找不到文件、或文件损坏 → **自动回退**到程序化绘制，不会报错、不会白屏
- 保存文件后自动热重载（无需重启）
- 图片会按比例居中缩放到目标区域，建议使用透明背景 PNG

## 槽位清单

| 槽位 | 用途 |
|---|---|
| `icon.tomato` | 番茄图标（顶栏、按钮、罐子） |
| `icon.leaf` | 叶子/称号徽章 |
| `icon.tray` | 托盘图标（程序化版本按状态变色，放图后将固定使用图片） |
| `icon.btn.start` / `icon.btn.pause` / `icon.btn.stop` / `icon.btn.reset` | 主按钮图标 |
| `icon.nav.prev` / `icon.nav.next` | 日历翻页 |
| `icon.settings` / `icon.achievements` / `icon.rewards` | 顶栏图标 |
| `icon.calendar` / `icon.close` / `icon.minimize` | 其他界面图标 |
| `medal.bronze` / `medal.silver` / `medal.gold` / `medal.rainbow` | 奖章 |
| `title.badge` | 称号徽章 |
| `bg.texture` | 背景纹理 |
| `art.empty` | 空状态插图 |

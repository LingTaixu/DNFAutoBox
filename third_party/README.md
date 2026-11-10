# UI 来源与授权

本程序的像素风组件是对 NES.css 2.3.0 的 WinForms 原生适配，不会在运行时加载网页、CSS 或浏览器。

- NES.css 2.3.0：https://github.com/nostalgic-css/NES.css/tree/v2.3.0
- 原版样式文件保存在 `nes.css/nes.css`，用于记录配色、4px 边框和块状阴影的来源，不参与运行。
- 原版作者：B.C.Rikko，MIT License，见 `nes.css/LICENSE`。
- 原生控件实现在 `src/PixelTheme.cs`，包括像素按钮、下拉选择器、边框面板、状态面板和标题。
- Press Start 2P 字体来源：https://github.com/google/fonts/tree/main/ofl/pressstart2p
- 字体采用 SIL Open Font License 1.1，见 `assets/fonts/OFL-PressStart2P.txt`。
- 字体及其许可证、NES.css 许可证均嵌入 auto-tools.exe，可直接分发单个 EXE。
- 中文使用系统字体，以保证中文窗口标题和控件标签可读；英文标题、数字和功能键使用像素字体。

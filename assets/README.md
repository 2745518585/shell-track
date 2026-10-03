# Shell Track 图标

原创终端提示符与任务轨迹图标，适用于窗口、任务栏和托盘。自有图形与生成脚本采用仓库根目录的 MIT 许可证，不包含第三方字体或图像素材。

- `shelltrack.svg`：可编辑的矢量源。
- `shelltrack.png`：256 × 256 透明背景预览。
- `shelltrack.ico`：16、20、24、32、40、48、64、128、256 像素的透明图标，包含适合 125% 和 150% 缩放的尺寸。
- `generate-icon.ps1`：在 Windows 使用 PowerShell 7 和系统绘图库重新生成 PNG 与 ICO。

```powershell
pwsh -NoProfile -File assets/generate-icon.ps1
```

脚本直接绘制与 SVG 相同的几何形状。修改图案时，同时更新 SVG 和脚本中的坐标、颜色。

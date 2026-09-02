using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace X4Calculator.UI;

public partial class MainWindow : Window
{
    /// <summary>整体布局缩放档位（老年友好放大），Ctrl+=/-/0 切换</summary>
    private static readonly double[] ZoomLevels = { 1.0, 1.15, 1.3, 1.5, 1.75, 2.0 };

    private int _zoomIndex = 0;
    private bool _baseSizeCaptured;
    private Size _baseSize;

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void EnableDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        const int immersiveDarkMode = 20;
        const int immersiveDarkModeBefore20H1 = 19;
        if (DwmSetWindowAttribute(handle, immersiveDarkMode, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, immersiveDarkModeBefore20H1, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    /// <summary>
    /// 启动时若默认窗口尺寸(720x1100 DIP)超出屏幕工作区(如 720p 小屏/高缩放)，
    /// 自动收缩到可用范围，避免窗口超出屏幕。全 DIP 单位，已自动适配系统 DPI。
    /// </summary>
    private void FitToWorkArea()
    {
        var workArea = SystemParameters.WorkArea; // 主显示器工作区(扣除任务栏)，单位 DIP
        const double margin = 16;                 // 与屏幕边缘保留的边距

        double maxWidth = workArea.Width - margin;
        double maxHeight = workArea.Height - margin;

        if (Width > maxWidth)
            Width = maxWidth;
        if (Height > maxHeight)
            Height = maxHeight;
    }

    // ===== Ctrl+加号/减号/0 整体缩放 =====

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 只要按下 Ctrl 即处理（兼容 Ctrl+Shift+= 等输入法组合下的加号键）
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;

        switch (e.Key)
        {
            case Key.OemPlus:      // Ctrl+= 或 Ctrl+（主键盘加号，需按 Shift）
            case Key.Add:          // 小键盘加号
                ApplyZoom(_zoomIndex + 1);
                e.Handled = true;
                break;
            case Key.OemMinus:     // Ctrl+-
            case Key.Subtract:     // 小键盘减号
                ApplyZoom(_zoomIndex - 1);
                e.Handled = true;
                break;
            case Key.D0:           // Ctrl+0
            case Key.NumPad0:
                ApplyZoom(0);
                e.Handled = true;
                break;
        }
    }

    private void ApplyZoom(int newIndex)
    {
        _zoomIndex = Math.Clamp(newIndex, 0, ZoomLevels.Length - 1);
        double factor = ZoomLevels[_zoomIndex];

        ZoomTransform.ScaleX = factor;
        ZoomTransform.ScaleY = factor;

        // 页面右下角显示当前缩放百分比
        if (ZoomText != null)
            ZoomText.Text = $"{factor * 100:0}%";

        SyncWindowSize(factor);
    }

    /// <summary>
    /// 缩放时同步调整窗口尺寸：以用户当前窗口尺寸为基准 × 系数，
    /// 并限制在屏幕工作区内，避免放大后内容被裁剪。
    /// </summary>
    private void SyncWindowSize(double factor)
    {
        if (!_baseSizeCaptured)
        {
            _baseSize = new Size(Width, Height);
            _baseSizeCaptured = true;
        }

        var workArea = SystemParameters.WorkArea;
        const double margin = 16;

        Width = Math.Min(_baseSize.Width * factor, workArea.Width - margin);
        Height = Math.Min(_baseSize.Height * factor, workArea.Height - margin);
    }
}

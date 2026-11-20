using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ovrix;

public partial class MainWindow
{
    private static FrameworkElement IconText(Geometry geometry, string? name, double size)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = geometry, Width = size, Height = size, StrokeThickness = 1.55,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetBinding(System.Windows.Shapes.Path.StrokeProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,
                typeof(Button), 1)
        });
        if (name is null) return icon;
        var layout = new StackPanel { Orientation = Orientation.Horizontal };
        layout.Children.Add(icon);
        var label = new TextBlock { Text = name, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,
                typeof(Button), 1)
        });
        layout.Children.Add(label);
        return layout;
    }

    private static void Reveal(UIElement element)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(.68, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    private void AnimateIdle(bool idle)
    {
        var animate = SystemParameters.ClientAreaAnimation;
        StageLogo.BeginAnimation(TranslateTransform.YProperty, idle && animate ? Pulse(0, -4, 1900) : null);
        StageGlow.BeginAnimation(ScaleTransform.ScaleXProperty, idle && animate ? Pulse(1, 1.13, 2050) : null);
        StageGlow.BeginAnimation(ScaleTransform.ScaleYProperty, idle && animate ? Pulse(1, 1.13, 2050) : null);
        LiveDot.BeginAnimation(OpacityProperty, !idle && animate ? Pulse(1, .48, 1250, true) : null);
    }

    private static DoubleAnimation Pulse(double start, double finish, int duration, bool repeat = false) => new(start, finish,
        TimeSpan.FromMilliseconds(duration))
    {
        AutoReverse = true, RepeatBehavior = repeat ? RepeatBehavior.Forever : new RepeatBehavior(2),
        FillBehavior = FillBehavior.Stop,
        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
    };

    private void ApplyCaptionTheme()
    {
        var window = new WindowInteropHelper(this).Handle;
        int dark = 1;
        _ = DwmSetWindowAttribute(window, 20, ref dark, sizeof(int));
        int panelBgr = 0x001D150D;
        _ = DwmSetWindowAttribute(window, 35, ref panelBgr, sizeof(int));
    }

    [DllImport("dwmapi")]
    private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, int valueSize);
}

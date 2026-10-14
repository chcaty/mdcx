using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace Organizer.Desktop.Controls;

public sealed class WorkbenchIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<WorkbenchIcon, string?>(nameof(Kind));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<WorkbenchIcon, IBrush?>(nameof(Stroke), Brush.Parse("#789068"));
    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    static WorkbenchIcon() => AffectsRender<WorkbenchIcon>(KindProperty, StrokeProperty);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var data = Kind switch
        {
            "folder" => "M3 7 V5 Q3 3 5 3 H10 L12 6 H19 Q21 6 21 8 V19 Q21 21 19 21 H5 Q3 21 3 19 Z",
            "stack" => "M4 5 H20 V20 H4 Z M8 2 H16 M2 9 H22 M7 14 H17",
            "eye" => "M2 12 Q12 -2 22 12 Q12 26 2 12 Z M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12",
            "scan" => "M8 3 H3 V8 M16 3 H21 V8 M21 16 V21 H16 M8 21 H3 V16 M4 12 H20",
            "spark" => "M12 2 L14.7 9.3 L22 12 L14.7 14.7 L12 22 L9.3 14.7 L2 12 L9.3 9.3 Z",
            "search" => "M16.5 10 A6.5 6.5 0 1 1 3.5 10 A6.5 6.5 0 1 1 16.5 10 M15 15 L21 21",
            "right" => "M9 5 L16 12 L9 19",
            "left" => "M15 5 L8 12 L15 19",
            "arrow" => "M3 12 H20 M14 6 L20 12 L14 18",
            "check" => "M5 12 L9 16 L19 6",
            "checkcircle" => "M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 M8 12 L11 15 L16 9",
            "warning" => "M10 3 L1 20 H23 L14 3 Z M12 8 V13 M12 16 V17",
            "film" => "M3 2 H21 V22 H3 Z M7 2 V22 M17 2 V22 M3 7 H7 M3 12 H7 M3 17 H7 M17 7 H21 M17 12 H21 M17 17 H21",
            "sub" => "M2 4 H22 V20 H2 Z M6 10 H11 M14 10 H18 M6 14 H9 M12 14 H18",
            "log" => "M9 5 H21 M9 12 H21 M9 19 H21 M3 5 H4 M3 12 H4 M3 19 H4",
            "lock" => "M5 10 H19 V21 H5 Z M8 10 V7 A4 4 0 0 1 16 7 V10 M12 14 V17",
            "edit" => "M14 4 L20 10 M3 21 L9 20 L21 8 L15 2 L3 14 Z",
            "close" => "M6 6 L18 18 M6 18 L18 6",
            "up" => "M5 15 L12 8 L19 15",
            _ => "M5 12 H19"
        };
        using var scale = context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24));
        context.DrawGeometry(null, new Pen(Stroke, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Geometry.Parse(data));
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Organizer.Desktop.Controls;

/// <summary>Original local vector illustration for synthetic fixtures; never a fetched poster.</summary>
public sealed class MediaPoster : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<MediaPoster, string?>(nameof(Kind));
    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    static MediaPoster() => AffectsRender<MediaPoster>(KindProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        using var clip = context.PushClip(new Rect(Bounds.Size));
        using var scale = context.PushTransform(Matrix.CreateScale(Bounds.Width / 160, Bounds.Height / 240));
        if (Kind == "lantern")
        {
            context.FillRectangle(Brush.Parse("#173E43"), new Rect(0, 0, 160, 240));
            Draw(context, "M0 75 Q80 12 160 83 V0 H0 Z", "#2C5657");
            context.DrawEllipse(Brush.Parse("#E4C789"), null, new Point(112, 66), 30, 30);
            Draw(context, "M0 160 L48 87 L102 174 L130 132 L160 177 V240 H0 Z", "#56746A");
            Draw(context, "M0 173 L45 131 L96 189 L137 120 L160 156 V240 H0 Z", "#254D4A");
            Draw(context, "M0 197 L42 156 L68 193 L101 172 L160 212 V240 H0 Z", "#113236");
            context.DrawGeometry(null, new Pen(Brush.Parse("#CBA869"), 2), Geometry.Parse("M80 240 Q51 201 86 181 Q121 161 78 138"));
            context.DrawLine(new Pen(Brush.Parse("#F3D69A"), 1.5), new Point(69, 119), new Point(80, 119));
            context.FillRectangle(Brush.Parse("#F7DDA2"), new Rect(72, 124, 5, 9));
        }
        else if (Kind == "mountain")
        {
            context.FillRectangle(Brush.Parse("#D9C5A2"), new Rect(0, 0, 160, 240));
            Draw(context, "M0 0 H160 V127 L0 75 Z", "#D7D9CE");
            context.DrawEllipse(Brush.Parse("#B45F46"), null, new Point(112, 72), 34, 34);
            Draw(context, "M0 164 L61 92 L160 186 V240 H0 Z", "#7A8B87");
            Draw(context, "M0 210 L49 142 L98 166 L160 138 V240 H0 Z", "#364F54");
            Draw(context, "M66 240 L98 123 L103 128 L81 240 Z", "#D6CCAA");
        }
        else
        {
            context.FillRectangle(Brush.Parse("#EDF0E9"), new Rect(0, 0, 160, 240));
            var pen = new Pen(Brush.Parse("#91A085"), 4);
            context.DrawRectangle(null, pen, new Rect(49, 83, 62, 76), 6, 6);
            context.DrawLine(pen, new Point(64, 83), new Point(64, 159));
            context.DrawLine(pen, new Point(96, 83), new Point(96, 159));
            for (var y = 99; y < 159; y += 20)
            {
                context.DrawLine(pen, new Point(49, y), new Point(64, y));
                context.DrawLine(pen, new Point(96, y), new Point(111, y));
            }
        }
    }
    private static void Draw(DrawingContext context, string path, string fill) => context.DrawGeometry(Brush.Parse(fill), null, Geometry.Parse(path));
}

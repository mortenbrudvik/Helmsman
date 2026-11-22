using System.Drawing;
using WinDesktopMcp.Models;

namespace WinDesktopMcp.Services;

/// <summary>
/// Where capture_with_elements puts its marks: the marks of the elements in the full-resolution capture, with their colors,
/// and their bounds scaled to the returned image.
/// </summary>
internal static class ElementMarkLayout
{
    /// <summary>
    /// Scales ImageBounds from the full-resolution capture to the output image. ScreenBounds stay in
    /// screen pixels.
    /// </summary>
    internal static List<AnnotatedElement> ScaleAnnotations(
        IReadOnlyList<AnnotatedElement> annotations,
        int finalWidth,
        int finalHeight,
        int originalWidth,
        int originalHeight)
    {
        var scaleX = (double)finalWidth / originalWidth;
        var scaleY = (double)finalHeight / originalHeight;

        return annotations.Select(annotation =>
        {
            var x = ClampToImage(RoundScaled(annotation.ImageBounds.X, scaleX), finalWidth);
            var y = ClampToImage(RoundScaled(annotation.ImageBounds.Y, scaleY), finalHeight);
            var width = ScaleLength(annotation.ImageBounds.Width, scaleX, finalWidth - x);
            var height = ScaleLength(annotation.ImageBounds.Height, scaleY, finalHeight - y);
            return annotation with { ImageBounds = new BoundsDto(x, y, width, height) };
        }).ToList();
    }

    private static int RoundScaled(int value, double scale) =>
        (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);

    private static int ClampToImage(int value, int limit) =>
        Math.Clamp(value, 0, Math.Max(0, limit - 1));

    private static int ScaleLength(int value, double scale, int room)
    {
        if (value <= 0 || room <= 0)
        {
            return 0;
        }

        var scaled = Math.Max(1, RoundScaled(value, scale));
        return Math.Min(scaled, room);
    }

    /// <summary>
    /// The marks of <paramref name="elements"/> and their children, in reading order (an element, then the elements inside
    /// it), with their positions in the full-resolution capture (clipped to it) and their colors. Elements smaller than 2
    /// pixels, without bounds or outside the image get no mark; their children still can.
    /// </summary>
    internal static void CollectAnnotations(
        IReadOnlyList<UIElementInfo> elements,
        int originX,
        int originY,
        int imageWidth,
        int imageHeight,
        bool highlightInteractable,
        List<(AnnotatedElement Element, Color Color)> annotations)
    {
        foreach (var element in elements)
        {
            if (element.Bounds is { Width: >= 2, Height: >= 2 } screenBounds)
            {
                var visible = Rectangle.Intersect(
                    new Rectangle(screenBounds.X - originX, screenBounds.Y - originY, screenBounds.Width, screenBounds.Height),
                    new Rectangle(0, 0, imageWidth, imageHeight));

                if (visible.Width > 0 && visible.Height > 0)
                {
                    var (color, colorName) = GetOverlayColor(element.ControlType, element.IsEnabled, highlightInteractable);
                    annotations.Add((new AnnotatedElement(
                        Name: element.Name,
                        ControlType: element.ControlType,
                        AutomationId: element.AutomationId,
                        ImageBounds: new BoundsDto(visible.X, visible.Y, visible.Width, visible.Height),
                        ScreenBounds: screenBounds,
                        IsEnabled: element.IsEnabled,
                        OverlayColor: colorName,
                        Ref: element.Ref), color));
                }
            }

            if (element.Children != null)
            {
                CollectAnnotations(element.Children, originX, originY, imageWidth, imageHeight, highlightInteractable, annotations);
            }
        }
    }

    private static (Color color, string name) GetOverlayColor(string controlType, bool isEnabled, bool highlightInteractable)
    {
        // Dim colors for disabled elements when highlighting interactable
        var alpha = (highlightInteractable && !isEnabled) ? 100 : 200;

        return controlType switch
        {
            "Button" or "SplitButton" => (Color.FromArgb(alpha, 0, 200, 0), "green"),      // Green for buttons
            "Edit" or "Document" => (Color.FromArgb(alpha, 0, 150, 255), "blue"),          // Blue for text input
            "CheckBox" or "RadioButton" => (Color.FromArgb(alpha, 255, 165, 0), "orange"), // Orange for toggles
            "ComboBox" or "List" or "ListItem" => (Color.FromArgb(alpha, 200, 0, 200), "purple"), // Purple for lists
            "Hyperlink" => (Color.FromArgb(alpha, 0, 200, 200), "cyan"),                   // Cyan for links
            "MenuItem" or "Menu" or "MenuBar" => (Color.FromArgb(alpha, 255, 100, 100), "red"), // Red for menus
            "Tab" or "TabItem" => (Color.FromArgb(alpha, 255, 200, 0), "yellow"),          // Yellow for tabs
            "Image" => (Color.FromArgb(alpha, 150, 150, 150), "gray"),                     // Gray for images
            "Text" => (Color.FromArgb(alpha, 180, 180, 180), "lightgray"),                 // Light gray for text
            _ => (Color.FromArgb(alpha, 100, 100, 255), "lightblue")                       // Light blue for others
        };
    }
}

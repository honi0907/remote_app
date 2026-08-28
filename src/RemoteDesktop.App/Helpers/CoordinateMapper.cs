namespace RemoteDesktop.App.Helpers;

public static class CoordinateMapper
{
    public static bool TryMapPointerToNormalized(
        double pointerX,
        double pointerY,
        double containerWidth,
        double containerHeight,
        double sourceWidth,
        double sourceHeight,
        out double normalizedX,
        out double normalizedY)
    {
        normalizedX = 0;
        normalizedY = 0;

        if (containerWidth <= 0 || containerHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return false;
        }

        var scale = Math.Min(containerWidth / sourceWidth, containerHeight / sourceHeight);
        var renderedWidth = sourceWidth * scale;
        var renderedHeight = sourceHeight * scale;
        var offsetX = (containerWidth - renderedWidth) / 2;
        var offsetY = (containerHeight - renderedHeight) / 2;

        if (pointerX < offsetX || pointerY < offsetY ||
            pointerX > offsetX + renderedWidth || pointerY > offsetY + renderedHeight)
        {
            return false;
        }

        normalizedX = (pointerX - offsetX) / renderedWidth;
        normalizedY = (pointerY - offsetY) / renderedHeight;
        return true;
    }
}

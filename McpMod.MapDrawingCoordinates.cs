using Godot;

namespace STS2_MCP;

public static partial class McpMod
{
    internal static Vector2 GetMapDrawingNodeAnchor(Vector2 nodeSize)
    {
        return nodeSize * 0.5f;
    }

    internal static Vector2 ToMapDrawingPosition(Vector2 drawingSize, Vector2 localPosition)
    {
        return new Vector2(
            (localPosition.X - drawingSize.X * 0.5f) / 960f,
            localPosition.Y / drawingSize.Y);
    }

    internal static Vector2 FromMapDrawingPosition(Vector2 drawingSize, Vector2 point)
    {
        return new Vector2(point.X * 960f + drawingSize.X * 0.5f, point.Y * drawingSize.Y);
    }

    internal static Vector2 DecodeSerializedMapDrawingPosition(Vector2 drawingSize, Vector2 point)
    {
        // NMapDrawings renders Line2D nodes in a half-resolution SubViewport,
        // so BeginLine stores local input positions at half scale. Its
        // GetSerializableMapDrawings path nevertheless applies ToNetPosition
        // directly to those stored points. Restore the full-resolution local
        // input before exposing the normalized point through MCP.
        var halfResolutionLocal = FromMapDrawingPosition(drawingSize, point);
        return ToMapDrawingPosition(drawingSize, halfResolutionLocal * 2f);
    }
}

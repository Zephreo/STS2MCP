using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private const int MaxMapDrawingStrokesPerRequest = 64;
    private const int MaxMapDrawingPointsPerRequest = 2048;
    private const float MinMapDrawingX = -3f;
    private const float MaxMapDrawingX = 3f;
    private const float MinMapDrawingY = -2f;
    private const float MaxMapDrawingY = 2f;

    private sealed record ParsedMapDrawingStroke(DrawingMode Mode, List<Vector2> Points);

    private static void HandleGetMapDrawings(HttpListenerResponse response)
    {
        try
        {
            var dataTask = RunOnMainThread(BuildMapDrawingsState);
            SendJson(response, dataTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Failed to read map drawings: {ex.Message}");
        }
    }

    private static Dictionary<string, object?> BuildMapDrawingsState()
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress");

        var mapScreen = NMapScreen.Instance;
        var drawings = mapScreen?.Drawings;
        if (mapScreen == null || drawings == null)
            return Error("Map drawing surface is not available");

        var players = new List<Dictionary<string, object?>>();
        foreach (var playerDrawings in drawings.GetSerializableMapDrawings().drawings)
        {
            var lines = new List<Dictionary<string, object?>>();
            foreach (var line in playerDrawings.lines)
            {
                lines.Add(new Dictionary<string, object?>
                {
                    ["mode"] = line.isEraser ? "erase" : "draw",
                    ["points"] = line.mapPoints.Select(BuildMapDrawingPoint).ToList()
                });
            }

            players.Add(new Dictionary<string, object?>
            {
                ["player_id"] = playerDrawings.playerId.ToString(),
                ["is_local"] = playerDrawings.playerId == LocalContext.NetId,
                ["lines"] = lines
            });
        }

        var nodes = BuildMapDrawingNodePositions(mapScreen)
            .OrderBy(pair => pair.Key.row)
            .ThenBy(pair => pair.Key.col)
            .Select(pair => new Dictionary<string, object?>
            {
                ["col"] = pair.Key.col,
                ["row"] = pair.Key.row,
                ["position"] = BuildMapDrawingPoint(pair.Value)
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["coordinate_space"] = BuildMapDrawingCoordinateSpace(),
            ["nodes"] = nodes,
            ["players"] = players
        };
    }

    private static Dictionary<string, object?> ExecuteMapDraw(Dictionary<string, JsonElement> data)
    {
        var mapScreen = NMapScreen.Instance;
        if (mapScreen == null || (!mapScreen.IsOpen && !IsNodeVisible(mapScreen)))
            return Error("Map screen is not open");

        var drawings = mapScreen.Drawings;
        if (drawings == null)
            return Error("Map drawing surface is not available");
        if (drawings.IsLocalDrawing())
            return Error("The local player is already drawing a line");
        if (!data.TryGetValue("strokes", out var strokesElement) || strokesElement.ValueKind != JsonValueKind.Array)
            return Error("Missing 'strokes' array");
        if (strokesElement.GetArrayLength() == 0)
            return Error("'strokes' must contain at least one stroke");
        if (strokesElement.GetArrayLength() > MaxMapDrawingStrokesPerRequest)
            return Error($"Too many strokes (maximum {MaxMapDrawingStrokesPerRequest})");

        var nodePositions = BuildMapDrawingNodePositions(mapScreen);
        var parsedStrokes = new List<ParsedMapDrawingStroke>();
        int totalPoints = 0;

        foreach (var strokeElement in strokesElement.EnumerateArray())
        {
            if (strokeElement.ValueKind != JsonValueKind.Object)
                return Error("Each stroke must be an object");

            var modeResult = ParseMapDrawingMode(strokeElement, out var mode);
            if (modeResult != null)
                return modeResult;

            if (!strokeElement.TryGetProperty("points", out var pointsElement) || pointsElement.ValueKind != JsonValueKind.Array)
                return Error("Each stroke must contain a 'points' array");
            if (pointsElement.GetArrayLength() == 0)
                return Error("Each stroke must contain at least one point");

            totalPoints += pointsElement.GetArrayLength();
            if (totalPoints > MaxMapDrawingPointsPerRequest)
                return Error($"Too many drawing points (maximum {MaxMapDrawingPointsPerRequest})");

            var points = new List<Vector2>(pointsElement.GetArrayLength());
            foreach (var pointElement in pointsElement.EnumerateArray())
            {
                var pointResult = ParseMapDrawingPoint(pointElement, nodePositions, out var point);
                if (pointResult != null)
                    return pointResult;
                points.Add(point);
            }
            parsedStrokes.Add(new ParsedMapDrawingStroke(mode, points));
        }

        foreach (var stroke in parsedStrokes)
        {
            bool began = false;
            try
            {
                drawings.BeginLineLocal(FromMapDrawingPosition(drawings, stroke.Points[0]), stroke.Mode);
                began = true;
                foreach (var point in stroke.Points.Skip(1))
                    drawings.UpdateCurrentLinePositionLocal(FromMapDrawingPosition(drawings, point));
            }
            finally
            {
                if (began && drawings.IsLocalDrawing())
                    drawings.StopLineLocal();
            }
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Added {parsedStrokes.Count} map drawing stroke(s) with {totalPoints} requested point(s)",
            ["strokes_added"] = parsedStrokes.Count,
            ["points_requested"] = totalPoints
        };
    }

    private static Dictionary<string, object?> ExecuteMapClearDrawings()
    {
        var mapScreen = NMapScreen.Instance;
        if (mapScreen == null || (!mapScreen.IsOpen && !IsNodeVisible(mapScreen)))
            return Error("Map screen is not open");

        var drawings = mapScreen.Drawings;
        if (drawings == null)
            return Error("Map drawing surface is not available");
        if (drawings.IsLocalDrawing())
            return Error("The local player is already drawing a line");

        drawings.ClearDrawnLinesLocal();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Cleared the local player's map drawings"
        };
    }

    private static Dictionary<string, object?>? ParseMapDrawingMode(JsonElement stroke, out DrawingMode mode)
    {
        mode = DrawingMode.Drawing;
        if (!stroke.TryGetProperty("mode", out var modeElement))
            return null;
        if (modeElement.ValueKind != JsonValueKind.String)
            return Error("Stroke 'mode' must be 'draw' or 'erase'");

        switch (modeElement.GetString()?.Trim().ToLowerInvariant())
        {
            case "draw":
            case "drawing":
                mode = DrawingMode.Drawing;
                return null;
            case "erase":
            case "erasing":
                mode = DrawingMode.Erasing;
                return null;
            default:
                return Error("Stroke 'mode' must be 'draw' or 'erase'");
        }
    }

    private static Dictionary<string, object?>? ParseMapDrawingPoint(
        JsonElement pointElement,
        IReadOnlyDictionary<(int col, int row), Vector2> nodePositions,
        out Vector2 point)
    {
        point = default;
        if (pointElement.ValueKind != JsonValueKind.Object)
            return Error("Each drawing point must be an object with either x/y or col/row fields");

        bool hasX = pointElement.TryGetProperty("x", out var xElement);
        bool hasY = pointElement.TryGetProperty("y", out var yElement);
        bool hasCol = pointElement.TryGetProperty("col", out var colElement);
        bool hasRow = pointElement.TryGetProperty("row", out var rowElement);

        if (hasX || hasY)
        {
            if (!hasX || !hasY || hasCol || hasRow)
                return Error("A coordinate point must contain exactly x and y, not node col/row fields");
            if (!TryGetFiniteSingle(xElement, out var x) || !TryGetFiniteSingle(yElement, out var y))
                return Error("Drawing point x and y must be finite numbers");
            if (x is < MinMapDrawingX or > MaxMapDrawingX || y is < MinMapDrawingY or > MaxMapDrawingY)
                return Error($"Drawing point ({x}, {y}) is outside x=[{MinMapDrawingX},{MaxMapDrawingX}], y=[{MinMapDrawingY},{MaxMapDrawingY}]");
            point = new Vector2(x, y);
            return null;
        }

        if (!hasCol || !hasRow)
            return Error("Each drawing point must contain either x/y or col/row fields");
        if (!colElement.TryGetInt32(out var col) || !rowElement.TryGetInt32(out var row))
            return Error("Drawing node col and row must be integers");
        if (!nodePositions.TryGetValue((col, row), out point))
            return Error($"Map node ({col},{row}) was not found in the live map UI");
        return null;
    }

    private static bool TryGetFiniteSingle(JsonElement element, out float value)
    {
        value = 0f;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out value))
            return false;
        return float.IsFinite(value);
    }

    private static Vector2 FromMapDrawingPosition(NMapDrawings drawings, Vector2 point)
    {
        return new Vector2(point.X * 960f + drawings.Size.X * 0.5f, point.Y * drawings.Size.Y);
    }

    private static Vector2 ToMapDrawingPosition(NMapDrawings drawings, Vector2 point)
    {
        return new Vector2((point.X - drawings.Size.X * 0.5f) / 960f, point.Y / drawings.Size.Y);
    }

    private static Dictionary<(int col, int row), Vector2> BuildMapDrawingNodePositions(NMapScreen? mapScreen)
    {
        var positions = new Dictionary<(int col, int row), Vector2>();
        var drawings = mapScreen?.Drawings;
        if (mapScreen == null || drawings == null || drawings.Size.Y == 0f)
            return positions;

        var drawingsInverse = drawings.GetGlobalTransform().Inverse();
        foreach (var node in FindAll<NMapPoint>(mapScreen))
        {
            if (node.Point == null)
                continue;

            // NMapScreen.GetLineEndpoint uses the origin for normal points and
            // the visual centre for the larger starting/boss point controls.
            var localAnchor = node is NNormalMapPoint ? Vector2.Zero : node.Size * 0.5f;
            var globalAnchor = node.GetGlobalTransform() * localAnchor;
            var drawingLocal = drawingsInverse * globalAnchor;
            positions[(node.Point.coord.col, node.Point.coord.row)] = ToMapDrawingPosition(drawings, drawingLocal);
        }
        return positions;
    }

    private static Dictionary<string, object?> BuildMapDrawingPoint(Vector2 point)
    {
        return new Dictionary<string, object?> { ["x"] = point.X, ["y"] = point.Y };
    }

    private static Dictionary<string, object?> BuildMapDrawingCoordinateSpace()
    {
        return new Dictionary<string, object?>
        {
            ["name"] = "map_normalized",
            ["x_min"] = MinMapDrawingX,
            ["x_max"] = MaxMapDrawingX,
            ["y_min"] = MinMapDrawingY,
            ["y_max"] = MaxMapDrawingY,
            ["description"] = "Resolution-independent coordinates used by the game's saved and multiplayer map drawings"
        };
    }
}

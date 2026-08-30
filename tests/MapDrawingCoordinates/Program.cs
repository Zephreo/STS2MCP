using System;
using Godot;
using STS2_MCP;

static void AssertNear(Vector2 actual, Vector2 expected, string message)
{
    const float tolerance = 0.00001f;
    if (MathF.Abs(actual.X - expected.X) > tolerance || MathF.Abs(actual.Y - expected.Y) > tolerance)
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}

var drawingSize = new Vector2(1920f, 1080f);
var node = new Vector2(-0.5148421f, -0.45199984f);
var local = McpMod.FromMapDrawingPosition(drawingSize, node);

AssertNear(McpMod.GetMapDrawingNodeAnchor(new Vector2(64f, 64f)), new Vector2(32f, 32f),
    "map drawing anchors must use the visible centre of normal-room controls");

AssertNear(McpMod.ToMapDrawingPosition(drawingSize, local), node,
    "normalized/local coordinate conversion must round-trip");

var serializedHalfResolutionPoint = McpMod.ToMapDrawingPosition(drawingSize, local * 0.5f);
AssertNear(McpMod.DecodeSerializedMapDrawingPosition(drawingSize, serializedHalfResolutionPoint), node,
    "half-resolution Line2D readback must recover the requested node position");

Console.WriteLine("Map drawing coordinate checks passed.");

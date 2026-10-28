#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// Writes LineworkDocument to .linework JSON format.
/// Mirror of LineworkReader for saving/exporting linework documents.
/// </summary>
public static class LineworkWriter
{
    /// <summary>
    /// Write a LineworkDocument to a file
    /// </summary>
    public static void Write(LineworkDocument doc, string path)
    {
        var json = Serialize(doc);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    /// <summary>
    /// Serialize a LineworkDocument to JSON string
    /// </summary>
    public static string Serialize(LineworkDocument doc)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();

        // Document name
        writer.WriteString("name", doc.Name);

        // Symmetry
        writer.WriteStartObject("sym");
        writer.WriteBoolean("on", doc.SymOn);
        writer.WriteNumber("x", doc.SymX);
        writer.WriteEndObject();

        // Layers
        writer.WriteStartArray("layers");
        foreach (var layer in doc.Layers)
        {
            WriteLayer(writer, layer);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteLayer(Utf8JsonWriter writer, LineworkLayer layer)
    {
        writer.WriteStartObject();

        writer.WriteString("id", layer.Id);
        writer.WriteString("name", layer.Name);
        writer.WriteBoolean("visible", layer.Visible);
        writer.WriteBoolean("ref", layer.Ref);

        // Strokes
        writer.WriteStartArray("strokes");
        foreach (var stroke in layer.Strokes)
        {
            WriteStroke(writer, stroke);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteStroke(Utf8JsonWriter writer, LineworkStroke stroke)
    {
        writer.WriteStartObject();

        writer.WriteString("id", stroke.Id);
        writer.WriteString("tool", stroke.Tool);
        writer.WriteString("color", stroke.Color);
        writer.WriteNumber("size", stroke.Size);
        writer.WriteNumber("opacity", stroke.Opacity);
        writer.WriteBoolean("closed", stroke.Closed);

        // Points (as x,y,pressure triples)
        writer.WriteStartArray("pts");
        for (int i = 0; i < stroke.Points.Count; i++)
        {
            var p = stroke.Points[i];
            float pressure = (i < stroke.Pressures.Count) ? stroke.Pressures[i] : 0.5f;
            writer.WriteNumberValue(p.x);
            writer.WriteNumberValue(p.y);
            writer.WriteNumberValue(pressure);
        }
        writer.WriteEndArray();

        // Holes
        writer.WriteStartArray("holes");
        foreach (var hole in stroke.Holes)
        {
            writer.WriteStartArray();
            for (int i = 0; i < hole.Count; i++)
            {
                var p = hole[i];
                float pressure = 0.5f; // Holes don't have pressure in current format
                writer.WriteNumberValue(p.x);
                writer.WriteNumberValue(p.y);
                writer.WriteNumberValue(pressure);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Create a minimal LineworkDocument from strokes (single layer, default settings)
    /// </summary>
    public static LineworkDocument CreateDocument(string name, List<LineworkStroke> strokes)
    {
        return new LineworkDocument
        {
            Name = name,
            Layers = new List<LineworkLayer>
            {
                new LineworkLayer
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "Layer 1",
                    Visible = true,
                    Strokes = strokes
                }
            }
        };
    }
}

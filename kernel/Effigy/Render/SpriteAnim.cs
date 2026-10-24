// Native sprite animation for the Geppetto 2D animation suite (lane 2).
//
// What this is: the data + playback model for a PNG sprite sheet. It slices
// a sheet into frames, carries per-frame timing, Aseprite-style tags, trim
// correction and hitbox/pivot slices, and answers "which frame is showing
// at time T" for loop / ping-pong / reverse / once playback.
//
// What this is NOT:
//  - It does not decode PNG pixels. A sheet's pixel size comes from the
//    atlas JSON (meta.size) or from the caller (grid / baker import). The
//    cabinet player crops the PNG itself with sourceClipRect.
//  - It does not bake sheets. SpriteSheetBaker output is an INPUT here
//    (see FromBaker): this file never edits SpriteSheetBaker.cs.
//  - It does not simulate particles or paint brushes. It only draws frames
//    that already exist (2D canvas/UI boundary).
//
// Formats, as they exist in the wild (not invented here):
//  - TexturePacker JSON: frames as an object (Hash, keyed by sprite name)
//    or an array (Array, each entry with a filename), each frame carrying
//    frame {x,y,w,h}, spriteSourceSize {x,y,w,h} and sourceSize {w,h}.
//  - Aseprite JSON: the same frames shape, plus meta.frameTags (name, from,
//    to, direction forward/reverse/pingpong, repeat) and meta.slices
//    (name + keys with frame, bounds, pivot).
//
// Trim rule (the jitter fix): a trimmed frame is drawn at (OffsetX, OffsetY)
// inside a box of (SourceW, SourceH). Two frames of the same sprite with
// different trims therefore land on the same origin instead of jumping
// around as their trimmed rects change size. DrawOffsetX/Y are exactly
// spriteSourceSize.x/y.
//
// Duration rule: a frame with DurationMs <= 0 plays for 1000/Fps ms, so a
// grid or baker import (which carries no per-frame timing) still animates
// at the animation's fps, and a single frame can be held by giving it a
// longer DurationMs (AddHold adds milliseconds to one frame).
//
// Tag repeat rule: Repeat <= 0 means loop forever (the Aseprite convention);
// Repeat > 0 means that many total cycles, then the playback finishes on
// the last frame of the tag's sequence. A non-tag animation with
// LoopMode Once always plays exactly one cycle; Loop/PingPong/Reverse
// loop forever unless Repeat is set on the animation itself.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Effigy.Render;

/// <summary>How a sprite animation cycles. PingPong does NOT double its
/// endpoints: 0,1,2,1,0,1,2,1... (the standard flipbook behaviour).</summary>
public enum SpriteLoopMode { Loop, PingPong, Reverse, Once }

/// <summary>Direction of an Aseprite frame tag.</summary>
public enum SpriteTagDirection { Forward, Reverse, PingPong }

/// <summary>One frame of a sprite sheet: a rect in sheet pixels plus the
/// trim correction that puts it back on a stable origin.</summary>
public sealed class SpriteFrame
{
    public string Name = "";
    public int X, Y, W, H;
    /// <summary>Milliseconds this frame shows. <= 0 means "use 1000/Fps".</summary>
    public int DurationMs;
    /// <summary>Untrimmed sprite size (sourceSize). Defaults to W/H.</summary>
    public int SourceW, SourceH;
    /// <summary>Position of the trimmed rect inside the untrimmed box
    /// (spriteSourceSize.x/y). Drawing at this offset stops trim jitter.</summary>
    public int OffsetX, OffsetY;

    public bool IsTrimmed => SourceW != W || SourceH != H || OffsetX != 0 || OffsetY != 0;
    /// <summary>Where to draw the trimmed image inside its untrimmed box.</summary>
    public int DrawOffsetX => OffsetX;
    public int DrawOffsetY => OffsetY;

    public SpriteFrame() { }
    public SpriteFrame(string name, int x, int y, int w, int h, int durationMs = 0)
    {
        Name = name; X = x; Y = y; W = w; H = h; DurationMs = durationMs;
        SourceW = w; SourceH = h;
    }
}

/// <summary>One key of a slice: the hitbox/pivot rect that applies from
/// this frame until the next key (Aseprite slice-key semantics).</summary>
public sealed class SpriteSliceKey
{
    public int Frame;
    public int X, Y, W, H;
    /// <summary>Normalised pivot inside the slice rect (0..1). Default centre.</summary>
    public double PivotX = 0.5, PivotY = 0.5;
}

/// <summary>A named slice (hitbox, pivot, nine-slice region) with per-frame
/// keys. BoundsAt returns the latest key at or before the frame, or the
/// first key when the frame precedes every key.</summary>
public sealed class SpriteSlice
{
    public string Name = "";
    public List<SpriteSliceKey> Keys = new();

    public SpriteSliceKey? BoundsAt(int frameIndex)
    {
        if (Keys.Count == 0) return null;
        SpriteSliceKey? best = null;
        foreach (var k in Keys)
        {
            if (k.Frame <= frameIndex) best = k;
            else break;
        }
        return best ?? Keys[0];
    }
}

/// <summary>An Aseprite-style frame tag: a named sub-range of frames with
/// its own direction and repeat count.</summary>
public sealed class SpriteTag
{
    public string Name = "";
    public int From, To;
    public SpriteTagDirection Direction = SpriteTagDirection.Forward;
    /// <summary><= 0 means loop forever; > 0 means that many total cycles.</summary>
    public int Repeat;
    public int FrameCount => To - From + 1;
}

/// <summary>A sliced sprite sheet: frames, tags and slices, plus the
/// factories that build one from a grid, from a baker sheet, or from a
/// TexturePacker / Aseprite atlas JSON.</summary>
public sealed class SpriteSheet
{
    public string ImagePath = "";
    public int Width, Height;
    public List<SpriteFrame> Frames = new();
    public List<SpriteTag> Tags = new();
    public List<SpriteSlice> Slices = new();

    public SpriteFrame? FindFrame(string name) => Frames.FirstOrDefault(f => f.Name == name);
    public SpriteTag? FindTag(string name) => Tags.FirstOrDefault(t => t.Name == name);

    static void Require(bool ok, string message)
    {
        if (!ok) throw new ArgumentException(message);
    }

    static void CheckRect(SpriteSheet sheet, SpriteFrame f)
    {
        Require(f.W > 0 && f.H > 0, $"Sprite frame '{f.Name}' has a non-positive size.");
        Require(f.X >= 0 && f.Y >= 0 && f.X + f.W <= sheet.Width && f.Y + f.H <= sheet.Height,
            $"Sprite frame '{f.Name}' rect ({f.X},{f.Y} {f.W}x{f.H}) is outside the {sheet.Width}x{sheet.Height} sheet.");
        Require(f.SourceW >= f.W && f.SourceH >= f.H,
            $"Sprite frame '{f.Name}' untrimmed size ({f.SourceW}x{f.SourceH}) is smaller than its trimmed rect ({f.W}x{f.H}).");
        Require(f.OffsetX >= 0 && f.OffsetY >= 0 && f.OffsetX + f.W <= f.SourceW && f.OffsetY + f.H <= f.SourceH,
            $"Sprite frame '{f.Name}' trim offset ({f.OffsetX},{f.OffsetY}) does not fit its untrimmed size.");
    }

    static void CheckTags(SpriteSheet sheet)
    {
        foreach (var t in sheet.Tags)
        {
            Require(t.From >= 0 && t.To >= t.From && t.To < sheet.Frames.Count,
                $"Sprite tag '{t.Name}' range {t.From}..{t.To} is outside the {sheet.Frames.Count} frames.");
        }
    }

    /// <summary>Slice a uniform grid, row-major, top-left first. A trailing
    /// partial strip (sheet not an exact multiple of the tile) is ignored,
    /// never stretched into a frame.</summary>
    public static SpriteSheet FromGrid(int sheetWidth, int sheetHeight, int tileW, int tileH,
        int fps = 12, string namePrefix = "frame", int columns = 0, int rows = 0)
    {
        Require(sheetWidth > 0 && sheetHeight > 0, "Sprite sheet size must be positive.");
        Require(tileW > 0 && tileH > 0, "Sprite tile size must be positive.");
        if (columns <= 0) columns = sheetWidth / tileW;
        if (rows <= 0) rows = sheetHeight / tileH;
        Require(columns > 0 && rows > 0, "Sprite sheet is smaller than one tile.");
        Require(columns * tileW <= sheetWidth && rows * tileH <= sheetHeight,
            "Sprite grid extends past the sheet.");
        var sheet = new SpriteSheet { Width = sheetWidth, Height = sheetHeight };
        int i = 0;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
                sheet.Frames.Add(new SpriteFrame($"{namePrefix}_{i++}", c * tileW, r * tileH, tileW, tileH));
        return sheet;
    }

    /// <summary>Slice a SpriteSheetBaker sheet. The baker's grid is
    /// directions across, frames down (see SpriteSheetOptions): frame f of
    /// direction d lives at (d * Tile, f * Tile). The sheet size must match
    /// exactly, so a baker sheet is never silently mis-sliced.</summary>
    public static SpriteSheet FromBaker(int sheetWidth, int sheetHeight, int tile, int directions, int frames)
    {
        Require(directions is 1 or 4 or 8, "SpriteSheet directions must be 1, 4, or 8 (as the baker accepts).");
        Require(tile >= 16 && tile <= 512, "SpriteSheet tile must be 16..512 (as the baker accepts).");
        Require(frames >= 1, "SpriteSheet frames must be >= 1.");
        Require(sheetWidth == directions * tile && sheetHeight == frames * tile,
            $"Baker sheet size {sheetWidth}x{sheetHeight} does not match {directions} directions x {frames} frames of {tile}px tiles.");
        var sheet = new SpriteSheet { Width = sheetWidth, Height = sheetHeight };
        for (int f = 0; f < frames; f++)
            for (int d = 0; d < directions; d++)
                sheet.Frames.Add(new SpriteFrame($"dir{d}_frame{f}", d * tile, f * tile, tile, tile));
        return sheet;
    }

    /// <summary>Frames of one baker direction, in frame order, as an
    /// animation. Direction d is one column of the baker grid.</summary>
    public SpriteAnimation AnimationForDirection(int direction, int directions, int frames, int fps = 12,
        SpriteLoopMode mode = SpriteLoopMode.Loop)
    {
        Require(direction >= 0 && direction < directions, $"Direction {direction} is outside 0..{directions - 1}.");
        Require(Frames.Count == directions * frames, "Sheet frame count does not match directions x frames.");
        var picked = new List<SpriteFrame>();
        for (int f = 0; f < frames; f++)
            picked.Add(Frames[f * directions + direction]);
        return new SpriteAnimation($"dir{direction}", picked, mode, fps);
    }

    /// <summary>An animation covering one tag's range, with the tag's
    /// direction and repeat. Tag direction maps: forward -> Loop,
    /// reverse -> Reverse, pingpong -> PingPong.</summary>
    public SpriteAnimation TagAnimation(string tagName, int fps = 12)
    {
        var tag = FindTag(tagName) ?? throw new ArgumentException($"No sprite tag named '{tagName}'.");
        var mode = tag.Direction switch
        {
            SpriteTagDirection.Forward => SpriteLoopMode.Loop,
            SpriteTagDirection.Reverse => SpriteLoopMode.Reverse,
            _ => SpriteLoopMode.PingPong,
        };
        return new SpriteAnimation(tag.Name, Frames.Skip(tag.From).Take(tag.FrameCount).ToList(), mode, fps, tag.Repeat);
    }

    // ---- Atlas JSON (TexturePacker Hash/Array, Aseprite) ----

    public static SpriteSheet ParseAtlas(string json, string imagePath = "")
        => ParseAtlas(JsonDocument.Parse(json).RootElement, imagePath);

    public static SpriteSheet LoadAtlasFile(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var sheet = ParseAtlas(json);
        if (sheet.ImagePath.Length == 0) return sheet;
        if (!Path.IsPathRooted(sheet.ImagePath))
        {
            var dir = Path.GetDirectoryName(jsonPath) ?? "";
            sheet.ImagePath = Path.Combine(dir, sheet.ImagePath);
        }
        return sheet;
    }

    static int JInt(JsonElement e, string name, int fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    static double JDbl(JsonElement e, string name, double fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    public static SpriteSheet ParseAtlas(JsonElement root, string imagePath = "")
    {
        var sheet = new SpriteSheet { ImagePath = imagePath };
        if (root.TryGetProperty("meta", out var meta))
        {
            if (meta.TryGetProperty("size", out var size))
            {
                sheet.Width = JInt(size, "w", 0);
                sheet.Height = JInt(size, "h", 0);
            }
            if (sheet.ImagePath.Length == 0 && meta.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String)
                sheet.ImagePath = img.GetString() ?? "";
        }
        Require(sheet.Width > 0 && sheet.Height > 0, "Sprite atlas meta.size is missing or non-positive.");

        Require(root.TryGetProperty("frames", out var framesEl), "Sprite atlas has no 'frames'.");
        if (framesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in framesEl.EnumerateObject())
                sheet.Frames.Add(ParseAtlasFrame(prop.Name, prop.Value));
        }
        else if (framesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in framesEl.EnumerateArray())
            {
                var name = el.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String
                    ? fn.GetString() ?? "" : $"frame_{sheet.Frames.Count}";
                sheet.Frames.Add(ParseAtlasFrame(name, el));
            }
        }
        else throw new ArgumentException("Sprite atlas 'frames' must be an object (Hash) or an array (Array).");

        foreach (var f in sheet.Frames) CheckRect(sheet, f);

        if (meta.ValueKind != JsonValueKind.Undefined && meta.TryGetProperty("frameTags", out var tagsEl)
            && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in tagsEl.EnumerateArray())
            {
                var dir = el.TryGetProperty("direction", out var dv) && dv.ValueKind == JsonValueKind.String
                    ? dv.GetString() ?? "forward" : "forward";
                sheet.Tags.Add(new SpriteTag
                {
                    Name = el.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() ?? "" : "",
                    From = JInt(el, "from", 0),
                    To = JInt(el, "to", 0),
                    Direction = dir switch
                    {
                        "reverse" => SpriteTagDirection.Reverse,
                        "pingpong" or "ping-pong" => SpriteTagDirection.PingPong,
                        "forward" => SpriteTagDirection.Forward,
                        _ => throw new ArgumentException($"Unknown sprite tag direction '{dir}'."),
                    },
                    Repeat = JInt(el, "repeat", 0),
                });
            }
        }
        CheckTags(sheet);

        if (meta.ValueKind != JsonValueKind.Undefined && meta.TryGetProperty("slices", out var slicesEl)
            && slicesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in slicesEl.EnumerateArray())
            {
                var slice = new SpriteSlice
                {
                    Name = el.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() ?? "" : "",
                };
                if (el.TryGetProperty("keys", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var k in keysEl.EnumerateArray())
                    {
                        var key = new SpriteSliceKey { Frame = JInt(k, "frame", 0) };
                        if (k.TryGetProperty("bounds", out var b))
                        {
                            key.X = JInt(b, "x", 0); key.Y = JInt(b, "y", 0);
                            key.W = JInt(b, "w", 0); key.H = JInt(b, "h", 0);
                        }
                        if (k.TryGetProperty("pivot", out var p))
                        {
                            key.PivotX = JDbl(p, "x", 0.5); key.PivotY = JDbl(p, "y", 0.5);
                        }
                        slice.Keys.Add(key);
                    }
                }
                slice.Keys.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                sheet.Slices.Add(slice);
            }
        }
        return sheet;
    }

    static SpriteFrame ParseAtlasFrame(string name, JsonElement el)
    {
        if (el.TryGetProperty("rotated", out var rot) && rot.ValueKind == JsonValueKind.True)
            throw new NotSupportedException($"Sprite frame '{name}' is rotated in the atlas; rotated packing is not supported (re-export unrotated).");
        Require(el.TryGetProperty("frame", out var rect), $"Sprite frame '{name}' has no 'frame' rect.");
        var f = new SpriteFrame(name, JInt(rect, "x", 0), JInt(rect, "y", 0), JInt(rect, "w", 0), JInt(rect, "h", 0),
            JInt(el, "duration", 0));
        f.SourceW = f.W; f.SourceH = f.H;
        if (el.TryGetProperty("spriteSourceSize", out var src))
        {
            f.OffsetX = JInt(src, "x", 0); f.OffsetY = JInt(src, "y", 0);
        }
        if (el.TryGetProperty("sourceSize", out var size))
        {
            f.SourceW = JInt(size, "w", f.W); f.SourceH = JInt(size, "h", f.H);
        }
        return f;
    }

    /// <summary>Round-trip back to TexturePacker-Hash-shaped JSON (frames as
    /// an object, tags and slices under meta), so another tool can consume
    /// what this parser read. Durations, trim offsets and tags survive.</summary>
    public string ToJsonAtlas()
    {
        var frames = new JsonObject();
        foreach (var f in Frames)
        {
            frames[f.Name] = new JsonObject
            {
                ["frame"] = new JsonObject { ["x"] = f.X, ["y"] = f.Y, ["w"] = f.W, ["h"] = f.H },
                ["rotated"] = false,
                ["trimmed"] = f.IsTrimmed,
                ["spriteSourceSize"] = new JsonObject { ["x"] = f.OffsetX, ["y"] = f.OffsetY, ["w"] = f.W, ["h"] = f.H },
                ["sourceSize"] = new JsonObject { ["w"] = f.SourceW, ["h"] = f.SourceH },
                ["duration"] = f.DurationMs,
            };
        }
        var tags = new JsonArray();
        foreach (var t in Tags)
        {
            tags.Add(new JsonObject
            {
                ["name"] = t.Name, ["from"] = t.From, ["to"] = t.To,
                ["direction"] = t.Direction switch
                {
                    SpriteTagDirection.Reverse => "reverse",
                    SpriteTagDirection.PingPong => "pingpong",
                    _ => "forward",
                },
                ["repeat"] = t.Repeat,
            });
        }
        var slices = new JsonArray();
        foreach (var s in Slices)
        {
            var keys = new JsonArray();
            foreach (var k in s.Keys)
            {
                keys.Add(new JsonObject
                {
                    ["frame"] = k.Frame,
                    ["bounds"] = new JsonObject { ["x"] = k.X, ["y"] = k.Y, ["w"] = k.W, ["h"] = k.H },
                    ["pivot"] = new JsonObject { ["x"] = k.PivotX, ["y"] = k.PivotY },
                });
            }
            slices.Add(new JsonObject { ["name"] = s.Name, ["keys"] = keys });
        }
        var root = new JsonObject
        {
            ["frames"] = frames,
            ["meta"] = new JsonObject
            {
                ["app"] = "Geppetto SpriteAnim",
                ["image"] = ImagePath,
                ["size"] = new JsonObject { ["w"] = Width, ["h"] = Height },
                ["frameTags"] = tags,
                ["slices"] = slices,
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>One playable animation: an ordered frame list plus its cycle
/// rule. Pure time math — no rendering, no clock — so the cabinet player
/// and any headless test agree on exactly which frame shows at time T.</summary>
public sealed class SpriteAnimation
{
    public string Name = "";
    public List<SpriteFrame> Frames = new();
    public SpriteLoopMode LoopMode = SpriteLoopMode.Loop;
    public int Fps = 12;
    /// <summary><= 0 means loop forever (ignored for Once, which is one
    /// cycle by definition); > 0 means that many total cycles.</summary>
    public int Repeat;

    public SpriteAnimation() { }
    public SpriteAnimation(string name, List<SpriteFrame> frames, SpriteLoopMode mode = SpriteLoopMode.Loop,
        int fps = 12, int repeat = 0)
    {
        Name = name; Frames = frames; LoopMode = mode; Fps = fps; Repeat = repeat;
    }

    public int DefaultDurationMs => Fps > 0 ? 1000 / Fps : 100;

    public int DurationOf(SpriteFrame f) => f.DurationMs > 0 ? f.DurationMs : DefaultDurationMs;

    /// <summary>One play-order cycle, as frame-list indices. PingPong
    /// expands to 0..n-1..1 (endpoints once); Reverse to n-1..0.</summary>
    public List<int> SequenceIndices()
    {
        var seq = new List<int>();
        int n = Frames.Count;
        if (n == 0) return seq;
        if (LoopMode == SpriteLoopMode.Reverse)
        {
            for (int i = n - 1; i >= 0; i--) seq.Add(i);
            return seq;
        }
        for (int i = 0; i < n; i++) seq.Add(i);
        if (LoopMode == SpriteLoopMode.PingPong && n > 1)
            for (int i = n - 2; i >= 1; i--) seq.Add(i);
        return seq;
    }

    public int CycleDurationMs => SequenceIndices().Sum(i => DurationOf(Frames[i]));

    public bool IsFinite => LoopMode == SpriteLoopMode.Once || Repeat > 0;

    public int TotalDurationMs => IsFinite
        ? CycleDurationMs * (LoopMode == SpriteLoopMode.Once ? 1 : Repeat)
        : int.MaxValue;

    /// <summary>Hold one frame longer by adding milliseconds to it.</summary>
    public void AddHold(int frameIndex, int extraMs)
    {
        if (frameIndex < 0 || frameIndex >= Frames.Count) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (extraMs < 0) throw new ArgumentException("Hold must not be negative.");
        Frames[frameIndex].DurationMs = DurationOf(Frames[frameIndex]) + extraMs;
    }

    /// <summary>Frame-list index showing at elapsedMs. Negative time shows
    /// the first frame of the sequence; past the end of a finite animation
    /// shows its last frame.</summary>
    public int FrameIndexAt(int elapsedMs)
    {
        if (Frames.Count == 0) throw new InvalidOperationException("Sprite animation has no frames.");
        var seq = SequenceIndices();
        int cycle = CycleDurationMs;
        if (cycle <= 0) return seq[0];
        if (elapsedMs <= 0) return seq[0];
        if (IsFinite && elapsedMs >= TotalDurationMs) return seq[seq.Count - 1];
        int t = elapsedMs % cycle;
        int acc = 0;
        foreach (var idx in seq)
        {
            acc += DurationOf(Frames[idx]);
            if (t < acc) return idx;
        }
        return seq[seq.Count - 1];
    }

    public SpriteFrame FrameAt(int elapsedMs) => Frames[FrameIndexAt(elapsedMs)];

    /// <summary>Start time (ms) of each entry in one cycle, in play order.</summary>
    public List<int> SequenceStartTimes()
    {
        var starts = new List<int>();
        int acc = 0;
        foreach (var idx in SequenceIndices())
        {
            starts.Add(acc);
            acc += DurationOf(Frames[idx]);
        }
        return starts;
    }
}

/// <summary>Stateful playback over a SpriteAnimation: play/pause, seek,
/// step frame by frame (in play order), and a finished flag for finite
/// animations. The cabinet player drives this shape; the QML player
/// mirrors the same sequence math in JS.</summary>
public sealed class SpritePlayback
{
    public SpriteAnimation Animation;
    public bool Playing;
    public int ElapsedMs;

    public SpritePlayback(SpriteAnimation animation)
    {
        Animation = animation ?? throw new ArgumentNullException(nameof(animation));
    }

    public bool Finished => Animation.IsFinite && ElapsedMs >= Animation.TotalDurationMs;
    public SpriteFrame CurrentFrame => Animation.FrameAt(ElapsedMs);
    public int CurrentFrameIndex => Animation.FrameIndexAt(ElapsedMs);

    public void Play() => Playing = true;
    public void Pause() => Playing = false;
    public void Reset() { ElapsedMs = 0; Playing = false; }
    public void Seek(int ms) => ElapsedMs = Math.Max(0, Animation.IsFinite ? Math.Min(ms, Animation.TotalDurationMs) : ms);

    /// <summary>Advance the clock; a finished finite animation stops on
    /// its last frame instead of wrapping.</summary>
    public void Advance(int deltaMs)
    {
        if (!Playing || deltaMs <= 0) return;
        ElapsedMs += deltaMs;
        if (Animation.IsFinite && ElapsedMs >= Animation.TotalDurationMs)
        {
            ElapsedMs = Animation.TotalDurationMs;
            Playing = false;
        }
    }

    int SequencePosition()
    {
        var seq = Animation.SequenceIndices();
        var starts = Animation.SequenceStartTimes();
        int cycle = Animation.CycleDurationMs;
        if (cycle <= 0 || seq.Count == 0) return 0;
        int cycleIndex = Animation.IsFinite ? Math.Min(ElapsedMs / cycle, Math.Max(0, (Animation.TotalDurationMs / cycle) - 1)) : ElapsedMs / cycle;
        int within = ElapsedMs - cycleIndex * cycle;
        int pos = 0;
        for (int i = 0; i < starts.Count; i++)
            if (within >= starts[i]) pos = i;
        return pos;
    }

    /// <summary>Step one frame forward in play order (wraps for looping
    /// animations, clamps at the end for finite ones).</summary>
    public void StepForward()
    {
        var seq = Animation.SequenceIndices();
        var starts = Animation.SequenceStartTimes();
        int cycle = Animation.CycleDurationMs;
        if (seq.Count == 0 || cycle <= 0) return;
        int pos = SequencePosition();
        int cycleIndex = Animation.IsFinite ? Math.Min(ElapsedMs / cycle, Math.Max(0, (Animation.TotalDurationMs / cycle) - 1)) : ElapsedMs / cycle;
        if (pos + 1 < seq.Count)
            ElapsedMs = cycleIndex * cycle + starts[pos + 1];
        else if (Animation.IsFinite)
            ElapsedMs = Animation.TotalDurationMs;
        else
            ElapsedMs = (cycleIndex + 1) * cycle;
    }

    /// <summary>Step one frame backward in play order (wraps to the last
    /// frame for looping animations, clamps at 0 for finite ones).</summary>
    public void StepBack()
    {
        var seq = Animation.SequenceIndices();
        var starts = Animation.SequenceStartTimes();
        int cycle = Animation.CycleDurationMs;
        if (seq.Count == 0 || cycle <= 0) return;
        int pos = SequencePosition();
        int cycleIndex = Animation.IsFinite ? Math.Min(ElapsedMs / cycle, Math.Max(0, (Animation.TotalDurationMs / cycle) - 1)) : ElapsedMs / cycle;
        if (pos > 0)
            ElapsedMs = cycleIndex * cycle + starts[pos - 1];
        else if (Animation.IsFinite || cycleIndex == 0)
            ElapsedMs = 0;
        else
            ElapsedMs = (cycleIndex - 1) * cycle + starts[seq.Count - 1];
    }
}

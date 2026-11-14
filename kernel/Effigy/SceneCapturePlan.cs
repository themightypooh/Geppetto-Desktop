using System;

namespace Effigy;

/// <summary>
/// S&amp;box Lane 1a — the ENGINE-FREE half of the GPU SceneCapture shell.
///
/// The shell itself (Editor/EffigyEditor/EffigySceneCapture.cs) drives the viewport's
/// SceneRenderingWidget through these frames; this file is the part that can be proven
/// headless: which way each canonical camera looks, how far it sits, and where every
/// view/pass lands on the contact sheet.
///
/// COORDINATES — s&amp;box, as EffigyViewport documents them: +X forward, +Y left, +Z up.
/// A "Front" camera therefore sits on the +X side of the model looking back along -X,
/// a "Side" camera on the +Y side, "Back" on -X, and the 3/4 view on the front-left-top
/// diagonal (1, 1, 1), which is the classic isometric elevation asin(1/sqrt(3)) = 35.26deg.
///
/// FRAMING PARITY — FitDistance mirrors EffigyViewport.FrameCamera exactly: the
/// bounding-sphere radius over sin(FOV/2), times the 1.2 margin, widened for a
/// portrait canvas by 1/min(1, aspect). The viewport floors a degenerate radius to
/// 0.001 before framing; this plan throws on radius &lt;= 0 instead, so a degenerate
/// model fails loudly here and the shell does the flooring, in the open.
///
/// TIMING — nothing here measures GPU time. The spec's &lt;20ms figure is an engine-side
/// claim and stays UNVERIFIED until the shell runs inside the editor (see the shell).
/// </summary>
public enum CaptureView
{
	Front,
	Side,
	Back,
	Isometric,
}

/// <summary>One render pass per sheet row, in the spec's order.</summary>
public enum CapturePass
{
	Lit,
	Normal,
	Wireframe,
	AmbientOcclusion,
}

/// <summary>
/// One canonical camera: where it sits and which way it looks, in kernel vectors.
/// Forward points INTO the scene (camera to model); Right/Up are the camera's own
/// axes, orthonormal by construction: Right = (Forward x WorldUp) normalised,
/// Up = Right x Forward.
/// </summary>
public readonly struct CameraFrame
{
	public readonly CaptureView View;
	public readonly Vec3 Direction;
	public readonly Vec3 Position;
	public readonly Vec3 Forward;
	public readonly Vec3 Right;
	public readonly Vec3 Up;
	public readonly float Distance;
	public readonly float FieldOfViewDegrees;

	public CameraFrame( CaptureView view, Vec3 direction, Vec3 position, Vec3 forward, Vec3 right, Vec3 up, float distance, float fieldOfViewDegrees )
	{
		View = view;
		Direction = direction;
		Position = position;
		Forward = forward;
		Right = right;
		Up = up;
		Distance = distance;
		FieldOfViewDegrees = fieldOfViewDegrees;
	}
}

/// <summary>One cell of the contact sheet: a (view, pass) pair at a pixel rectangle.</summary>
public readonly struct SheetCell
{
	public readonly CaptureView View;
	public readonly CapturePass Pass;
	public readonly int Column;
	public readonly int Row;
	public readonly int X;
	public readonly int Y;
	public readonly int Width;
	public readonly int Height;

	public SheetCell( CaptureView view, CapturePass pass, int column, int row, int x, int y, int width, int height )
	{
		View = view;
		Pass = pass;
		Column = column;
		Row = row;
		X = x;
		Y = y;
		Width = width;
		Height = height;
	}
}

public static class SceneCapturePlan
{
	/// <summary>The spec's sheet: 512 x 512, four views across, four passes down.</summary>
	public const int DefaultSheetSize = 512;

	public const int ViewCount = 4;
	public const int PassCount = 4;
	public const int CellCount = ViewCount * PassCount;

	/// <summary>The viewport camera's field of view (EffigyViewport constructor).</summary>
	public const float DefaultFieldOfViewDegrees = 45f;

	/// <summary>The viewport's framing margin (FrameCamera: distance * 1.2).</summary>
	public const float DefaultMargin = 1.2f;

	public static readonly Vec3 WorldUp = new( 0f, 0f, 1f );

	/// <summary>Unit direction from the model centre OUT to the camera, per view.</summary>
	public static Vec3 ViewDirection( CaptureView view )
	{
		switch ( view )
		{
			case CaptureView.Front: return new Vec3( 1f, 0f, 0f );
			case CaptureView.Side: return new Vec3( 0f, 1f, 0f );
			case CaptureView.Back: return new Vec3( -1f, 0f, 0f );
			case CaptureView.Isometric: return new Vec3( 1f, 1f, 1f ).Normal;
			default: throw new ArgumentOutOfRangeException( nameof( view ), view, "unknown capture view" );
		}
	}

	/// <summary>
	/// Camera distance that fits a bounding sphere of <paramref name="radius"/> in the
	/// vertical field of view, exactly as EffigyViewport.FrameCamera frames it.
	/// A portrait canvas (aspect &lt; 1) pushes the camera back by 1/aspect.
	/// </summary>
	public static float FitDistance( float radius, float fieldOfViewDegrees = DefaultFieldOfViewDegrees, float aspect = 1f, float margin = DefaultMargin )
	{
		if ( radius <= 0f )
			throw new ArgumentOutOfRangeException( nameof( radius ), "bounding radius must be positive; the shell floors degenerate bounds to 0.001 before calling" );
		if ( fieldOfViewDegrees <= 0f || fieldOfViewDegrees >= 180f )
			throw new ArgumentOutOfRangeException( nameof( fieldOfViewDegrees ), "field of view must be between 0 and 180 degrees" );
		if ( aspect <= 0f )
			throw new ArgumentOutOfRangeException( nameof( aspect ), "canvas aspect (width / height) must be positive" );
		if ( margin <= 0f )
			throw new ArgumentOutOfRangeException( nameof( margin ), "framing margin must be positive" );

		var halfFovRadians = fieldOfViewDegrees * MathF.PI / 360f;
		return radius / MathF.Sin( halfFovRadians ) * margin / MathF.Min( 1f, aspect );
	}

	/// <summary>Full camera frame for one view: position, look direction and camera axes.</summary>
	public static CameraFrame CameraFor( CaptureView view, Vec3 center, float radius, float fieldOfViewDegrees = DefaultFieldOfViewDegrees, float aspect = 1f, float margin = DefaultMargin )
	{
		var direction = ViewDirection( view ).Normal;
		var distance = FitDistance( radius, fieldOfViewDegrees, aspect, margin );
		var forward = -direction;
		var right = Vec3.Cross( forward, WorldUp ).Normal;
		var up = Vec3.Cross( right, forward ).Normal;
		var position = center + direction * distance;
		return new CameraFrame( view, direction, position, forward, right, up, distance, fieldOfViewDegrees );
	}

	/// <summary>Cell edge in pixels. The sheet must split evenly into 4 x 4 cells.</summary>
	public static int CellSize( int sheetSize = DefaultSheetSize )
	{
		if ( sheetSize < ViewCount || sheetSize % ViewCount != 0 )
			throw new ArgumentOutOfRangeException( nameof( sheetSize ), "sheet size must be a multiple of 4 so the 4 x 4 grid has whole-pixel cells (512 -> 128px cells)" );
		return sheetSize / ViewCount;
	}

	/// <summary>
	/// One sheet cell: columns are views (Front, Side, Back, Isometric, left to right),
	/// rows are passes (Lit, Normal, Wireframe, AO, top to bottom).
	/// </summary>
	public static SheetCell CellFor( CaptureView view, CapturePass pass, int sheetSize = DefaultSheetSize )
	{
		var cell = CellSize( sheetSize );
		var column = (int)view;
		var row = (int)pass;
		return new SheetCell( view, pass, column, row, column * cell, row * cell, cell, cell );
	}

	/// <summary>All 16 cells in row-major order: pass by pass, views left to right.</summary>
	public static SheetCell[] AllCells( int sheetSize = DefaultSheetSize )
	{
		var cells = new SheetCell[CellCount];
		var i = 0;
		for ( var pass = 0; pass < PassCount; pass++ )
			for ( var view = 0; view < ViewCount; view++ )
				cells[i++] = CellFor( (CaptureView)view, (CapturePass)pass, sheetSize );
		return cells;
	}
}

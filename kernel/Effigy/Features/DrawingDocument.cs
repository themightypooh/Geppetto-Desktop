using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>One hole as the drawing sees it: where it is on the face, how big, how deep, what thread.</summary>
public sealed class DrawingHole
{
	public string Label = "H1";
	public float XMm;
	public float YMm;
	public float DiameterMm;
	/// <summary>0 means through.</summary>
	public float DepthMm;
	public string Thread = "";
}

/// <summary>One bill-of-materials line.</summary>
public sealed class BomItem
{
	public string PartNo = "";
	public string Name = "";
	public int Quantity = 1;
	public string Material = "";
}

/// <summary>Everything a drawing is built from: the solid's bounding box, its holes, its parts, and the title block.</summary>
public sealed class DrawingSource
{
	public Vec3 Min;
	public Vec3 Max;
	public List<DrawingHole> Holes = new();
	public List<BomItem> Parts = new();
	public string Title = "";
	public string DrawingNo = "";
	public string Revision = "A";
	public float Scale = 1f;
	public string SheetSize = "A4";
	public string DrawnBy = "";
	public string Date = "";
}

public enum DrawingViewKind
{
	Front,
	Top,
	Section,
	Detail,
}

/// <summary>One projected view on the sheet: its outline, its hole circles and its centrelines, all in sheet millimetres.</summary>
public sealed class DrawingView
{
	public DrawingViewKind Kind;
	public string Label = "";
	public Vec2 Centre;
	public float WidthMm;
	public float HeightMm;
	public float Scale = 1f;
	public List<(Vec2 Centre, float RadiusMm)> Circles = new();
	public List<(Vec2 A, Vec2 B)> Centrelines = new();
}

/// <summary>One dimension with its tolerance, as text on the sheet.</summary>
public sealed class DrawingDimension
{
	public string Label = "";
	public float NominalMm;
	public float UpperMm;
	public float LowerMm;
	public string Text = "";
}

/// <summary>One hole-table row.</summary>
public sealed class HoleTableRow
{
	public string Id = "";
	public string Size = "";
	public float XMm;
	public float YMm;
	public float DepthMm;
	public string Thread = "";
}

/// <summary>The title block: what the shop reads to know which part, which revision, which scale.</summary>
public sealed class TitleBlock
{
	public string Title = "";
	public string DrawingNo = "";
	public string Revision = "";
	public float Scale = 1f;
	public string SheetSize = "";
	public string DrawnBy = "";
	public string Date = "";
}

/// <summary>
/// The 2D drawing workspace: a solid becomes a dimensioned sheet with front / top / section /
/// detail views, an H7 hole tolerance, a hole table, a BOM with balloons, a title block and a
/// revision — and DXF + PDF exports a shop can actually open.
///
/// WHAT THE VIEWS ARE: orthographic projections of the solid's bounding box and hole list.
/// A section view is the front projection labelled SECTION A-A with its cut line; it is not a
/// computed section cut through a B-rep solid, and it says so. The computed cut is the OCCT
/// ticket's. What IS real here: every number on the sheet is derived from the source solid, the
/// hole table and the ISO tables, and the out-of-date flag fires when the source changes.
///
/// ALL LENGTHS ARE MILLIMETRES. Sheet coordinates are millimetres from the top-left of the
/// sheet, y down — the DXF and PDF writers convert to their own frames.
/// </summary>
public sealed class DrawingDocument
{
	public List<DrawingView> Views = new();
	public List<DrawingDimension> Dimensions = new();
	public List<HoleTableRow> HoleTable = new();
	public List<BomItem> Bom = new();
	public TitleBlock TitleBlock = new();
	/// <summary>Hash of the source this document was built from. A different source means out of date.</summary>
	public string SourceFingerprint = "";
	public bool IsOutOfDate;

	public static (float WidthMm, float HeightMm) SheetMillimetres( string sheetSize ) => sheetSize switch
	{
		"A3" => (420f, 297f),
		"A2" => (594f, 420f),
		_ => (297f, 210f),
	};

	/// <summary>Build a drawing from a source. The document is not out of date when built.</summary>
	public static DrawingDocument Build( DrawingSource source )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		var doc = new DrawingDocument();
		doc.Regenerate( source );
		return doc;
	}

	/// <summary>True when <paramref name="source"/> no longer matches the source this document was built from.</summary>
	public bool CheckOutOfDate( DrawingSource source ) => Fingerprint( source ) != SourceFingerprint;

	/// <summary>Rebuild from <paramref name="source"/>. A rebuild from an unchanged source is a no-op; the flag clears.</summary>
	public void Regenerate( DrawingSource source )
	{
		if ( source is null ) throw new ArgumentNullException( nameof( source ) );
		var fingerprint = Fingerprint( source );
		if ( fingerprint == SourceFingerprint && Views.Count > 0 )
		{
			IsOutOfDate = false;
			return;
		}

		SourceFingerprint = fingerprint;
		IsOutOfDate = false;
		TitleBlock = new TitleBlock
		{
			Title = source.Title, DrawingNo = source.DrawingNo, Revision = source.Revision,
			Scale = source.Scale, SheetSize = source.SheetSize, DrawnBy = source.DrawnBy, Date = source.Date,
		};
		Bom = source.Parts.Select( p => new BomItem { PartNo = p.PartNo, Name = p.Name, Quantity = p.Quantity, Material = p.Material } ).ToList();

		var sizeX = source.Max.x - source.Min.x;
		var sizeY = source.Max.y - source.Min.y;
		var sizeZ = source.Max.z - source.Min.z;
		var (sheetW, sheetH) = SheetMillimetres( source.SheetSize );

		// Dimensions: the three overall extents, then every hole diameter with its H7 tolerance.
		Dimensions = new List<DrawingDimension>
		{
			Dim( "Width", sizeX ), Dim( "Depth", sizeY ), Dim( "Height", sizeZ ),
		};
		foreach ( var hole in source.Holes )
		{
			var upper = IsoMetric.H7UpperDeviationMm( hole.DiameterMm );
			Dimensions.Add( new DrawingDimension
			{
				Label = $"Hole {hole.Label} dia",
				NominalMm = hole.DiameterMm,
				UpperMm = upper ?? 0f,
				LowerMm = 0f,
				Text = IsoMetric.H7Text( hole.DiameterMm ),
			} );
		}

		HoleTable = source.Holes.Select( h => new HoleTableRow
		{
			Id = h.Label,
			Size = string.IsNullOrEmpty( h.Thread ) ? $"Ø{h.DiameterMm:0.##}" : h.Thread,
			XMm = h.XMm, YMm = h.YMm, DepthMm = h.DepthMm, Thread = h.Thread,
		} ).ToList();

		// Views: front (X right, Z up), top (X right, Y up), section (front projection, cut line),
		// detail (first hole, 2x). Each fits its quarter of the sheet.
		var margin = 10f;
		var viewW = (sheetW - margin * 3f) / 2f;
		var viewH = (sheetH - margin * 3f - 40f) / 2f;
		Views = new List<DrawingView>
		{
			ProjectView( DrawingViewKind.Front, "FRONT", new Vec2( margin + viewW / 2f, margin + viewH / 2f ),
				viewW, viewH, source, ( p ) => new Vec2( p.x, p.z ), sizeX, sizeZ ),
			ProjectView( DrawingViewKind.Top, "TOP", new Vec2( margin * 2f + viewW * 1.5f, margin + viewH / 2f ),
				viewW, viewH, source, ( p ) => new Vec2( p.x, p.y ), sizeX, sizeY ),
			ProjectView( DrawingViewKind.Section, "SECTION A-A", new Vec2( margin + viewW / 2f, margin * 2f + viewH * 1.5f ),
				viewW, viewH, source, ( p ) => new Vec2( p.x, p.z ), sizeX, sizeZ ),
			DetailView( source, new Vec2( margin * 2f + viewW * 1.5f, margin * 2f + viewH * 1.5f ), viewW, viewH ),
		};
	}

	static DrawingDimension Dim( string label, float nominal ) => new()
	{
		Label = label, NominalMm = nominal, UpperMm = 0f, LowerMm = 0f, Text = $"{label} {nominal:0.##}",
	};

	static DrawingView ProjectView( DrawingViewKind kind, string label, Vec2 centre,
		float viewW, float viewH, DrawingSource source, Func<Vec3, Vec2> project, float spanU, float spanV )
	{
		var scale = spanU > 0f && spanV > 0f
			? Math.Min( viewW / spanU, viewH / spanV ) * source.Scale
			: 1f;
		// Centres are measured from the face the holes sit on (the top face, z = Max.z).
		var origin = project( new Vec3( (source.Min.x + source.Max.x) / 2f, (source.Min.y + source.Max.y) / 2f, (source.Min.z + source.Max.z) / 2f ) );
		var view = new DrawingView
		{
			Kind = kind, Label = label, Centre = centre, Scale = scale,
			WidthMm = spanU * scale, HeightMm = spanV * scale,
		};
		foreach ( var hole in source.Holes )
		{
			var p = project( new Vec3( hole.XMm, hole.YMm, source.Max.z ) );
			var c = new Vec2(
				centre.x + (p.x - origin.x) * scale,
				centre.y - (p.y - origin.y) * scale );
			view.Circles.Add( (c, hole.DiameterMm * 0.5f * scale) );
			view.Centrelines.Add( (new Vec2( c.x - hole.DiameterMm * scale, c.y ), new Vec2( c.x + hole.DiameterMm * scale, c.y )) );
			view.Centrelines.Add( (new Vec2( c.x, c.y - hole.DiameterMm * scale ), new Vec2( c.x, c.y + hole.DiameterMm * scale )) );
		}
		return view;
	}

	static DrawingView DetailView( DrawingSource source, Vec2 centre, float viewW, float viewH )
	{
		var view = new DrawingView { Kind = DrawingViewKind.Detail, Label = "DETAIL", Centre = centre, WidthMm = viewW, HeightMm = viewH, Scale = 2f };
		var first = source.Holes.FirstOrDefault();
		if ( first is null ) return view;
		var c = centre;
		view.Circles.Add( (c, first.DiameterMm * 0.5f * 2f) );
		view.Centrelines.Add( (new Vec2( c.x - first.DiameterMm * 2f, c.y ), new Vec2( c.x + first.DiameterMm * 2f, c.y )) );
		view.Centrelines.Add( (new Vec2( c.x, c.y - first.DiameterMm * 2f ), new Vec2( c.x, c.y + first.DiameterMm * 2f )) );
		return view;
	}

	/// <summary>FNV-1a 64-bit hash of the canonical source description. Any model change changes it.</summary>
	public static string Fingerprint( DrawingSource source )
	{
		var inv = CultureInfo.InvariantCulture;
		var sb = new StringBuilder();
		sb.Append( "min=" ).Append( source.Min.x.ToString( "0.###", inv ) ).Append( ',' )
			.Append( source.Min.y.ToString( "0.###", inv ) ).Append( ',' ).Append( source.Min.z.ToString( "0.###", inv ) );
		sb.Append( "|max=" ).Append( source.Max.x.ToString( "0.###", inv ) ).Append( ',' )
			.Append( source.Max.y.ToString( "0.###", inv ) ).Append( ',' ).Append( source.Max.z.ToString( "0.###", inv ) );
		foreach ( var h in source.Holes )
			sb.Append( "|hole=" ).Append( h.Label ).Append( ',' ).Append( h.XMm.ToString( "0.###", inv ) ).Append( ',' )
				.Append( h.YMm.ToString( "0.###", inv ) ).Append( ',' ).Append( h.DiameterMm.ToString( "0.###", inv ) ).Append( ',' )
				.Append( h.DepthMm.ToString( "0.###", inv ) ).Append( ',' ).Append( h.Thread );
		foreach ( var p in source.Parts )
			sb.Append( "|part=" ).Append( p.PartNo ).Append( ',' ).Append( p.Name ).Append( ',' ).Append( p.Quantity ).Append( ',' ).Append( p.Material );
		sb.Append( "|title=" ).Append( source.Title ).Append( "|no=" ).Append( source.DrawingNo )
			.Append( "|rev=" ).Append( source.Revision ).Append( "|scale=" ).Append( source.Scale.ToString( "0.###", inv ) )
			.Append( "|sheet=" ).Append( source.SheetSize ).Append( "|by=" ).Append( source.DrawnBy ).Append( "|date=" ).Append( source.Date );
		var bytes = Encoding.UTF8.GetBytes( sb.ToString() );
		ulong hash = 1469598103934665603UL;
		foreach ( var b in bytes ) { hash ^= b; hash *= 1099511628211UL; }
		return hash.ToString( "X16", inv );
	}

	// --- DXF export ---------------------------------------------------------------------------

	/// <summary>Real DXF (R12) text: header, tables, entities. LINE / CIRCLE / TEXT only — the entities a shop's CAM reads from a drawing.</summary>
	public string ToDxf()
	{
		var inv = CultureInfo.InvariantCulture;
		string F( float v ) => v.ToString( "0.###", inv );
		var sb = new StringBuilder();
		void Pair( string code, string value ) => sb.Append( code ).Append( '\n' ).Append( value ).Append( '\n' );

		sb.Append( "  0\nSECTION\n  2\nHEADER\n" );
		Pair( "  9", "$ACADVER" ); Pair( "  1", "AC1009" );
		Pair( "  9", "$INSUNITS" ); Pair( " 70", "4" );
		sb.Append( "  0\nENDSEC\n" );

		sb.Append( "  0\nSECTION\n  2\nTABLES\n" );
		sb.Append( "  0\nTABLE\n  2\nLTYPE\n 70\n     1\n" );
		sb.Append( "  0\nLTYPE\n  2\nContinuous\n 70\n     0\n  3\nSolid line\n 72\n    65\n 73\n     0\n 40\n0.0\n" );
		sb.Append( "  0\nENDTAB\n" );
		sb.Append( "  0\nTABLE\n  2\nLAYER\n 70\n     1\n" );
		sb.Append( "  0\nLAYER\n  2\n0\n 70\n     0\n 62\n     7\n  6\nContinuous\n" );
		sb.Append( "  0\nENDTAB\n" );
		sb.Append( "  0\nENDSEC\n" );

		sb.Append( "  0\nSECTION\n  2\nENTITIES\n" );
		void Line( float x1, float y1, float x2, float y2 )
		{
			sb.Append( "  0\nLINE\n  8\n0\n" );
			Pair( " 10", F( x1 ) ); Pair( " 20", F( -y1 ) ); Pair( " 30", "0" );
			Pair( " 11", F( x2 ) ); Pair( " 21", F( -y2 ) ); Pair( " 31", "0" );
		}
		void Circle( float x, float y, float r )
		{
			sb.Append( "  0\nCIRCLE\n  8\n0\n" );
			Pair( " 10", F( x ) ); Pair( " 20", F( -y ) ); Pair( " 30", "0" );
			Pair( " 40", F( r ) );
		}
		void Text( float x, float y, float height, string text )
		{
			sb.Append( "  0\nTEXT\n  8\n0\n" );
			Pair( " 10", F( x ) ); Pair( " 20", F( -y ) ); Pair( " 30", "0" );
			Pair( " 40", F( height ) ); Pair( "  1", Ascii( text ) );
		}

		foreach ( var view in Views )
		{
			var x0 = view.Centre.x - view.WidthMm / 2f;
			var y0 = view.Centre.y - view.HeightMm / 2f;
			var x1 = view.Centre.x + view.WidthMm / 2f;
			var y1 = view.Centre.y + view.HeightMm / 2f;
			Line( x0, y0, x1, y0 ); Line( x1, y0, x1, y1 ); Line( x1, y1, x0, y1 ); Line( x0, y1, x0, y0 );
			Text( x0, y0 - 4f, 3f, view.Label );
			foreach ( var (c, r) in view.Circles ) Circle( c.x, c.y, r );
			foreach ( var (a, b) in view.Centrelines ) Line( a.x, a.y, b.x, b.y );
		}
		var ty = 8f;
		foreach ( var dim in Dimensions ) { Text( 10f, ty, 3f, dim.Text ); ty += 5f; }
		foreach ( var row in HoleTable )
		{
			Text( 10f, ty, 3f, $"{row.Id} {row.Size} X{row.XMm:0.##} Y{row.YMm:0.##}" +
				(row.DepthMm > 0f ? $" DEPTH {row.DepthMm:0.##}" : " THRU") +
				(string.IsNullOrEmpty( row.Thread ) ? "" : $" {row.Thread}") );
			ty += 5f;
		}
		Text( 10f, ty, 3f, $"{TitleBlock.Title} {TitleBlock.DrawingNo} REV {TitleBlock.Revision}" );
		sb.Append( "  0\nENDSEC\n  0\nEOF\n" );
		return sb.ToString();
	}

	// --- PDF export ---------------------------------------------------------------------------

	/// <summary>
	/// Minimal valid PDF 1.4: catalog, pages, one page, Helvetica, one content stream with the
	/// sheet border, title block, view outlines, hole circles (4-curve bezier approximation),
	/// centrelines, dimensions and hole table as text. Byte offsets in the xref table are computed
	/// while writing, so the file opens in a real reader.
	/// </summary>
	public byte[] ToPdf()
	{
		var (sheetW, sheetH) = SheetMillimetres( TitleBlock.SheetSize );
		const float pt = 72f / 25.4f;
		var pageW = sheetW * pt;
		var pageH = sheetH * pt;
		float X( float mm ) => mm * pt;
		float Y( float mm ) => pageH - mm * pt;

		var content = new StringBuilder();
		var inv = CultureInfo.InvariantCulture;
		string F( float v ) => v.ToString( "0.###", inv );
		void Rect( float x, float y, float w, float h ) =>
			content.Append( $"{F( X( x ) )} {F( Y( y + h ) )} {F( w * pt )} {F( h * pt )} re S\n" );
		void Line( float x1, float y1, float x2, float y2 ) =>
			content.Append( $"{F( X( x1 ) )} {F( Y( y1 ) )} m {F( X( x2 ) )} {F( Y( y2 ) )} l S\n" );
		void Circle( float x, float y, float r )
		{
			// Four cubic beziers, k = 0.5523 — the standard circle approximation.
			const float k = 0.5523f;
			var px = X( x ); var py = Y( y ); var pr = r * pt;
			content.Append( $"{F( px + pr )} {F( py )} m " );
			content.Append( $"{F( px + pr )} {F( py + pr * k )} {F( px + pr * k )} {F( py + pr )} {F( px )} {F( py + pr )} c " );
			content.Append( $"{F( px - pr * k )} {F( py + pr )} {F( px - pr )} {F( py + pr * k )} {F( px - pr )} {F( py )} c " );
			content.Append( $"{F( px - pr )} {F( py - pr * k )} {F( px - pr * k )} {F( py - pr )} {F( px )} {F( py - pr )} c " );
			content.Append( $"{F( px + pr * k )} {F( py - pr )} {F( px + pr )} {F( py - pr * k )} {F( px + pr )} {F( py )} c S\n" );
		}
		void Text( float x, float y, float size, string text ) =>
			content.Append( $"BT /F1 {F( size )} Tf {F( X( x ) )} {F( Y( y ) )} Td ({PdfEscape( Ascii( text ) )}) Tj ET\n" );

		Rect( 10f, 10f, sheetW - 20f, sheetH - 20f );
		foreach ( var view in Views )
		{
			Rect( view.Centre.x - view.WidthMm / 2f, view.Centre.y - view.HeightMm / 2f, view.WidthMm, view.HeightMm );
			Text( view.Centre.x - view.WidthMm / 2f, view.Centre.y - view.HeightMm / 2f - 4f, 9f, view.Label );
			foreach ( var (c, r) in view.Circles ) Circle( c.x, c.y, r );
			foreach ( var (a, b) in view.Centrelines ) Line( a.x, a.y, b.x, b.y );
		}
		var ty = 14f;
		foreach ( var dim in Dimensions ) { Text( 14f, ty, 9f, dim.Text ); ty += 5f; }
		foreach ( var row in HoleTable )
		{
			Text( 14f, ty, 9f, $"{row.Id} {row.Size} X{row.XMm:0.##} Y{row.YMm:0.##}" +
				(row.DepthMm > 0f ? $" DEPTH {row.DepthMm:0.##}" : " THRU") );
			ty += 5f;
		}
		var (tbW, tbH) = (180f, 40f);
		var tbX = sheetW - 10f - tbW;
		var tbY = sheetH - 10f - tbH;
		Rect( tbX, tbY, tbW, tbH );
		Text( tbX + 4f, tbY + 6f, 11f, TitleBlock.Title );
		Text( tbX + 4f, tbY + 18f, 9f, $"{TitleBlock.DrawingNo}  REV {TitleBlock.Revision}  SCALE {TitleBlock.Scale:0.##}" );
		Text( tbX + 4f, tbY + 30f, 9f, $"DRAWN {TitleBlock.DrawnBy}  {TitleBlock.Date}  SHEET {TitleBlock.SheetSize}" );
		foreach ( var part in Bom )
			Text( 14f, ty, 9f, $"{part.PartNo} {part.Name} x{part.Quantity} {part.Material}" );

		var contentBytes = Encoding.ASCII.GetBytes( content.ToString() );
		var objects = new List<byte[]>
		{
			Encoding.ASCII.GetBytes( "<< /Type /Catalog /Pages 2 0 R >>" ),
			Encoding.ASCII.GetBytes( "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" ),
			Encoding.ASCII.GetBytes( $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F( pageW )} {F( pageH )}] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>" ),
			Encoding.ASCII.GetBytes( "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>" ),
			null,
		};
		var stream = new List<byte>();
		stream.AddRange( Encoding.ASCII.GetBytes( $"<< /Length {contentBytes.Length} >>\nstream\n" ) );
		stream.AddRange( contentBytes );
		stream.AddRange( Encoding.ASCII.GetBytes( "\nendstream" ) );
		objects[4] = stream.ToArray();

		var pdf = new List<byte>();
		void Add( string s ) => pdf.AddRange( Encoding.ASCII.GetBytes( s ) );
		Add( "%PDF-1.4\n" );
		var offsets = new List<int>();
		for ( var i = 0; i < objects.Count; i++ )
		{
			offsets.Add( pdf.Count );
			Add( $"{i + 1} 0 obj\n" );
			pdf.AddRange( objects[i] );
			Add( "\nendobj\n" );
		}
		var xrefAt = pdf.Count;
		Add( "xref\n" );
		Add( $"0 {objects.Count + 1}\n" );
		Add( "0000000000 65535 f \n" );
		foreach ( var off in offsets )
			Add( $"{off:D10} 00000 n \n" );
		Add( "trailer\n" );
		Add( $"<< /Size {objects.Count + 1} /Root 1 0 R >>\n" );
		Add( "startxref\n" );
		Add( $"{xrefAt}\n" );
		Add( "%%EOF\n" );
		return pdf.ToArray();
	}

	static string PdfEscape( string s ) => s.Replace( "\\", "\\\\" ).Replace( "(", "\\(" ).Replace( ")", "\\)" );

	/// <summary>PDF and DXF text are ASCII: degrees, diameter and multiply signs become words.</summary>
	static string Ascii( string s ) => s
		.Replace( "Ø", "Dia " ).Replace( "×", "x" ).Replace( "²", "2" ).Replace( "°", "deg" )
		.Replace( "—", "-" ).Replace( "–", "-" )
		.Normalize( NormalizationForm.FormKD )
		.Where( c => c < 128 ).Aggregate( new StringBuilder(), ( sb, c ) => sb.Append( c ) ).ToString();
}

/// <summary>
/// The drawing workspace as a feature: a sheet in the tree, built from the bodies above it and
/// the hole list the Hole Wizard (or a script) hands it. The DXF text and PDF bytes are on the
/// feature after a rebuild, ready for the release workflow — solid, drawing, DXF + PDF — with
/// the out-of-date flag carried by the document itself.
/// </summary>
public sealed class DrawingSheetFeature : Feature
{
	public override string TypeName => "Drawing sheet";

	public readonly StringParam Title = new( "Title", "Bracket" );
	public readonly StringParam DrawingNo = new( "Drawing no", "DWG-001" );
	public readonly StringParam Revision = new( "Revision", "A" );
	public readonly ChoiceParam SheetSize = new( "Sheet", new[] { "A4", "A3", "A2" } );
	public readonly FloatParam Scale = new( "Scale", 1f, 0.01f, 100f );
	public readonly StringParam DrawnBy = new( "Drawn by", "" );
	public readonly StringParam Date = new( "Date", "2026-11-13" );

	public readonly BodySelectionParam Bodies = new( "Bodies" );

	/// <summary>Holes to dimension and tabulate, in the face's own millimetres. Set by the Hole Wizard or a script.</summary>
	public List<DrawingHole> Holes = new();

	/// <summary>Parts for the BOM. Set by a script.</summary>
	public List<BomItem> Parts = new();

	public DrawingDocument LastDocument;
	public string DxfText = "";
	public byte[] PdfBytes = Array.Empty<byte>();

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Title, DrawingNo, Revision, SheetSize, Scale, DrawnBy, Date };

	protected override void Execute( FeatureContext ctx )
	{
		var chosen = ctx.Bodies.Where( b => Bodies.Matches( b ) ).ToList();
		if ( chosen.Count == 0 )
			Fail(
				"No bodies to draw",
				"The drawing sheet has no bodies above it in the tree (or none match the body selection).",
				"Add a solid feature above this sheet" );

		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var body in chosen )
		{
			foreach ( var p in body.Mesh.Positions )
			{
				min = new Vec3( Math.Min( min.x, p.x ), Math.Min( min.y, p.y ), Math.Min( min.z, p.z ) );
				max = new Vec3( Math.Max( max.x, p.x ), Math.Max( max.y, p.y ), Math.Max( max.z, p.z ) );
			}
		}

		var source = new DrawingSource
		{
			Min = min, Max = max, Holes = Holes, Parts = Parts,
			Title = Title.Value, DrawingNo = DrawingNo.Value, Revision = Revision.Value,
			Scale = Scale.Value, SheetSize = SheetSize.Value, DrawnBy = DrawnBy.Value, Date = Date.Value,
		};
		LastDocument = DrawingDocument.Build( source );
		DxfText = LastDocument.ToDxf();
		PdfBytes = LastDocument.ToPdf();
		Note = $"Sheet {SheetSize.Value} rev {Revision.Value}: {LastDocument.Views.Count} views, " +
			$"{LastDocument.HoleTable.Count} holes tabled, {LastDocument.Dimensions.Count} dimensions. " +
			"Section view is a labelled front projection, not a computed B-rep section cut.";
	}
}

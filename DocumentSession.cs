#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Effigy;

namespace Marionette.Desktop;

public enum DocumentSourceKind
{
	Effigy,
	Fbx
}

/// <summary>
/// Persistent selection references in authoring space.
/// Never identifies objects by transient GPU draw call or vertex index.
/// </summary>
public sealed record SessionSelection
{
	public string? FeatureId { get; init; }
	public string? BoneName { get; init; }
	public string? BodyId { get; init; }
	public IReadOnlyList<string> SelectedBodyIds { get; init; } = Array.Empty<string>();

	public static SessionSelection Empty { get; } = new();
}

/// <summary>
/// Retained import data for FBX models.
/// An external FBX is preserved with its source data and options; it does not masquerade as a PartStudio.
/// </summary>
public sealed class FbxSourceData
{
	public FbxImport.Result Result { get; }
	public string? SourcePath { get; }
	public byte[]? RawBytes { get; }

	public int PieceCount => Result?.Pieces?.Count ?? 0;
	public int SkinnedPieceCount => Result?.SkinnedPieces ?? 0;
	public int InwardPieceCount => Result?.InwardPieces ?? 0;
	public Skeleton? Skeleton => Result?.Skeleton;

	public FbxSourceData( FbxImport.Result result, string? sourcePath = null, byte[]? rawBytes = null )
	{
		Result = result ?? throw new ArgumentNullException( nameof( result ) );
		SourcePath = sourcePath;
		RawBytes = rawBytes;
	}
}

/// <summary>
/// Derived render snapshot tagged with document ID and revision.
/// Immutable result of a build pass, ready for promotion on the GL thread.
/// </summary>
public sealed class RenderSnapshot : IDisposable
{
	public string DocumentId { get; }
	public long Revision { get; }
	public ViewportMesh Mesh { get; }
	public MarionettePlayer? Player { get; }
	public double LoadSeconds { get; }
	public double ExtractSeconds { get; }
	public double TotalSeconds { get; }
	public string? CacheNote { get; }

	public RenderSnapshot( string documentId, long revision, ViewportMesh mesh, MarionettePlayer? player = null,
		double loadSeconds = 0, double extractSeconds = 0, double totalSeconds = 0, string? cacheNote = null )
	{
		DocumentId = documentId ?? throw new ArgumentNullException( nameof( documentId ) );
		Revision = revision;
		Mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		Player = player;
		LoadSeconds = loadSeconds;
		ExtractSeconds = extractSeconds;
		TotalSeconds = totalSeconds;
		CacheNote = cacheNote;
	}

	public bool IsStaleFor( DocumentSession session ) =>
		session is null || session.DocumentId != DocumentId || session.Revision != Revision;

	public void Dispose()
	{
		Mesh?.Dispose();
	}
}

/// <summary>
/// Retained document session owning authoring state independently of viewport rendering.
/// Holds the mutable PartStudio (for Effigy) or imported source data (for FBX),
/// raw clip JSON alongside parsed RigAnimFile, playhead, selection, and revision tracking.
/// </summary>
public sealed class DocumentSession
{
	long _revision = 1;

	public string DocumentId { get; }
	public DocumentSourceKind SourceKind { get; }
	public string SourcePath { get; private set; }
	public AssetReference? AssetReference { get; private set; }

	public long Revision => Volatile.Read( ref _revision );
	public long SavedRevision { get; set; } = 1;
	public bool IsDirty => Revision != SavedRevision || History.IsDirty;

	public EditHistory History { get; } = new();

	public PartStudio? Studio { get; }
	public FbxSourceData? FbxSource { get; }

	public RigAnimFile? Clip { get; private set; }
	public string? ClipSourceJson { get; private set; }
	public string? ClipPath { get; private set; }

	public float PlayheadTime { get; set; }
	public bool IsPlaying { get; set; }
	public SessionSelection Selection { get; set; } = SessionSelection.Empty;

	public RenderSnapshot? CurrentSnapshot { get; private set; }

	/// <summary>
	/// Exact Survey ModelDoc reference for M4's read-only material remaps, or null for an
	/// ordinary document. Settable so a project manifest can name the ModelDoc explicitly;
	/// otherwise inferred from a recognized Survey source basename.
	/// </summary>
	public string? SurveyModelReference { get; set; }

	string? ResolveSurveyReference()
	{
		if ( !string.IsNullOrWhiteSpace( SurveyModelReference ) ) return SurveyModelReference;
		string name = Path.GetFileNameWithoutExtension( SourcePath ?? "" );
		if ( name.Equals( "player_suit", StringComparison.OrdinalIgnoreCase ) ) return SurveyMaterialMap.SuitModel;
		if ( name.Equals( "player_head", StringComparison.OrdinalIgnoreCase ) ) return SurveyMaterialMap.HeadModel;
		return null;
	}

	DocumentSession( string? documentId, DocumentSourceKind kind, string path, PartStudio? studio,
		FbxSourceData? fbxSource, string? clipPath, string? clipJson, RigAnimFile? clip, AssetReference? assetRef )
	{
		DocumentId = documentId ?? Guid.NewGuid().ToString( "N" );
		SourceKind = kind;
		SourcePath = path;
		Studio = studio;
		FbxSource = fbxSource;
		ClipPath = clipPath;
		ClipSourceJson = clipJson;
		Clip = clip;
		AssetReference = assetRef;
		History.StateChanged += () => BumpRevision();
	}

	public static DocumentSession CreateEffigy( string path, PartStudio studio, string? clipPath = null,
		string? clipJson = null, RigAnimFile? clip = null, AssetReference? assetRef = null, string? documentId = null )
	{
		ArgumentNullException.ThrowIfNull( studio );
		return new DocumentSession( documentId, DocumentSourceKind.Effigy, path, studio, null, clipPath, clipJson, clip, assetRef );
	}

	public static DocumentSession CreateFbx( string path, FbxImport.Result fbxResult, byte[]? rawBytes = null,
		string? clipPath = null, string? clipJson = null, RigAnimFile? clip = null, AssetReference? assetRef = null, string? documentId = null )
	{
		ArgumentNullException.ThrowIfNull( fbxResult );
		var fbxData = new FbxSourceData( fbxResult, path, rawBytes );
		return new DocumentSession( documentId, DocumentSourceKind.Fbx, path, null, fbxData, clipPath, clipJson, clip, assetRef );
	}

	public long BumpRevision() => Interlocked.Increment( ref _revision );

	public void MarkSaved()
	{
		SavedRevision = Revision;
		History.MarkSaved();
	}

	public void MarkSaved( long revision, Guid? stateId = null )
	{
		SavedRevision = revision;
		if ( stateId.HasValue )
		{
			History.MarkSaved( stateId.Value );
		}
		else if ( Revision == revision )
		{
			History.MarkSaved();
		}
	}

	public void Relocate( string newPath, AssetReference? newAssetRef = null )
	{
		if ( string.IsNullOrWhiteSpace( newPath ) ) throw new ArgumentException( "New path cannot be empty.", nameof( newPath ) );
		SourcePath = newPath;
		AssetReference = newAssetRef;
	}

	public void SetClip( string? clipPath, string? clipJson, RigAnimFile? clip )
	{
		ClipPath = clipPath;
		ClipSourceJson = clipJson;
		Clip = clip;
		BumpRevision();
	}

	public bool TryPromoteSnapshot( RenderSnapshot snapshot )
	{
		ArgumentNullException.ThrowIfNull( snapshot );
		if ( snapshot.IsStaleFor( this ) ) return false;
		CurrentSnapshot = snapshot;
		return true;
	}

	public RenderSnapshot BuildSnapshot( double loadSeconds = 0, string? cacheNote = null, bool animate = false, float? time = null )
	{
		long capturedRevision = Revision;
		string capturedDocId = DocumentId;
		var sw = Stopwatch.StartNew();

		ViewportMesh mesh;
		if ( SourceKind == DocumentSourceKind.Fbx )
		{
			if ( FbxSource is null ) throw new InvalidOperationException( "FBX session has no import data." );
			mesh = ViewportMesh.FromFbx( FbxSource.Result, loadSeconds, SourcePath );
		}
		else
		{
			if ( Studio is null ) throw new InvalidOperationException( "Effigy session has no PartStudio." );
			mesh = ViewportMesh.FromStudio( Studio, loadSeconds, SourcePath );
		}
		// Snapshot construction runs on the loader worker: resolve and decode before GL upload.
		// Explicit project overrides can call mesh.ApplyMaterials with their own resolver instead.
		// M4: a source whose ModelDoc reference is an exact Survey model gets its explicit
		// read-only .vmat remaps; anything else keeps the ordinary per-document resolver.
		string materialRoot = Path.GetDirectoryName( Path.GetFullPath( SourcePath ) )!;
		AssetPathResolver materialPaths = Directory.Exists( materialRoot )
			? new AssetPathResolver( materialRoot ) : new AssetPathResolver( Directory.GetCurrentDirectory() );
		// Survey remaps wrap a per-document resolver carrying the explicit .vmat overrides.
		// Its ResolveSlot additionally reports unsupported ps1_lit effects to any caller that
		// wants them; the viewport consumes the wrapped resolver for the actual maps.
		var surveyMap = SurveyMaterialMap.ForSourcePath( materialPaths, ResolveSurveyReference() ?? "" );
		mesh.ApplyMaterials( surveyMap?.Resolver ?? new MaterialResolver( materialPaths, DocumentId ) );

		MarionettePlayer? player = null;
		if ( mesh.Rig is { Count: > 0 } )
		{
			player = new MarionettePlayer( mesh.Rig, startPlaying: animate || IsPlaying );
			if ( Clip is not null )
			{
				player.LoadClip( Clip );
				player.Playing = animate || IsPlaying;
			}
			else if ( !string.IsNullOrWhiteSpace( ClipPath ) )
			{
				player.LoadClip( ClipPath );
				player.Playing = animate || IsPlaying;
			}

			float targetTime = time ?? PlayheadTime;
			if ( targetTime != 0 )
			{
				player.Time = targetTime;
				player.Step( 0 );
			}
		}
		else if ( Clip is not null || !string.IsNullOrWhiteSpace( ClipPath ) )
		{
			throw new InvalidOperationException( "Cannot play a skeletal clip on a model without bones." );
		}

		sw.Stop();
		double extractSeconds = mesh.ExtractSeconds > 0 ? mesh.ExtractSeconds : sw.Elapsed.TotalSeconds;

		return new RenderSnapshot(
			capturedDocId,
			capturedRevision,
			mesh,
			player,
			loadSeconds: loadSeconds,
			extractSeconds: extractSeconds,
			totalSeconds: loadSeconds + extractSeconds,
			cacheNote: cacheNote
		);
	}
}

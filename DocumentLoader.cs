using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Loads an .effigy off the UI thread, and never blocks the window while it does.
///
/// THE WORK IS <see cref="StudioCache.Load"/>, NOT ReadFile + Rebuild. That matters more than it
/// looks: the pod engine takes about 12.8 s to rebuild from its feature tree and about 0.09 s to
/// restore from the geometry cache, so those two lines are the difference between a window that
/// shows a progress pulse for thirteen seconds on every single launch and one that is up before
/// the user has finished moving the mouse to it. Doing the read and the rebuild by hand here was
/// the app paying that cost every time, in the one place that had already been fixed.
///
/// The first launch of a document is still a full rebuild — the cache has to be written by
/// somebody — and a document carrying a paint atlas or a shape key is deliberately never cached,
/// so it rebuilds every launch. That is why <see cref="CacheNote"/> exists and why the loading
/// screen reports it: "slow" and "the cache is broken" look identical from the outside otherwise.
/// </summary>
public sealed class DocumentLoader
{
	public enum State { Idle, Loading, Ready, Failed }

	int _status;
	public State Status => (State)Volatile.Read( ref _status );
	public Task Completion { get; private set; } = Task.CompletedTask;
	public string Path { get; private set; }
	public string Error { get; private set; }
	public ViewportMesh Mesh { get; private set; }
	public MarionettePlayer Player { get; private set; }
	public DocumentSession Session { get; private set; }
	public RenderSnapshot Snapshot { get; private set; }

	/// <summary>Everything from reading the file to having a built studio: cache restore or rebuild,
	/// whichever it took. Named for the whole job rather than for the rebuild, because that is the
	/// number a user waits through.</summary>
	public double LoadSeconds { get; private set; }

	public double ExtractSeconds { get; private set; }
	public double TotalSeconds { get; private set; }

	/// <summary>Straight from the cache: "cache hit — restored 419 body(ies)", or a miss with the
	/// reason. Reported verbatim so the loading screen cannot claim a fast load it did not get.</summary>
	public string CacheNote { get; private set; }

	volatile string _phase = "";
	public string Phase => _phase;

	public void Start( string path, string clipPath = null, bool animate = false, float? time = null )
	{
		if ( Status == State.Loading ) return;
		Path = path;
		Error = null;
		CacheNote = null;
		LoadSeconds = ExtractSeconds = TotalSeconds = 0;
		_phase = "loading";
		Volatile.Write( ref _status, (int)State.Loading );

		Completion = Task.Run( () =>
		{
			var total = Stopwatch.StartNew();
			try
			{
				var sw = Stopwatch.StartNew();
				DocumentSession session;
				string cacheNote;
				double loadSeconds;

				string clipJson = null;
				RigAnimFile clip = null;
				byte[] fbxClipBytes = null;
				if ( !string.IsNullOrWhiteSpace( clipPath ) )
				{
					if ( clipPath.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) )
					{
						// I4: an animated FBX take converts against the model's own skeleton,
						// so reading is deferred until the document import has produced it.
						fbxClipBytes = System.IO.File.ReadAllBytes( clipPath );
					}
					else
					{
						clipJson = System.IO.File.ReadAllText( clipPath );
						clip = RigAnimFile.ReadFile( clipPath );
					}
				}

				if ( path.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) )
				{
					_phase = "document";
					var rawBytes = System.IO.File.ReadAllBytes( path );
					var imported = SurveyPlayerAssembly.IsSurveyPlayer( path )
						? SurveyPlayerAssembly.Load( path )
						: Effigy.FbxImport.Read( rawBytes );
					loadSeconds = sw.Elapsed.TotalSeconds;
					cacheNote = $"fbx import: {imported.Pieces.Count} piece(s), {imported.SkinnedPieces} skinned, {imported.InwardPieces} inward";

					if ( fbxClipBytes != null )
						(clip, clipJson) = ConvertFbxClip( fbxClipBytes, clipPath, imported.Skeleton );

					session = DocumentSession.CreateFbx( path, imported, rawBytes: rawBytes, clipPath: clipPath, clipJson: clipJson, clip: clip );
				}
				else
				{
					if ( fbxClipBytes != null )
						throw new NotSupportedException(
							"An animated FBX clip needs an FBX document so its skeleton is available for target mapping." );

					_phase = "document";
					var studio = StudioCache.Load( path, out var note );
					loadSeconds = sw.Elapsed.TotalSeconds;
					cacheNote = note;

					session = DocumentSession.CreateEffigy( path, studio, clipPath: clipPath, clipJson: clipJson, clip: clip );
				}

				_phase = "extract";
				var snapshot = session.BuildSnapshot( loadSeconds, cacheNote, animate: animate, time: time );

				LoadSeconds = loadSeconds;
				ExtractSeconds = snapshot.ExtractSeconds;
				CacheNote = cacheNote;
				TotalSeconds = total.Elapsed.TotalSeconds;

				_phase = "ready";
				Session = session;
				Snapshot = snapshot;
				Mesh = snapshot.Mesh;
				Player = snapshot.Player;
				session.TryPromoteSnapshot( snapshot );

				// Release-publish all fields together; the render thread reads Status first.
				Volatile.Write( ref _status, (int)State.Ready );
			}
			catch ( Exception ex )
			{
				Error = ex.ToString();
				_phase = "failed";
				TotalSeconds = total.Elapsed.TotalSeconds;
				Volatile.Write( ref _status, (int)State.Failed );
			}
		} );
	}

	/// <summary>
	/// I4: convert an animated FBX take into a rig clip against the document's own skeleton.
	/// Returns the clip plus its JSON form so the session can save it back out unchanged.
	/// </summary>
	static (RigAnimFile clip, string json) ConvertFbxClip( byte[] fbxClipBytes, string clipPath, Skeleton skeleton )
	{
		var result = FbxClipConverter.Convert( fbxClipBytes, skeleton );
		string json = System.IO.Path.ChangeExtension( clipPath, ".clip.json" );
		FbxClipConverter.WriteNew( result, json );
		return ( result.Clip, json );
	}

	public bool TryPromoteSnapshot( RenderSnapshot snapshot )
	{
		if ( Session is null || snapshot.IsStaleFor( Session ) ) return false;
		Session.TryPromoteSnapshot( snapshot );
		Snapshot = snapshot;
		Mesh = snapshot.Mesh;
		Player = snapshot.Player;
		return true;
	}

	public void Reset()
	{
		if ( Status == State.Loading ) return;
		Session = null;
		Snapshot = null;
		Mesh = null;
		Player = null;
		Path = null;
		Error = null;
		CacheNote = null;
		LoadSeconds = ExtractSeconds = TotalSeconds = 0;
		_phase = "";
		Volatile.Write( ref _status, (int)State.Idle );
	}
}

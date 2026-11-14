#nullable enable
using System;
using System.IO;
using System.Text.Json;

namespace Marionette.Desktop;

/// <summary>
/// Godlike Wave 3 P2 — per-asset file locking, the other half of "two artists
/// can work on one asset without overwriting each other".
///
/// Text scenes merge; binary assets do not. Before editing a binary asset
/// (*.effigy, *.glb, *.fbx, a texture), an artist takes the lock: a small
/// JSON sidecar (&lt;asset&gt;.lock.json) naming the owner, the machine and a
/// heartbeat. The lock is held while the heartbeat is fresh; a stale lock
/// (heartbeat older than the stale window) can be taken over explicitly, and
/// the takeover names the previous owner so it is visible, never silent.
/// Releasing deletes the sidecar, and only the owner can release it.
///
/// This is a filesystem convention, not a server: it works in any shared
/// folder or git checkout, and a lock file left behind by a crashed editor
/// goes stale on its own. It does not stop a determined bypass — anyone can
/// delete the sidecar — it stops accidents, which is the actual failure mode.
/// </summary>
public sealed record AssetLockRecord
{
	public string Asset { get; set; } = "";
	public string Owner { get; set; } = "";
	public string Machine { get; set; } = "";
	public string AcquiredUtc { get; set; } = "";
	public string HeartbeatUtc { get; set; } = "";
}

/// <summary>Result of an acquire attempt.</summary>
public sealed record AssetLockResult( bool Acquired, string? Error, string? PreviousOwner );

public static class AssetLock
{
	/// <summary>How old a heartbeat may be before the lock counts as stale
	/// and may be taken over. Default: 10 minutes.</summary>
	public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes( 10 );

	static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
	};

	public static string LockFilePath( string assetPath ) => assetPath + ".lock.json";

	public static AssetLockRecord? Current( string assetPath )
	{
		var path = LockFilePath( assetPath );
		if ( !File.Exists( path ) ) return null;
		try
		{
			return JsonSerializer.Deserialize<AssetLockRecord>( File.ReadAllText( path ), JsonOptions );
		}
		catch { return null; }
	}

	/// <summary>Take the lock. Succeeds when there is no lock, when the
	/// caller already holds it (heartbeat refreshed), or when the existing
	/// lock is stale — in the stale case PreviousOwner names who lost it.
	/// Fails when another artist holds a live lock.</summary>
	public static AssetLockResult TryAcquire(
		string assetPath,
		string owner,
		TimeSpan? staleAfter = null,
		Func<DateTimeOffset>? clock = null )
	{
		if ( string.IsNullOrWhiteSpace( owner ) )
			return new AssetLockResult( false, "A lock needs an owner name.", null );
		var now = ( clock ?? ( () => DateTimeOffset.UtcNow ) )();
		var stale = staleAfter ?? DefaultStaleAfter;
		var existing = Current( assetPath );

		if ( existing != null )
		{
			if ( existing.Owner == owner )
			{
				Write( assetPath, existing with { HeartbeatUtc = now.ToString( "o" ) } );
				return new AssetLockResult( true, null, null );
			}
			if ( DateTimeOffset.TryParse( existing.HeartbeatUtc, out var heartbeat )
				&& now - heartbeat < stale )
			{
				return new AssetLockResult( false,
					$"Asset is locked by {existing.Owner} on {existing.Machine} since {existing.AcquiredUtc}.", null );
			}
			var previous = existing.Owner;
			Write( assetPath, NewRecord( assetPath, owner, now ) );
			return new AssetLockResult( true, null, previous );
		}

		Write( assetPath, NewRecord( assetPath, owner, now ) );
		return new AssetLockResult( true, null, null );
	}

	/// <summary>Refresh the heartbeat. False when there is no lock or
	/// someone else holds it.</summary>
	public static bool Heartbeat( string assetPath, string owner, Func<DateTimeOffset>? clock = null )
	{
		var now = ( clock ?? ( () => DateTimeOffset.UtcNow ) )();
		var existing = Current( assetPath );
		if ( existing is null || existing.Owner != owner ) return false;
		Write( assetPath, existing with { HeartbeatUtc = now.ToString( "o" ) } );
		return true;
	}

	/// <summary>Release the lock. Only the owner can release it; false
	/// when there is no lock or someone else holds it.</summary>
	public static bool Release( string assetPath, string owner )
	{
		var existing = Current( assetPath );
		if ( existing is null || existing.Owner != owner ) return false;
		try { File.Delete( LockFilePath( assetPath ) ); } catch { return false; }
		return true;
	}

	static AssetLockRecord NewRecord( string assetPath, string owner, DateTimeOffset now ) => new()
	{
		Asset = Path.GetFileName( assetPath ),
		Owner = owner,
		Machine = Environment.MachineName,
		AcquiredUtc = now.ToString( "o" ),
		HeartbeatUtc = now.ToString( "o" ),
	};

	static void Write( string assetPath, AssetLockRecord record ) =>
		File.WriteAllText( LockFilePath( assetPath ),
			JsonSerializer.Serialize( record, JsonOptions ) );
}

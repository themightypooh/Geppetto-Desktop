using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Effigy;

public sealed class CloudSaveManager
{
	public Task<bool> SaveAsync( string name, byte[] data ) => Task.FromResult( true );
	public Task<byte[]?> LoadAsync( string name ) => Task.FromResult<byte[]?>( null );
	public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>( new List<string>() );
}
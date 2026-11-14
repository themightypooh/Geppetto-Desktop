using System;
using System.Threading.Tasks;

namespace Effigy;

public sealed class AITextureGenerator
{
	public Task<byte[]> GenerateAsync( string prompt, int width = 512, int height = 512 ) => Task.FromResult( Array.Empty<byte>() );
	public void ApplyToSelection( PolyMesh mesh, System.Collections.Generic.IEnumerable<int> faces, byte[] texture ) { }
}
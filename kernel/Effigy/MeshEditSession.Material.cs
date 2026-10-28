using System;

namespace Effigy;

public sealed partial class MeshEditSession
{
	/// <summary>
	/// Put the selected faces on material <paramref name="slot"/> — s&amp;box's "material for these faces"
	/// in edit mode. One undo step; faces already on that slot are left alone, and none to change says so.
	/// </summary>
	public void SetFaceMaterial( int slot )
	{
		if ( slot < 0 ) throw new ArgumentException( "A material slot is 0 or more." );
		var faces = RequireFaces( "Material" );

		Step( "Material", () =>
		{
			int changed = 0;
			foreach ( var f in faces )
			{
				if ( f < 0 || f >= Mesh.FaceCount || Mesh.Faces[f].Material == slot ) continue;
				Mesh.Faces[f].Material = slot;
				changed++;
			}

			if ( changed == 0 ) { NoChange( "The selected faces already use that material." ); return; }
			Report( $"{changed} face{( changed == 1 ? "" : "s" )} now use slot {slot}" );
		} );
	}
}

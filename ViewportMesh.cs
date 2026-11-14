using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Effigy;
using Silk.NET.OpenGL;
using DrawBatch = Marionette.Desktop.CpuMesh.DrawBatch;
using BodyPickEntry = Marionette.Desktop.CpuMesh.BodyPickEntry;
using PieceCache = Marionette.Desktop.CpuMesh.PieceCache;

namespace Marionette.Desktop;

/// <summary>
/// GPU-side geometry and OpenGL resources (VAO/VBO/EBO/textures) backed by a <see cref="CpuMesh"/>.
/// Uploaded once on the GL thread; drawn batched per material.
/// </summary>
public sealed class ViewportMesh : IDisposable
{
	private static readonly ConditionalWeakTable<CpuMesh, ViewportMesh> _wrappers = new();

	public CpuMesh Cpu { get; }

	public static ViewportMesh For( CpuMesh cpu )
	{
		if ( cpu is null ) return null;
		return _wrappers.GetValue( cpu, c => new ViewportMesh( c ) );
	}

	public static implicit operator ViewportMesh( CpuMesh cpu ) => For( cpu );
	public static implicit operator CpuMesh( ViewportMesh vm ) => vm?.Cpu;

	public const int FloatsPerVertex = CpuMesh.FloatsPerVertex;

	public ViewportMesh()
	{
		Cpu = new CpuMesh();
		_wrappers.Add( Cpu, this );
	}

	public ViewportMesh( CpuMesh cpu )
	{
		Cpu = cpu ?? throw new ArgumentNullException( nameof( cpu ) );
	}

	public float[] Vertices { get => Cpu.Vertices; set => Cpu.Vertices = value; }
	public uint[] Indices { get => Cpu.Indices; set => Cpu.Indices = value; }
	public DrawBatch[] Batches { get => Cpu.Batches; set => Cpu.Batches = value; }
	public BodyPickEntry[] BodyPicks { get => Cpu.BodyPicks; set => Cpu.BodyPicks = value; }
	public MeshPicker Picker => Cpu.Picker;
	public Skeleton Rig { get => Cpu.Rig; set => Cpu.Rig = value; }
	public bool HasSkin { get => Cpu.HasSkin; set => Cpu.HasSkin = value; }
	public Vector3 BoundsMin { get => Cpu.BoundsMin; set => Cpu.BoundsMin = value; }
	public Vector3 BoundsMax { get => Cpu.BoundsMax; set => Cpu.BoundsMax = value; }
	public Vector3 Centre { get => Cpu.Centre; set => Cpu.Centre = value; }
	public float Radius { get => Cpu.Radius; set => Cpu.Radius = value; }
	public int TriangleCount { get => Cpu.TriangleCount; set => Cpu.TriangleCount = value; }
	public int BodyCount { get => Cpu.BodyCount; set => Cpu.BodyCount = value; }
	public double LoadSeconds { get => Cpu.LoadSeconds; set => Cpu.LoadSeconds = value; }
	public double ExtractSeconds { get => Cpu.ExtractSeconds; set => Cpu.ExtractSeconds = value; }
	public float[] BaseVertices { get => Cpu.BaseVertices; set => Cpu.BaseVertices = value; }
	public CornerVertexMap MorphCornerMap { get => Cpu.MorphCornerMap; set => Cpu.MorphCornerMap = value; }
	public RetainedMeshMorphs Morphs { get => Cpu.Morphs; set => Cpu.Morphs = value; }
	public Dictionary<string, float> MorphWeights { get => Cpu.MorphWeights; set => Cpu.MorphWeights = value; }
	public PolyMesh SourceMesh => Cpu.SourceMesh;
	public MeshResourceBudget Budget => Cpu.Budget;

	public static PieceCache LivePieces
	{
		get => CpuMesh.LivePieces;
		set => CpuMesh.LivePieces = value;
	}

	public ViewportMesh Remesh( PolyMesh modified ) => For( Cpu.Remesh( modified ) );

	public static ViewportMesh FromStudio( PartStudio studio, double loadSeconds, string documentPath )
		=> For( CpuMesh.FromStudio( studio, loadSeconds, documentPath ) );

	public static ViewportMesh FromPolyMesh( PolyMesh mesh, Func<int, string> nameForSlot )
		=> For( CpuMesh.FromPolyMesh( mesh, nameForSlot ) );

	public static ViewportMesh FromFbx( Effigy.FbxImport.Result imported, double loadSeconds, string documentPath )
		=> For( CpuMesh.FromFbx( imported, loadSeconds, documentPath ) );

	uint _vao, _vbo, _ebo;
	nuint _vboBytes, _eboBytes;           // allocated sizes, so a reused buffer grows only when needed
	readonly Dictionary<string, uint> _inheritedTextures = new( StringComparer.Ordinal );
	bool _uploaded;
	long _gpuTextureBytes;
	uint _whiteTexture, _flatNormalTexture, _unitDataTexture;
	uint[] _batchAlbedoTextures, _batchNormalTextures, _batchRoughnessTextures, _batchMetallicTextures, _batchEmissionTextures, _batchOcclusionTextures;
	readonly Dictionary<string, uint> _ownedTextures = new( StringComparer.Ordinal );

	/// <summary>Resolve/decode authored maps before upload, on the document worker thread.</summary>
	public void ApplyMaterials( MaterialResolver resolver )
	{
		if ( _uploaded ) throw new InvalidOperationException( "Material resolution must precede GL upload." );
		_batchUv = null;
		Cpu.ApplyMaterials( resolver );
	}

	public void Upload( GL gl ) => Upload( gl, null );

	/// <summary>Upload, taking over <paramref name="previous"/>'s vertex array, buffers and matching textures
	/// when it has them (a live pane's last revision): buffers are written in place and grow only when this
	/// mesh is bigger, and when both meshes have the same per-body layout only the bodies that changed are
	/// written. <paramref name="previous"/> keeps its CPU data but no GPU objects; ReleaseGpu it as usual.</summary>
	public unsafe void Upload( GL gl, ViewportMesh previous )
	{
		if ( _uploaded ) return;
		var uploadClock = Stopwatch.StartNew();

		bool reuse = previous is not null && !ReferenceEquals( previous, this ) && previous._uploaded && previous._vao != 0;
		if ( reuse )
		{
			_vao = previous._vao; _vbo = previous._vbo; _ebo = previous._ebo;
			_vboBytes = previous._vboBytes; _eboBytes = previous._eboBytes;
			previous._vao = previous._vbo = previous._ebo = 0; previous._vboBytes = previous._eboBytes = 0;
			foreach ( var kv in previous._ownedTextures ) _inheritedTextures[kv.Key] = kv.Value;
			previous._ownedTextures.Clear();
		}
		else
		{
			_vao = gl.GenVertexArray();
			_vbo = gl.GenBuffer();
			_ebo = gl.GenBuffer();
		}

		gl.BindVertexArray( _vao );

		gl.BindBuffer( BufferTargetARB.ArrayBuffer, _vbo );
		nuint vertexBytes = (nuint)((Vertices?.Length ?? 0) * sizeof( float ));
		fixed ( float* raw = Vertices )
		{
			bool sameLayout = reuse && vertexBytes <= _vboBytes && previous.Cpu.LayoutKeys is { } pk && Cpu.LayoutKeys is { } nk
				&& pk.Length == nk.Length && System.Linq.Enumerable.SequenceEqual( previous.Cpu.LayoutOffsets, Cpu.LayoutOffsets )
				&& System.Linq.Enumerable.SequenceEqual( previous.Cpu.LayoutLengths, Cpu.LayoutLengths );
			if ( vertexBytes > _vboBytes )
			{
				// grow with headroom so a model that keeps growing does not reallocate every revision
				_vboBytes = reuse ? vertexBytes + vertexBytes / 4 : vertexBytes;
				gl.BufferData( BufferTargetARB.ArrayBuffer, _vboBytes, null, BufferUsageARB.DynamicDraw );
				gl.BufferSubData( BufferTargetARB.ArrayBuffer, 0, vertexBytes, raw );
			}
			else if ( sameLayout )
			{
				for ( int i = 0; i < Cpu.LayoutKeys.Length; i++ )
					if ( Cpu.LayoutKeys[i] != previous.Cpu.LayoutKeys[i] )
						gl.BufferSubData( BufferTargetARB.ArrayBuffer, (nint)(Cpu.LayoutOffsets[i] * sizeof( float )),
							(nuint)(Cpu.LayoutLengths[i] * sizeof( float )), raw + Cpu.LayoutOffsets[i] );
			}
			else gl.BufferSubData( BufferTargetARB.ArrayBuffer, 0, vertexBytes, raw );
		}

		gl.BindBuffer( BufferTargetARB.ElementArrayBuffer, _ebo );
		nuint indexBytes = (nuint)((Indices?.Length ?? 0) * sizeof( uint ));
		fixed ( uint* raw = Indices )
		{
			if ( indexBytes > _eboBytes )
			{
				_eboBytes = reuse ? indexBytes + indexBytes / 4 : indexBytes;
				gl.BufferData( BufferTargetARB.ElementArrayBuffer, _eboBytes, null, reuse ? BufferUsageARB.DynamicDraw : BufferUsageARB.StaticDraw );
			}
			gl.BufferSubData( BufferTargetARB.ElementArrayBuffer, 0, indexBytes, raw );
		}

		const uint stride = FloatsPerVertex * sizeof( float );
		gl.EnableVertexAttribArray( 0 );
		gl.VertexAttribPointer( 0, 3, VertexAttribPointerType.Float, false, stride, (void*)0 );
		gl.EnableVertexAttribArray( 1 );
		gl.VertexAttribPointer( 1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof( float )) );
		gl.EnableVertexAttribArray( 2 );
		gl.VertexAttribPointer( 2, 4, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof( float )) );
		gl.EnableVertexAttribArray( 3 );
		gl.VertexAttribPointer( 3, 4, VertexAttribPointerType.Float, false, stride, (void*)(10 * sizeof( float )) );
		gl.EnableVertexAttribArray( 4 );
		gl.VertexAttribPointer( 4, 2, VertexAttribPointerType.Float, false, stride, (void*)(14 * sizeof( float )) );
		gl.EnableVertexAttribArray( 5 );
		gl.VertexAttribPointer( 5, 4, VertexAttribPointerType.Float, false, stride, (void*)(16 * sizeof( float )) );
		// Wave-5 look fix: location 7 carries the per-vertex colour. (Location 6 is LiveGeometry's prev-position buffer in its own VAO; 7 keeps the two from ever colliding.)
		gl.EnableVertexAttribArray( 7 );
		gl.VertexAttribPointer( 7, 4, VertexAttribPointerType.Float, false, stride, (void*)(20 * sizeof( float )) );
		_whiteTexture = CreateTexture( gl, DecodedImage.NeutralWhite, null, true );
		_flatNormalTexture = CreateTexture( gl, DecodedImage.NeutralFlatNormal, null, false );
		_unitDataTexture = CreateTexture( gl, DecodedImage.NeutralWhite, null, false );
		int batchCount = Batches?.Length ?? 0;
		_batchAlbedoTextures = new uint[batchCount];
		_batchNormalTextures = new uint[batchCount];
		_batchRoughnessTextures = new uint[batchCount];
		_batchMetallicTextures = new uint[batchCount];
		_batchEmissionTextures = new uint[batchCount];
		_batchOcclusionTextures = new uint[batchCount];
		for ( int i = 0; i < batchCount; i++ )
		{
			var batch = Batches[i];
			_batchAlbedoTextures[i] = Texture( gl, batch.AlbedoImage, batch.AlbedoBinding, _whiteTexture, true );
			_batchNormalTextures[i] = Texture( gl, batch.NormalImage, batch.NormalBinding, _flatNormalTexture, false );
			_batchRoughnessTextures[i] = Texture( gl, batch.RoughnessImage, batch.RoughnessBinding, _unitDataTexture, false );
			_batchMetallicTextures[i] = Texture( gl, batch.MetallicImage, batch.MetallicBinding, _unitDataTexture, false );
			// Unauthored emission map multiplies by one, preserving the existing emission factor.
			_batchEmissionTextures[i] = Texture( gl, batch.EmissionImage, batch.EmissionBinding, _whiteTexture, true );
			_batchOcclusionTextures[i] = Texture( gl, batch.OcclusionImage, batch.OcclusionBinding, _unitDataTexture, false );
		}

		gl.BindVertexArray( 0 );
		_uploaded = true;
		foreach ( var leftover in _inheritedTextures.Values ) gl.DeleteTexture( leftover );   // maps the new revision dropped
		_inheritedTextures.Clear();

		// M4 budget: driver-side storage for the owned maps plus the three shared fallbacks
		// (white, flat normal, unit data — one texel each).
		Budget.RecordUpload( uploadClock.Elapsed.TotalSeconds,
			_gpuTextureBytes + 3 * 4, _ownedTextures.Count + 3 );

		// CPU copies are no longer needed after upload.
		Vertices = null;
		Indices = null;
	}

	uint Texture( GL gl, DecodedImage image, TextureBinding binding, uint fallback, bool srgb )
	{
		if ( image is null ) return fallback;
		if ( binding.UvSetIndex != 0 ) throw new NotSupportedException( "Only UV set 0 is available in the viewport." );
		// Format is part of the identity: the same bytes may be sampled as color and data.
		string key = $"{image.Width}x{image.Height}:{binding.Wrap}:{binding.Filter}:{srgb}:" +
			Convert.ToHexString( SHA256.HashData( image.Rgba ) );
		if ( !_ownedTextures.TryGetValue( key, out uint texture ) )
		{
			_ownedTextures[key] = texture = _inheritedTextures.Remove( key, out uint kept ) ? kept : CreateTexture( gl, image, binding, srgb );
			_gpuTextureBytes += (long)image.Width * image.Height * 4; // RGBA8 or sRGB8: 4 bytes/texel
		}
		return texture;
	}

	static unsafe uint CreateTexture( GL gl, DecodedImage image, TextureBinding binding, bool srgb )
	{
		uint handle = gl.GenTexture();
		gl.BindTexture( TextureTarget.Texture2D, handle );
		bool nearest = binding?.Filter == TextureFilter.Nearest;
		bool hasMips = !nearest && image.Width > 1 && image.Height > 1;
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
			(int)(nearest ? GLEnum.Nearest : (hasMips ? GLEnum.LinearMipmapLinear : GLEnum.Linear)) );
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
			(int)(nearest ? GLEnum.Nearest : GLEnum.Linear) );
		GLEnum wrap = GLEnum.Repeat;
		if ( binding?.Wrap == TextureWrap.Clamp ) wrap = GLEnum.ClampToEdge;
		else if ( binding?.Wrap == TextureWrap.Mirror ) wrap = GLEnum.MirroredRepeat;
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap );
		gl.TexParameter( TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap );
		gl.PixelStore( PixelStoreParameter.UnpackAlignment, 1 );
		// Reader row 0 is top, GL upload row 0 is bottom. Flip once here; UV V remains upward.
		int stride = image.Width * 4;
		byte[] bottomFirst = new byte[image.Rgba.Length];
		for ( int row = 0; row < image.Height; row++ )
			Array.Copy( image.Rgba, row * stride, bottomFirst, (image.Height - 1 - row) * stride, stride );
		fixed ( byte* pixels = bottomFirst )
			gl.TexImage2D( TextureTarget.Texture2D, 0, srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8,
				(uint)image.Width, (uint)image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels );

		if ( hasMips )
		{
			gl.GenerateMipmap( TextureTarget.Texture2D );
			try
			{
				gl.GetFloat( (GLEnum)0x84FF /* GL_MAX_TEXTURE_MAX_ANISOTROPY_EXT */, out float maxAniso );
				if ( maxAniso > 1f )
					gl.TexParameter( TextureTarget.Texture2D, (TextureParameterName)0x84FE /* GL_TEXTURE_MAX_ANISOTROPY_EXT */, Math.Min( 8f, maxAniso ) );
			}
			catch { }
		}
		gl.BindTexture( TextureTarget.Texture2D, 0 );
		return handle;
	}

	/// <summary>
	/// Applies <paramref name="weights"/> to the uploaded vertex buffer, re-uploading the affected
	/// positions and normals. Pass null or empty to restore the base shape. Safe to call every frame.
	/// </summary>
	readonly Dictionary<string, float> _appliedWeights = new( StringComparer.Ordinal );
	bool _morphApplied, _morphUploaded;
	float[] _morphVertices;
	/// <summary>Bumped whenever <see cref="ApplyMorphWeights"/> rewrites <see cref="Vertices"/>.</summary>
	public int MorphRevision { get; private set; }

	bool SameWeights( IReadOnlyDictionary<string, float> weights )
	{
		int count = weights?.Count ?? 0;
		if ( count != _appliedWeights.Count ) return false;
		if ( count == 0 ) return true;
		// Concrete-type enumeration avoids boxing the enumerator every frame.
		if ( weights is Dictionary<string, float> d )
		{
			foreach ( var kv in d )
				if ( !_appliedWeights.TryGetValue( kv.Key, out var v ) || v != kv.Value ) return false;
			return true;
		}
		foreach ( var kv in weights )
			if ( !_appliedWeights.TryGetValue( kv.Key, out var v ) || v != kv.Value ) return false;
		return true;
	}

	public unsafe void ApplyMorphWeights( GL gl, IReadOnlyDictionary<string, float> weights )
	{
		if ( MorphCornerMap == null || Morphs == null ) return;

		if ( Vertices == null && BaseVertices != null )
			Vertices = (float[])BaseVertices.Clone();
		if ( Vertices == null || BaseVertices == null ) return;
		// R10: re-deform and re-upload only when the weights actually changed.
		if ( _morphApplied && _morphUploaded == _uploaded && ReferenceEquals( _morphVertices, Vertices ) && SameWeights( weights ) ) return;
		_appliedWeights.Clear();
		if ( weights != null ) foreach ( var kv in weights ) _appliedWeights[kv.Key] = kv.Value;
		_morphApplied = true; _morphUploaded = _uploaded; _morphVertices = Vertices;
		MorphRevision++;

		BaseVertices.CopyTo( Vertices, 0 );

		if ( weights != null && weights.Count > 0 )
		{
			MorphEvaluator.DeformInterleaved( BaseVertices, MorphCornerMap, weights, Morphs, Vertices, FloatsPerVertex, 0, 3 );
		}

		if ( !_uploaded ) return;
		gl.BindBuffer( BufferTargetARB.ArrayBuffer, _vbo );
		fixed ( float* raw = Vertices )
		{
			gl.BufferSubData( BufferTargetARB.ArrayBuffer, 0, (nuint)(Vertices.Length * sizeof( float )), raw );
		}
	}

	public unsafe void Draw( GL gl )
	{
		if ( !_uploaded ) return;
		gl.BindVertexArray( _vao );
		if ( Batches is not null )
		{
			foreach ( var batch in Batches )
			{
				gl.DrawElements( PrimitiveType.Triangles, (uint)batch.IndexCount,
					DrawElementsType.UnsignedInt, (void*)batch.IndexOffset );
			}
		}
		gl.BindVertexArray( 0 );
	}

	/// <summary>Draw with per-batch PBR material uniforms (albedo, roughness, metallic, emission).</summary>
	public unsafe void DrawBatched( GL gl, GlShader shader, int albedoLoc, int roughLoc = -1, int metalLoc = -1, int emitLoc = -1, int normalYLoc = -1, int uvScaleLoc = -1, int uvOffsetLoc = -1, int uvRotationLoc = -1, int alphaModeLoc = -1, int alphaCutoffLoc = -1, int baseAlphaLoc = -1, int normalStrengthLoc = -1, int ps1OcclusionLoc = -1, int ps1WearLoc = -1, int ps1WearColorLoc = -1, int ps1GrimeLoc = -1, int ps1GrimeFilmLoc = -1, int ps1GlowLoc = -1, int doubleSidedLoc = -1, LobeUniforms lobes = null, Action beforeTranslucent = null )
	{
		if ( !_uploaded || Batches is null ) return;
		gl.BindVertexArray( _vao );
		// Opaque, mask and hashed batches first; translucent ones (blend, transmission) after,
		// so refraction can sample a copy of the finished opaque scene and blending composites
		// over it. beforeTranslucent runs once between the two groups when any translucent exists.
		bool anyTranslucent = false;
		foreach ( var b in Batches ) anyTranslucent |= b.Translucent;
		// ps1_lit boot mud lives only in the main lit shader, which is the one that takes ps1 glow.
		int bootDirtLoc = ps1GlowLoc >= 0 ? shader.Uniform( "uPs1BootDirt" ) : -1;
		int bootToeLoc = bootDirtLoc >= 0 ? shader.Uniform( "uPs1BootToe" ) : -1;
		int bootCuffLoc = bootDirtLoc >= 0 ? shader.Uniform( "uPs1BootCuff" ) : -1;
		Span<uint> bound = stackalloc uint[7];
		for ( int group = 0; group < 2; group++ )
		{
		bound.Clear();
		if ( group == 1 )
		{
			if ( !anyTranslucent ) break;
			beforeTranslucent?.Invoke();
			gl.BindVertexArray( _vao );
		}
		for ( int i = 0; i < Batches.Length; i++ )
		{
			var batch = Batches[i];
            bool isFur = Cpu.FurSlots.TryGetValue(batch.MaterialSlot, out var fur);
            shader.SetInt(shader.Uniform("uFur"), isFur ? 1 : 0);
            if (isFur)
            {
                var color = fur.Color;
                shader.SetVec3(shader.Uniform("uFurColor"), MathF.Pow(color.x, 2.2f), MathF.Pow(color.y, 2.2f), MathF.Pow(color.z, 2.2f));
                shader.SetVec3(shader.Uniform("uFurRim"), MathF.Pow(fur.Rim.x,2.2f), MathF.Pow(fur.Rim.y,2.2f), MathF.Pow(fur.Rim.z,2.2f));
                shader.SetFloat(shader.Uniform("uFurDensity"), fur.Density);
                shader.SetFloat(shader.Uniform("uFurDarkRoots"), fur.DarkRoots);
                shader.SetFloat(shader.Uniform("uFurClump"), fur.Clump);
            }

			if ( batch.Translucent != ( group == 1 ) ) continue;
			// R10: skip binds the previous batch already made (fallback maps are shared).
			Bind( gl, bound, 1, _batchAlbedoTextures[i] );
			Bind( gl, bound, 2, _batchNormalTextures[i] );
			Bind( gl, bound, 3, _batchRoughnessTextures[i] );
			Bind( gl, bound, 4, _batchMetallicTextures[i] );
			Bind( gl, bound, 5, _batchEmissionTextures[i] );
			Bind( gl, bound, 6, _batchOcclusionTextures[i] );
			shader.SetFloat( normalYLoc, batch.NormalBinding?.NormalConvention == NormalMapConvention.DirectX ? -1f : 1f );
			shader.SetVec3( albedoLoc, batch.R, batch.G, batch.B );
			shader.SetFloat( roughLoc, batch.Roughness );
			shader.SetFloat( metalLoc, batch.Metallic );
			shader.SetVec3( emitLoc, batch.Er, batch.Eg, batch.Eb );
			if ( uvScaleLoc >= 0 )
			{
				// R10: per-batch UV transforms are built once, not per draw.
				_batchUv ??= BuildBatchUv();
				var uv = _batchUv[i];
				shader.SetVec2Array( uvScaleLoc, uv.Scales );
				shader.SetVec2Array( uvOffsetLoc, uv.Offsets );
				if ( uvRotationLoc >= 0 ) shader.SetFloatArray( uvRotationLoc, uv.Rotations );
			}
			if ( alphaModeLoc >= 0 ) shader.SetInt( alphaModeLoc, (int)batch.AlphaMode );
			if ( alphaCutoffLoc >= 0 ) shader.SetFloat( alphaCutoffLoc, batch.AlphaCutoff );
			if ( baseAlphaLoc >= 0 ) shader.SetFloat( baseAlphaLoc, batch.A );
			if ( normalStrengthLoc >= 0 ) shader.SetFloat( normalStrengthLoc, batch.NormalStrength );
			if ( ps1OcclusionLoc >= 0 ) shader.SetFloat( ps1OcclusionLoc, batch.Ps1Occlusion );
			if ( ps1WearLoc >= 0 ) shader.SetFloat( ps1WearLoc, batch.Ps1Wear );
			if ( ps1WearColorLoc >= 0 ) shader.SetVec3( ps1WearColorLoc, batch.Wr, batch.Wg, batch.Wb );
			if ( ps1GrimeLoc >= 0 ) shader.SetFloat( ps1GrimeLoc, batch.Ps1Grime );
			if ( ps1GrimeFilmLoc >= 0 ) shader.SetFloat( ps1GrimeFilmLoc, batch.Ps1GrimeFilm );
			if ( ps1GlowLoc >= 0 ) shader.SetFloat( ps1GlowLoc, batch.Ps1Glow );
			if ( bootDirtLoc >= 0 )
			{
				shader.SetFloat( bootDirtLoc, batch.Ps1BootDirt );
				shader.SetFloat( bootToeLoc, batch.Ps1BootToe );
				shader.SetFloat( bootCuffLoc, batch.Ps1BootCuff );
			}
			if ( doubleSidedLoc >= 0 ) shader.SetInt( doubleSidedLoc, batch.DoubleSided ? 1 : 0 );
			// Wave-2 look wiring: one SetLook per batch, before its draw, so toon/stylized
			// actually reach the fragment shader. Defaults are a no-op there.
			shader.SetLook( batch.LookToon, batch.LookToonBands, batch.LookToonRim, batch.LookStylized,
				batch.LookOutlineWidth, batch.LookOutlineR, batch.LookOutlineG, batch.LookOutlineB,
				batch.LookClearcoat, batch.LookClearcoatRough, batch.LookSubsurface,
				batch.LookSubsurfaceR, batch.LookSubsurfaceG, batch.LookSubsurfaceB,
				batch.LookOpacity, batch.LookShadowR, batch.LookShadowG, batch.LookShadowB, batch.LookIridescence );
			lobes?.Apply( shader, batch.Lobes );
			bool blend = batch.AlphaMode == AlphaMode.Blend;
			// Blended surfaces test against depth but do not occlude each other.
			if ( blend ) { gl.Enable( EnableCap.Blend ); gl.BlendFunc( BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha ); gl.DepthMask( false ); }
			gl.DrawElements( PrimitiveType.Triangles, (uint)batch.IndexCount,
				DrawElementsType.UnsignedInt, (void*)batch.IndexOffset );
			if ( blend ) { gl.Disable( EnableCap.Blend ); gl.DepthMask( true ); }
		}
		}
		shader.SetInt(shader.Uniform("uFur"), 0); // Live overlays share this shader.
		gl.BindVertexArray( 0 );
		for ( int unit = 1; unit <= 6; unit++ )
		{
			gl.ActiveTexture( (TextureUnit)((int)TextureUnit.Texture0 + unit) );
			gl.BindTexture( TextureTarget.Texture2D, 0 );
		}
	}

	static void Bind( GL gl, Span<uint> bound, int unit, uint texture )
	{
		if ( bound[unit] == texture && texture != 0 ) return;
		bound[unit] = texture;
		gl.ActiveTexture( TextureUnit.Texture0 + unit );
		gl.BindTexture( TextureTarget.Texture2D, texture );
	}

	sealed record BatchUv( System.Numerics.Vector2[] Scales, System.Numerics.Vector2[] Offsets, float[] Rotations );
	BatchUv[] _batchUv;

	BatchUv[] BuildBatchUv()
	{
		var result = new BatchUv[Batches.Length];
		for ( int i = 0; i < Batches.Length; i++ )
		{
			var batch = Batches[i];
			var scales = new System.Numerics.Vector2[6];
			var offsets = new System.Numerics.Vector2[6];
			var rotations = new float[6];
			TextureBinding[] bindings = { batch.AlbedoBinding, batch.NormalBinding, batch.RoughnessBinding,
				batch.MetallicBinding, batch.EmissionBinding, batch.OcclusionBinding };
			for ( int c = 0; c < 6; c++ )
			{
				var b = bindings[c];
				scales[c] = b is null ? new System.Numerics.Vector2( 1f, 1f ) : new System.Numerics.Vector2( b.UvScale.x, b.UvScale.y );
				offsets[c] = b is null ? System.Numerics.Vector2.Zero : new System.Numerics.Vector2( b.UvOffset.x, b.UvOffset.y );
				rotations[c] = b is null ? 0f : b.UvRotation;
			}
			result[i] = new BatchUv( scales, offsets, rotations );
		}
		return result;
	}

	public void Dispose()
	{
		// GL delete happens via Release on the GL thread; this is a soft clear.
		Vertices = null;
		Indices = null;
		Batches = null;
	}

	public void ReleaseGpu( GL gl )
	{
		if ( !_uploaded ) return;
		gl.DeleteVertexArray( _vao );
		gl.DeleteBuffer( _vbo );
		gl.DeleteBuffer( _ebo );
		gl.DeleteTexture( _whiteTexture );
		gl.DeleteTexture( _flatNormalTexture );
		gl.DeleteTexture( _unitDataTexture );
		foreach ( var texture in _ownedTextures.Values ) gl.DeleteTexture( texture );
		_ownedTextures.Clear();
		Budget.RecordRelease();
		_gpuTextureBytes = 0;
		_batchAlbedoTextures = null;
		_batchNormalTextures = _batchRoughnessTextures = _batchMetallicTextures = _batchEmissionTextures = null;
		_batchOcclusionTextures = null;
		_batchUv = null;
		_morphApplied = false;
		_vao = _vbo = _ebo = 0; _vboBytes = _eboBytes = 0;
		_uploaded = false;
	}
}

public static class CpuMeshGlExtensions
{
	public static void Upload( this CpuMesh mesh, GL gl, ViewportMesh previous = null )
	{
		if ( mesh == null ) return;
		ViewportMesh.For( mesh ).Upload( gl, previous );
	}

	public static void Upload( this CpuMesh mesh, GL gl, CpuMesh previous )
	{
		if ( mesh == null ) return;
		ViewportMesh.For( mesh ).Upload( gl, previous is null ? null : ViewportMesh.For( previous ) );
	}

	public static void ReleaseGpu( this CpuMesh mesh, GL gl )
	{
		if ( mesh == null ) return;
		ViewportMesh.For( mesh ).ReleaseGpu( gl );
	}

	public static void DrawBatched( this CpuMesh mesh, GL gl, GlShader shader, int albedoLoc, int roughLoc = -1, int metalLoc = -1, int emitLoc = -1, int normalYLoc = -1, int uvScaleLoc = -1, int uvOffsetLoc = -1, int uvRotationLoc = -1, int alphaModeLoc = -1, int alphaCutoffLoc = -1, int baseAlphaLoc = -1, int normalStrengthLoc = -1, int ps1OcclusionLoc = -1, int ps1WearLoc = -1, int ps1WearColorLoc = -1, int ps1GrimeLoc = -1, int ps1GrimeFilmLoc = -1, int ps1GlowLoc = -1, int doubleSidedLoc = -1, LobeUniforms lobes = null, Action beforeTranslucent = null )
	{
		if ( mesh == null ) return;
		ViewportMesh.For( mesh ).DrawBatched( gl, shader, albedoLoc, roughLoc, metalLoc, emitLoc, normalYLoc, uvScaleLoc, uvOffsetLoc, uvRotationLoc, alphaModeLoc, alphaCutoffLoc, baseAlphaLoc, normalStrengthLoc, ps1OcclusionLoc, ps1WearLoc, ps1WearColorLoc, ps1GrimeLoc, ps1GrimeFilmLoc, ps1GlowLoc, doubleSidedLoc, lobes, beforeTranslucent );
	}
}

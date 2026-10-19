using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Marionette.Desktop;

internal static class DesktopProjectTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "project-manifest", name, test );

		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		void Throws<T>( Action body, string? messageSubstring = null ) where T : Exception
		{
			try
			{
				body();
			}
			catch ( T ex )
			{
				if ( messageSubstring != null && !ex.Message.Contains( messageSubstring, StringComparison.OrdinalIgnoreCase ) )
					throw new Exception( $"Expected exception message to contain '{messageSubstring}', got: '{ex.Message}'" );
				return;
			}
			catch ( Exception ex )
			{
				throw new Exception( $"Expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}" );
			}
			throw new Exception( $"Expected {typeof(T).Name}, but no exception was thrown." );
		}

		(string Root, AssetPathResolver Resolver) CreateProjectFixture( string name )
		{
			string root = Path.Combine( settings.WorkDirectory, "desktop-project-tests", Guid.NewGuid().ToString( "N" ), name );
			Directory.CreateDirectory( root );
			Directory.CreateDirectory( Path.Combine( root, "models" ) );
			Directory.CreateDirectory( Path.Combine( root, "materials" ) );
			Directory.CreateDirectory( Path.Combine( root, "anims" ) );
			Directory.CreateDirectory( Path.Combine( root, "textures" ) );
			return (root, new AssetPathResolver( root ));
		}

		// 1. Project manifest serialization and deserialization round-trip
		Check( "project manifest serialization and deserialization round-trip", () =>
		{
			var project = new DesktopProject
			{
				Id = "geppetto_hero_project",
				Name = "Geppetto Hero Character",
				SourceDocuments = new List<SourceDocument>
				{
					new( "doc_body", new AssetReference( "asset_body", "models/body.fbx" ), DocumentSourceKind.Fbx,
						new SourceDocumentOptions { Scale = 1.0f, AuthoringTool = "Blender" } ),
					new( "doc_rig", new AssetReference( "asset_rig", "models/rig.effigy" ), DocumentSourceKind.Effigy )
				},
				Clips = new List<ProjectClip>
				{
					new( "clip_walk", new AssetReference( "asset_walk", "anims/walk.riganim" ), "doc_rig", 30.0f, "Walk Cycle" )
				},
				AssemblyParts = new List<AssemblyPart>
				{
					new( "part_body", "doc_body", null, "Main Body" )
					{
						ExcludePieces = new List<string> { "temp_head" },
						MaterialRemap = new Dictionary<string, string> { ["M_Skin"] = "materials/skin.vmat" }
					},
					new( "part_hat", "doc_body", "part_body", "Hat Accessory" )
					{
						LocalOffset = new Vec3( 0, 10, 25 ),
						IncludePieces = new List<string> { "hat_mesh" }
					}
				},
				MaterialOverrides = new List<MaterialOverrideRecord>
				{
					new( "ovr_armor", "materials/armor.vmat", new AssetReference( "vmat_armor", "materials/armor.vmat" ), required: true ),
					new( "ovr_custom_paint", 1, new MaterialDescriptor( "desc_paint", "Red Paint" )
					{
						BaseColorFactor = new Vec4( 1f, 0f, 0f, 1f ),
						RoughnessFactor = 0.2f,
						MetallicFactor = 0.8f
					} )
				},
				ActiveSourceDocumentId = "doc_body",
				ActiveClipId = "clip_walk",
				ActiveAssemblyPartId = "part_body"
			};

			string json = project.ToJson( indented: true );
			Require( json.Contains( "\"schemaVersion\": 1" ), "Serialized JSON must contain schemaVersion." );
			Require( json.Contains( "\"geppetto_hero_project\"" ), "Serialized JSON must contain project ID." );

			var roundTrip = DesktopProject.FromJson( json );

			Require( roundTrip.Id == project.Id, "Project ID must match after round trip." );
			Require( roundTrip.Name == project.Name, "Project Name must match after round trip." );
			Require( roundTrip.SchemaVersion == 1, "Schema version must be 1." );
			Require( roundTrip.SourceDocuments.Count == 2, "Source documents count must match." );
			Require( roundTrip.Clips.Count == 1, "Clips count must match." );
			Require( roundTrip.AssemblyParts.Count == 2, "Assembly parts count must match." );
			Require( roundTrip.MaterialOverrides.Count == 2, "Material overrides count must match." );

			var bodyPart = roundTrip.AssemblyParts[0];
			Require( bodyPart.Id == "part_body" && bodyPart.ExcludePieces.Contains( "temp_head" ), "Assembly part options must match." );
			Require( bodyPart.MaterialRemap["M_Skin"] == "materials/skin.vmat", "Material remap must match." );

			var hatPart = roundTrip.AssemblyParts[1];
			Require( hatPart.ParentPartId == "part_body", "Parent ID must match." );
			Require( MathF.Abs( hatPart.LocalOffset.y - 10f ) < 1e-4f, "LocalOffset must match." );

			var paintOvr = roundTrip.MaterialOverrides[1];
			Require( paintOvr.SlotIndex == 1, "Slot index must match." );
			Require( paintOvr.Descriptor != null && MathF.Abs( paintOvr.Descriptor.RoughnessFactor - 0.2f ) < 1e-4f, "Descriptor factor must match." );
		} );

		// 2. Two-source assembly definition
		Check( "two-source assembly definition", () =>
		{
			var project = new DesktopProject
			{
				Id = "two_source_project",
				Name = "Survey-Style Assembly",
				SourceDocuments = new List<SourceDocument>
				{
					new( "suit_src", new AssetReference( "suit_ref", "models/player_suit.fbx" ) ),
					new( "head_src", new AssetReference( "head_ref", "models/player_head.fbx" ) )
				},
				AssemblyParts = new List<AssemblyPart>
				{
					new( "suit_part", "suit_src", null, "Suit" )
					{
						ExcludePieces = new List<string> { "suit_head" }
					},
					new( "head_part", "head_src", "suit_part", "Head" )
					{
						IncludePieces = new List<string> { "face_head", "face_eyes", "neck_ring" },
						BoneMerge = BoneMergePolicy.ReconcileBindPose,
						LocalOffset = new Vec3( 0, 0, 60 )
					}
				}
			};

			project.Validate();

			var suitPart = project.AssemblyParts[0];
			var headPart = project.AssemblyParts[1];

			Require( headPart.ParentPartId == "suit_part", "Head part must have suit_part as parent." );
			Require( headPart.BoneMerge == BoneMergePolicy.ReconcileBindPose, "Head part must specify ReconcileBindPose." );

			// Test piece filtering rules
			Require( !DesktopProject.ShouldIncludePiece( suitPart, "suit_head" ), "suit_head must be excluded from suit part." );
			Require( DesktopProject.ShouldIncludePiece( suitPart, "suit_torso" ), "suit_torso must be included in suit part." );
			Require( DesktopProject.ShouldIncludePiece( headPart, "face_head" ), "face_head must be included in head part." );
			Require( !DesktopProject.ShouldIncludePiece( headPart, "discarded_helmet" ), "discarded_helmet must be excluded from head part." );
		} );

		// 3. Moving/relocating project root via AssetPathResolver
		Check( "moving/relocating project root via AssetPathResolver", () =>
		{
			var (rootA, resA) = CreateProjectFixture( "projA" );
			var (rootB, resB) = CreateProjectFixture( "projB" );

			string bundle = Path.Combine( rootA, "bundle" );
			Directory.CreateDirectory( Path.Combine( bundle, "models" ) );
			Directory.CreateDirectory( Path.Combine( bundle, "materials" ) );
			File.WriteAllText( Path.Combine( bundle, "models", "character.fbx" ), "fbx content A" );
			File.WriteAllText( Path.Combine( bundle, "materials", "skin.vmat" ), "vmat content A" );

			var projectA = new DesktopProject
			{
				Id = "relocation_test_project",
				ProjectRoot = rootA,
				SourceDocuments = new List<SourceDocument>
				{
					new( "char_doc", new AssetReference( "char_asset", "bundle/models/character.fbx" ) )
				},
				MaterialOverrides = new List<MaterialOverrideRecord>
				{
					new( "skin_ovr", "materials/skin.vmat", new AssetReference( "skin_ref", "bundle/materials/skin.vmat" ) )
				}
			};

			// Case 1: Subfolder relocation (relative paths adjust to destination inside same tree)
			var subResolver = new AssetPathResolver( bundle );
			var relocatedSub = projectA.Relocate( resA, subResolver );
			Require( relocatedSub.ProjectRoot == subResolver.ProjectRoot, "Relocated ProjectRoot must match subRoot." );
			Require( relocatedSub.SourceDocuments[0].Asset.Path == "models/character.fbx", "Relative path must adjust to subfolder." );
			Require( relocatedSub.MaterialOverrides[0].MaterialAsset!.Path == "materials/skin.vmat", "Material path must adjust to subfolder." );
			var resolvedSub = subResolver.ResolveRead( relocatedSub.SourceDocuments[0].Asset );
			Require( resolvedSub.Exists && resolvedSub.CanonicalPath == Path.Combine( bundle, "models", "character.fbx" ), "Must resolve in subfolder." );

			// Case 2: Save As to external project requires allowExternal: true
			Throws<UnauthorizedAccessException>( () => projectA.Relocate( resA, resB, allowExternal: false ) );
			var relocatedExternal = projectA.Relocate( resA, resB, allowExternal: true );
			Require( relocatedExternal.SourceDocuments[0].Asset.Kind == AssetReferenceKind.ExternalReadOnly, "External relocation must mark as ExternalReadOnly." );
			var resolvedExternal = resB.ResolveRead( relocatedExternal.SourceDocuments[0].Asset );
			Require( resolvedExternal.Exists && resolvedExternal.CanonicalPath == Path.Combine( bundle, "models", "character.fbx" ),
				"External asset must resolve to original canonical path." );

			// Case 3: Moving entire project directory preserves relative references
			string movedRoot = rootA + "_moved";
			Directory.Move( rootA, movedRoot );
			var movedResolver = new AssetPathResolver( movedRoot );
			var resolvedMoved = movedResolver.ResolveRead( projectA.SourceDocuments[0].Asset );
			Require( resolvedMoved.Exists && resolvedMoved.CanonicalPath == Path.Combine( movedRoot, "bundle", "models", "character.fbx" ),
				"Entire project move preserves relative references." );
		} );

		// 4. Duplicate ID rejection
		Check( "duplicate ID rejection", () =>
		{
			// Duplicate SourceDocument ID
			var dupDoc = new DesktopProject
			{
				Id = "dup_test",
				SourceDocuments = new List<SourceDocument>
				{
					new( "dup_id", new AssetReference( "a", "models/a.fbx" ) ),
					new( "dup_id", new AssetReference( "b", "models/b.fbx" ) )
				}
			};
			Throws<ArgumentException>( () => dupDoc.Validate(), "Duplicate source document ID 'dup_id'" );

			// Duplicate AssemblyPart ID
			var dupPart = new DesktopProject
			{
				Id = "dup_test",
				SourceDocuments = new List<SourceDocument>
				{
					new( "doc1", new AssetReference( "a", "models/a.fbx" ) )
				},
				AssemblyParts = new List<AssemblyPart>
				{
					new( "part1", "doc1" ),
					new( "part1", "doc1" )
				}
			};
			Throws<ArgumentException>( () => dupPart.Validate(), "Duplicate assembly part ID 'part1'" );

			// Duplicate Clip ID
			var dupClip = new DesktopProject
			{
				Id = "dup_test",
				SourceDocuments = new List<SourceDocument>
				{
					new( "doc1", new AssetReference( "a", "models/a.fbx" ) )
				},
				Clips = new List<ProjectClip>
				{
					new( "clip1", new AssetReference( "c1", "anims/a.riganim" ) ),
					new( "clip1", new AssetReference( "c2", "anims/b.riganim" ) )
				}
			};
			Throws<ArgumentException>( () => dupClip.Validate(), "Duplicate clip ID 'clip1'" );

			// Duplicate MaterialOverride ID
			var dupMat = new DesktopProject
			{
				Id = "dup_test",
				MaterialOverrides = new List<MaterialOverrideRecord>
				{
					new( "ovr1", "slot_a", new AssetReference( "v1", "materials/a.vmat" ) ),
					new( "ovr1", "slot_b", new AssetReference( "v2", "materials/b.vmat" ) )
				}
			};
			Throws<ArgumentException>( () => dupMat.Validate(), "Duplicate material override ID 'ovr1'" );
		} );

		// 5. Cyclic parent hierarchy rejection
		Check( "cyclic parent hierarchy rejection", () =>
		{
			// Self cycle (A -> A)
			var selfCycle = new DesktopProject
			{
				Id = "cycle_test",
				SourceDocuments = new List<SourceDocument> { new( "doc1", new AssetReference( "a", "models/a.fbx" ) ) },
				AssemblyParts = new List<AssemblyPart> { new( "partA", "doc1", "partA" ) }
			};
			Throws<InvalidOperationException>( () => selfCycle.Validate(), "cannot be its own parent" );

			// Two-node cycle (A -> B -> A)
			var twoCycle = new DesktopProject
			{
				Id = "cycle_test",
				SourceDocuments = new List<SourceDocument> { new( "doc1", new AssetReference( "a", "models/a.fbx" ) ) },
				AssemblyParts = new List<AssemblyPart>
				{
					new( "partA", "doc1", "partB" ),
					new( "partB", "doc1", "partA" )
				}
			};
			Throws<InvalidOperationException>( () => twoCycle.Validate(), "Cyclic parent hierarchy detected" );

			// Three-node cycle (A -> B -> C -> A)
			var threeCycle = new DesktopProject
			{
				Id = "cycle_test",
				SourceDocuments = new List<SourceDocument> { new( "doc1", new AssetReference( "a", "models/a.fbx" ) ) },
				AssemblyParts = new List<AssemblyPart>
				{
					new( "partA", "doc1", "partB" ),
					new( "partB", "doc1", "partC" ),
					new( "partC", "doc1", "partA" )
				}
			};
			Throws<InvalidOperationException>( () => threeCycle.Validate(), "Cyclic parent hierarchy detected" );

			// Nonexistent parent
			var missingParent = new DesktopProject
			{
				Id = "missing_parent",
				SourceDocuments = new List<SourceDocument> { new( "doc1", new AssetReference( "a", "models/a.fbx" ) ) },
				AssemblyParts = new List<AssemblyPart> { new( "partA", "doc1", "ghost_parent" ) }
			};
			Throws<ArgumentException>( () => missingParent.Validate(), "references non-existent parent 'ghost_parent'" );
		} );

		// 6. Unknown future schema version failure
		Check( "unknown future schema version failure", () =>
		{
			string jsonFuture = """
			{
				"schemaVersion": 999,
				"id": "future_project",
				"name": "From the future"
			}
			""";
			Throws<NotSupportedException>( () => DesktopProject.FromJson( jsonFuture ), "Project schema version 999 is unsupported" );

			string jsonZero = """
			{
				"schemaVersion": 0,
				"id": "zero_version_project"
			}
			""";
			Throws<NotSupportedException>( () => DesktopProject.FromJson( jsonZero ), "Project schema version 0 is unsupported" );
		} );

		// 7. Unknown optional JSON fields preserved on save
		Check( "unknown optional JSON fields preserved on save", () =>
		{
			string jsonWithUnknownFields = """
			{
				"schemaVersion": 1,
				"id": "extension_data_project",
				"name": "Preserve Metadata",
				"customEngineVersion": "3.5.0-alpha",
				"pipelineConfiguration": {
					"lodBias": 2.5,
					"bakeShadows": true
				},
				"sourceDocuments": [
					{
						"id": "doc1",
						"asset": {
							"id": "asset1",
							"path": "models/mesh.fbx",
							"kind": "projectRelative"
						},
						"format": "fbx",
						"artistNotes": "Rigged in Maya 2025"
					}
				],
				"assemblyParts": [
					{
						"id": "part1",
						"name": "BodyPart",
						"sourceDocumentId": "doc1",
						"editorColorHex": "#00FF88"
					}
				],
				"materialOverrides": [
					{
						"id": "mo1",
						"slotName": "materials/head.vmat",
						"materialAsset": {
							"id": "vmat1",
							"path": "materials/head.vmat",
							"kind": "projectRelative"
						},
						"customShaderPass": "ForwardPlus"
					}
				]
			}
			""";

			var project = DesktopProject.FromJson( jsonWithUnknownFields );
			Require( project.Id == "extension_data_project", "Project must deserialize successfully." );
			Require( project.AdditionalData != null && project.AdditionalData.ContainsKey( "customEngineVersion" ),
				"Root AdditionalData must capture customEngineVersion." );
			Require( project.SourceDocuments[0].AdditionalData != null && project.SourceDocuments[0].AdditionalData!.ContainsKey( "artistNotes" ),
				"SourceDocument AdditionalData must capture artistNotes." );

			string serialized = project.ToJson( indented: true );

			Require( serialized.Contains( "\"customEngineVersion\": \"3.5.0-alpha\"" ), "Serialized JSON must retain root unknown field." );
			Require( serialized.Contains( "\"pipelineConfiguration\"" ), "Serialized JSON must retain root complex object." );
			Require( serialized.Contains( "\"artistNotes\": \"Rigged in Maya 2025\"" ), "Serialized JSON must retain source doc unknown field." );
			Require( serialized.Contains( "\"editorColorHex\": \"#00FF88\"" ), "Serialized JSON must retain assembly part unknown field." );
			Require( serialized.Contains( "\"customShaderPass\": \"ForwardPlus\"" ), "Serialized JSON must retain material override unknown field." );

			// Reopen second time
			var reopened = DesktopProject.FromJson( serialized );
			Require( reopened.AdditionalData!.ContainsKey( "customEngineVersion" ), "Second reopen must still retain customEngineVersion." );
		} );

		// 8. Material override identity persistence
		Check( "material override identity persistence", () =>
		{
			var (root, resolver) = CreateProjectFixture( "material_test" );
			string vmatPath = Path.Combine( root, "materials", "camhead_panel.vmat" );
			string texPath = Path.Combine( root, "textures", "panel_rough.png" );
			PngWriter.WriteFileRgba( texPath, new byte[] { 100, 100, 100, 255 }, 1, 1 );

			File.WriteAllText( vmatPath, @"// VMAT
Layer0
{
	shader ""shaders/complex.shader""
	TextureRoughness ""textures/panel_rough.png""
}
" );

			var project = new DesktopProject
			{
				Id = "material_identity_project",
				ProjectRoot = root,
				MaterialOverrides = new List<MaterialOverrideRecord>
				{
					new( "stable_panel_override_id", "materials/camhead/panel.vmat",
						new AssetReference( "panel_asset_ref", "materials/camhead_panel.vmat" ), required: true ),
					new( "stable_inline_override_id", 3, new MaterialDescriptor( "inline_desc_id", "Direct Descriptor" )
					{
						RoughnessFactor = 0.65f,
						MetallicFactor = 0.95f,
						EmissionFactor = new Vec3( 0.2f, 0.4f, 0.6f )
					} )
				}
			};

			string json = project.ToJson();
			var reopened = DesktopProject.FromJson( json );

			Require( reopened.MaterialOverrides.Count == 2, "Material overrides count must match." );
			var ovr0 = reopened.MaterialOverrides[0];
			Require( ovr0.Id == "stable_panel_override_id", "Override ID must be preserved." );
			Require( ovr0.SlotName == "materials/camhead/panel.vmat", "SlotName must be preserved." );
			Require( ovr0.MaterialAsset?.Id == "panel_asset_ref", "MaterialAsset reference must be preserved." );
			Require( ovr0.Required, "Required flag must be preserved." );

			var ovr1 = reopened.MaterialOverrides[1];
			Require( ovr1.Id == "stable_inline_override_id", "Inline override ID must be preserved." );
			Require( ovr1.SlotIndex == 3, "SlotIndex must be preserved." );
			Require( ovr1.Descriptor != null && MathF.Abs( ovr1.Descriptor.MetallicFactor - 0.95f ) < 1e-4f,
				"Descriptor metallic factor must be preserved." );
			Require( ovr1.Descriptor != null && MathF.Abs( ovr1.Descriptor.RoughnessFactor - 0.65f ) < 1e-4f,
				"Descriptor roughness factor must be preserved." );

			// Test application to MaterialResolver
			var matResolver = new MaterialResolver( resolver, "test_doc" );
			reopened.ApplyMaterialOverrides( matResolver );

			var res0 = matResolver.ResolveSlot( 0, "materials/camhead/panel.vmat" );
			Require( res0.Success, "Resolver must successfully resolve applied .vmat override." );
			Require( res0.Descriptor.RoughnessMap != null && res0.Descriptor.RoughnessMap.Reference.Path == "textures/panel_rough.png",
				"Resolved descriptor must carry parsed RoughnessMap." );

			var res1 = matResolver.ResolveSlot( 3, "unnamed_slot" );
			Require( res1.Descriptor.MetallicFactor == 0.95f, "Resolver must apply inline descriptor override at slot index 3." );
			Require( res1.Descriptor.RoughnessFactor == 0.65f, "Resolver must preserve inline roughness factor." );
		} );

		// 9. Camhead authoring-frame adapter coordinate transformation
		Check( "Camhead authoring-frame adapter coordinate transformation", () =>
		{
			// Camhead Blender frame: p -> (-p.y, p.x, p.z)
			var p = new Vec3( 10, 20, 30 );
			var transformed = DesktopProject.TransformPoint( p, AuthoringFrameAdapter.BlenderCitizenInverted );

			Require( MathF.Abs( transformed.x - (-20f) ) < 1e-4f, "Transformed x must be -p.y (-20)." );
			Require( MathF.Abs( transformed.y - 10f ) < 1e-4f, "Transformed y must be p.x (10)." );
			Require( MathF.Abs( transformed.z - 30f ) < 1e-4f, "Transformed z must be p.z (30)." );

			// None adapter leaves point unchanged
			var unchanged = DesktopProject.TransformPoint( p, AuthoringFrameAdapter.None );
			Require( unchanged.x == p.x && unchanged.y == p.y && unchanged.z == p.z, "None adapter must not mutate point." );
		} );

		// 10. Atomic save and load from disk
		Check( "atomic save and load from disk", () =>
		{
			var (root, _) = CreateProjectFixture( "save_load_test" );
			string manifestPath = Path.Combine( root, "project" + DesktopProject.ProjectExtension );

			var project = new DesktopProject
			{
				Id = "disk_saved_project",
				Name = "Saved To Disk",
				SourceDocuments = new List<SourceDocument>
				{
					new( "doc1", new AssetReference( "ref1", "models/test.fbx" ) )
				}
			};

			project.Save( manifestPath, atomic: true );
			Require( File.Exists( manifestPath ), "Manifest file must exist on disk after Save." );

			var loaded = DesktopProject.Load( manifestPath );
			Require( loaded.Id == "disk_saved_project", "Loaded project ID must match." );
			Require( loaded.ProjectRoot == root, "Loaded project ProjectRoot must match directory." );
		} );
	}
}

#if RSBLOB_SEMANTIC_ANALYSIS
using System;
using System.Collections.Generic;
using System.Linq;

namespace WitcherScriptMerger.FileIndex;

// Compare class fields and defaults individually so independent changes do not become whole-class conflicts.
internal partial class ModFileIndex
{
	private readonly Dictionary<string, string> scopedFieldTypes = new( StringComparer.OrdinalIgnoreCase );
	private readonly Dictionary<string, string> scopedTypeParents = new( StringComparer.OrdinalIgnoreCase );

	// A default statement has no type declaration. Find the matching field in the mod, vanilla or agreed
	// mod definitions, then follow agreed parent classes if needed.
	private void ResolveScopedMemberTypes()
	{
		var fields = Files.SelectMany( file => file.Mods ).Where( source => source.ScopedMember is { IsDefault: false } ).ToArray();
		var ownTypes = fields.GroupBy( source => (source.Name.ToUpperInvariant(), source.ScopedMember.Key.ToUpperInvariant()) )
			.ToDictionary( group => group.Key, group => group.First().ScopedMember.Type );
		var uniqueTypes = fields.GroupBy( source => source.ScopedMember.Key, StringComparer.OrdinalIgnoreCase )
			.Select( group => (group.Key, Types: group.Select( source => source.ScopedMember.Type ).Distinct( StringComparer.OrdinalIgnoreCase ).ToArray()) )
			.Where( group => group.Types.Length == 1 ).ToDictionary( group => group.Key, group => group.Types[0], StringComparer.OrdinalIgnoreCase );
		var parents = dependencySources.Concat( looseDependencySources )
			.SelectMany( source => source.Metadata.TypeParents ?? Array.Empty<RsBlobTypeParent>() )
			.Concat( scopedTypeParents.Select( value => new RsBlobTypeParent( value.Key, value.Value ) ) )
			.GroupBy( value => value.Name, StringComparer.OrdinalIgnoreCase )
			.Where( group => group.Select( value => value.Parent ).Distinct( StringComparer.OrdinalIgnoreCase ).Count() == 1 )
			.ToDictionary( group => group.Key, group => group.First().Parent, StringComparer.OrdinalIgnoreCase );

		foreach ( var source in Files.SelectMany( file => file.Mods ).Where( source => source.ScopedMember is { IsDefault: true } ) )
		{
			var member = source.ScopedMember;
			string key = ScriptSymbolKey.Create( "Fields", member.Owner, member.Name );

			if ( ownTypes.TryGetValue( (source.Name.ToUpperInvariant(), key.ToUpperInvariant()), out string own ) )
				source.ScopedMemberType = own;
			else if ( scopedFieldTypes.TryGetValue( key, out string type ) )
				source.ScopedMemberType = type;
			else if ( uniqueTypes.TryGetValue( key, out string unique ) )
				source.ScopedMemberType = unique;

			if ( string.IsNullOrEmpty( source.ScopedMemberType ) )
			{
				string owner = member.Owner;
				var visited = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

				while ( visited.Add( owner ) && parents.TryGetValue( owner, out string parent ) )
				{
					key = ScriptSymbolKey.Create( "Fields", parent, member.Name );

					if ( scopedFieldTypes.TryGetValue( key, out string inherited ) || uniqueTypes.TryGetValue( key, out inherited ) )
					{
						source.ScopedMemberType = inherited;

						break;
					}

					owner = parent;
				}
			}
		}
	}

}
#endif

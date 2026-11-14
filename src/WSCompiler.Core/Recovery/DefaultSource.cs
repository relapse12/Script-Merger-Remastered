using System.Globalization;
using System.Text.Json;
using WSCompiler.Core.Scripts.Standalone;

namespace WSCompiler.Core.Scripts;

// Compiled scopes can omit the fields whose defaults they change. References supply those types.
internal sealed class DefaultSource
{
	private readonly Dictionary<(string Owner, string Field), TypeRef> fields = [];
	private readonly Dictionary<string, string> parents = new( StringComparer.Ordinal );

	internal DefaultSource( ScriptBlob blob, IEnumerable<SourceFile> references )
	{
		var types = new Dictionary<string, ClassDecl>( StringComparer.Ordinal );
		foreach ( var item in new[] { BlobReferences.Read( blob ) }.Concat( references ).SelectMany( file => file.Items ) )
		{
			if ( item is ClassItem { Decl: var type } )
			{
				string owner = Symbols.ClassKey( type );
				if ( !types.TryGetValue( owner, out var declaration ) )
				{
					declaration = new ClassDecl { Name = type.Name, IsStruct = type.IsStruct, StateMachineOf = type.StateMachineOf };
					types.Add( owner, declaration );
				}
				declaration.Parent ??= type.Parent;

				foreach ( var field in type.Fields )
					foreach ( string name in field.Names )
						fields.TryAdd( (owner, name), field.Type );
			}
			else if ( item is FieldItem { Decl.Annotation: { Kind: AnnotationKind.AddField, Class: { } owner } } added )
				foreach ( string name in added.Decl.Names )
					fields.TryAdd( (owner, name), added.Decl.Type );
		}

		// Use the compiler's inheritance rules, including states inherited through their machines.
		var symbols = new Symbols();
		symbols.AddTypeNames( [new SourceFile( "default types", types.Values.Select( type => (Item)new ClassItem( type ) ).ToList() )] );
		symbols.ResolveStateParents();
		foreach ( var type in symbols.Classes.Values )
			if ( type.Parent is { } parent )
				parents.Add( type.Name, parent );
	}

	private TypeRef? FieldType( string owner, string name )
	{
		var visited = new HashSet<string>( StringComparer.Ordinal );
		while ( visited.Add( owner ) )
		{
			if ( fields.TryGetValue( (owner, name), out var type ) )
				return type;
			if ( !parents.TryGetValue( owner, out owner! ) )
				break;
		}
		return null;
	}

	internal bool TryWrite( string owner, ScriptDefault value, out string text )
	{
		text = "";
		var type = value.Property == "autoState" ? new TypeRef( "name" ) : FieldType( owner, value.Property );
		try
		{
			string expression = Expression( type, value.Value, value.Children, false );
			var parsed = ( (ClassItem)Parser.ParseSource( $"class DefaultProbe {{ default value = {expression}; }}", false ).Single() ).Decl.Defaults.Single();
			var node = DefaultValues.ToNode( parsed.Value );
			if ( node.Value != value.Value || JsonSerializer.Serialize( node.Children ) != JsonSerializer.Serialize( value.Children ) )
				return false;
			text = expression;
			return true;
		}
		catch ( Exception error ) when ( error is ScriptSyntaxException or InvalidDataException or CompileException )
		{
			return false;
		}
	}

	private string Expression( TypeRef? type, string value, IReadOnlyList<ScriptDefaultNode> children, bool nested )
	{
		if ( type?.Name == "array" && type.Args.Count == 1 && value.Length == 0 && children.All( child => child.Key.Length == 0 ) )
			return "{ " + string.Join( ", ", children.Select( child => Expression( type.Args[0], child.Value, child.Children, true ) ) ) + " }";

		if ( children.Count > 0 )
		{
			if ( type is null || children.Any( child => child.Key.Length == 0 ) )
				throw new InvalidDataException( "The compound default's type is unavailable." );
			return "(" + string.Join( ", ", children.Select( child => child.Key + " = "
				+ Expression( FieldType( type.Name, child.Key ), child.Value, child.Children, true ) ) ) + ")";
		}

		if ( type is null && value is not ("true" or "false" or "NULL")
			&& !double.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out _ ) )
			throw new InvalidDataException( "The default's field type is unavailable." );

		if ( nested || type?.Name is "String" or "string" )
			return Decompiler.Quote( value );
		if ( type?.Name is "CName" or "name" )
			return Decompiler.Quote( value, '\'' );
		return value;
	}
}

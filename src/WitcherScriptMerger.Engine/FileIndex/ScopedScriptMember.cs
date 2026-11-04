#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using WSCompiler.Core.Scripts;
using WSCompiler.Core.Scripts.Standalone;

namespace WitcherScriptMerger.FileIndex;

// Index fields and defaults individually so mods changing different members can coexist.
internal sealed record ScopedScriptMember( string Owner, string Name, string Type, uint Flags,
	string Binding, string Value, bool IsDefault, bool IsAnnotation = false )
{
	internal string Key => ScriptSymbolKey.Create( IsDefault ? "Defaults" : "Fields", Owner, Name );
	internal ScriptSymbolKind Kind => IsDefault ? ScriptSymbolKind.FieldDefault
		: IsAnnotation ? ScriptSymbolKind.AddedField : ScriptSymbolKind.FieldDefinition;

	// Defaults use their field's type because the default statement does not declare one.
	internal string Fingerprint( string? inheritedType = null )
	{
		string type = NormalizeType( string.IsNullOrEmpty( Type ) ? inheritedType ?? "" : Type );

		if ( !IsDefault )
			return JsonSerializer.Serialize( new[] { type, ( Flags & 0x1FC00F ).ToString( CultureInfo.InvariantCulture ), Binding } );

		return JsonSerializer.Serialize( new[] { type, NormalizeValue( type, Value ) } );
	}

	// Compare typed values so equivalent literals such as 1.0 and 1.0f do not conflict.
	private static string NormalizeValue( string type, string value )
	{
		if ( type == "float" && float.TryParse( value.TrimEnd( 'f', 'F' ), NumberStyles.Float, CultureInfo.InvariantCulture, out float number ) )
			return BitConverter.SingleToInt32Bits( number ).ToString( "X8", CultureInfo.InvariantCulture );

		if ( type is "int" or "byte" && long.TryParse( value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer ) )
			return integer.ToString( CultureInfo.InvariantCulture );

		if ( type == "bool" && bool.TryParse( value, out bool boolean ) )
			return boolean ? "true" : "false";

		if ( type.StartsWith( "array<", StringComparison.Ordinal ) && type.EndsWith( '>' ) )
		{
			if ( value.Length == 0 )
				return "[]";

			try
			{
				var nodes = JsonSerializer.Deserialize<List<ScriptDefaultNode>>( value );

				if ( nodes is not null )
					return JsonSerializer.Serialize( NormalizeNodes( nodes, type[6..^1] ) );
			}
			catch ( JsonException ) { }
		}

		return value;
	}

	private static List<ScriptDefaultNode> NormalizeNodes( IEnumerable<ScriptDefaultNode> nodes, string type ) => nodes.Select( node =>
		type.StartsWith( "array<", StringComparison.Ordinal ) && type.EndsWith( '>' )
			? node with
			{
				Children = NormalizeNodes( node.Children, type[6..^1] )
			}
			: node with
			{
				Value = NormalizeValue( type, node.Value )
			} ).ToList();

	internal static string NormalizeType( string type )
	{
		try
		{
			return ScriptTypeNames.Format( type ).Replace( " ", "" );
		}
		catch ( Exception error ) when ( error is ArgumentException or IndexOutOfRangeException )
		{
			throw new InvalidDataException( "Invalid compiled field type metadata.", error );
		}
	}

	// Use the compiler's field flags so loose and compiled versions of a member can be compared.
	internal static IEnumerable<ScopedScriptMember> FromClass( ClassDecl type, string? scope = null )
	{
		string owner = scope ?? ( type.StateMachineOf is { Length: > 0 } machine ? machine + "State" + type.Name : type.Name );

		foreach ( var field in type.Fields.Where( field => !field.Specifiers.IsImport ) )
		{
			var s = field.Specifiers;
			uint flags = s.Visibility switch
			{
				Visibility.Private => 0x10000u,
				Visibility.Protected => 0x20000u,
				_ => 0x40000u
			};

			if ( s.IsEditable )
				flags |= 1;

			if ( s.IsConst )
				flags |= 2;

			if ( s.IsInlined )
				flags |= 4;

			if ( s.IsSaved )
				flags |= 0x8000;

			if ( field.IsBinding )
			{
				flags |= 0x8000A;

				if ( s.IsOptional )
					flags |= 0x100000;
			}

			foreach ( string name in field.Names )
				yield return new( owner, name, TypeName( field.Type ), flags, field.Binding ?? "", "", false,
				field.Annotation?.Kind == AnnotationKind.AddField );
		}

		foreach ( var value in type.BinaryDefaults )
			foreach ( var member in DefaultMembers( owner, value.Property, value.Value, value.Children ) )
				yield return member;

		foreach ( var value in type.Defaults.GroupBy( value => value.Field, StringComparer.Ordinal ).Select( group => group.Last() ) )
			yield return new( owner, value.Field, "", 0, "", DefaultText( value.Value ), true );
	}

	internal static IEnumerable<ScopedScriptMember> DefaultMembers(
		string owner,
		string property,
		string value,
		IReadOnlyList<ScriptDefaultNode> children )
	{
		if ( children.Count > 0 && children.All( child => child.Key.Length > 0 ) )
			foreach ( var child in children )
				foreach ( var member in DefaultMembers( owner, property + "." + child.Key, child.Value, child.Children ) )
					yield return member;
		else
			yield return new( owner, property, "", 0, "", children.Count == 0 ? value : JsonSerializer.Serialize( children ), true );
	}

	private static string TypeName( TypeRef type ) => type.Args.Count == 0 ? NormalizeType( type.Name )
		: type.Name + "<" + string.Join( ",", type.Args.Select( TypeName ) ) + ">";

	private static string DefaultText( Expr expression ) => expression.Kind switch
	{
		IntLit value => value.Value.ToString( CultureInfo.InvariantCulture ),
		FloatLit value => value.Value.ToString( "R", CultureInfo.InvariantCulture ),
		StrLit value => value.Value,
		NameLit value => value.Value,
		BoolLit value => value.Value ? "true" : "false",
		Ident value => value.Name,
		NullLit => "NULL",
		Unary { Op: "-" } value => "-" + DefaultText( value.Operand ),
		DefaultArray value => JsonSerializer.Serialize( ArrayNodes( value ) ),
		_ => "source-expression:" + WSCompiler.Recovery.SourceWriter.Expression( expression )
	};

	private static List<ScriptDefaultNode> ArrayNodes( DefaultArray array ) => array.Values.Select( value => value.Kind is DefaultArray nested
		? new ScriptDefaultNode( "", "", ArrayNodes( nested ) ) : new ScriptDefaultNode( "", DefaultText( value ), [] ) ).ToList();
}

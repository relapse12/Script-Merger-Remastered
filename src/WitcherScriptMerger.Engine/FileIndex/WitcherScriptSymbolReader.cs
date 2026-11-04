#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WitcherScriptMerger.FileIndex;

internal sealed record WitcherScriptFunctionSymbol(
	string Key,
	ScriptSymbolKind Kind,
	string? DefinitionFingerprint = null,
	bool IsVanillaEquivalent = false,
	ScopedScriptMember? Member = null );

// Uses a token scan so conflict discovery can still read scripts that the compiler parser does not support.
internal static class WitcherScriptSymbolReader
{
	private static readonly UTF8Encoding StrictUtf8 = new( false, true );
	private static readonly UnicodeEncoding StrictUtf16LittleEndian = new( false, false, true );
	private static readonly UnicodeEncoding StrictUtf16BigEndian = new( true, false, true );
	private static readonly string[] MergeOperators = {
		">>>=", "<<=", ">>=", "&&=", "||=", "**=", ">>>", "===", "!==",
		"++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "&&", "||", "==", "!=", "<=", ">=", "<<", ">>", "**", "<>"
	};

	public static IReadOnlyList<WitcherScriptFunctionSymbol> Read(
		string path,
		ISet<string>? declaredTypes = null,
		bool includeMembers = true,
		ICollection<string>? memberWarnings = null,
		IDictionary<string, string>? typeParents = null )
	{
		string text;
		IReadOnlyList<LexToken> tokens;

		if ( string.IsNullOrWhiteSpace( path ) )
			throw new ArgumentException( UiText.T( "WitcherScriptSymbolReader", "A WitcherScript path is required." ), nameof( path ) );

		text = Decode( File.ReadAllBytes( Path.GetFullPath( path ) ) );
		tokens = Tokenize( text );

		if ( declaredTypes != null )
			CollectDeclaredTypes( tokens, declaredTypes );

		return new SymbolScan( tokens, path, includeMembers, memberWarnings, typeParents ).Run();
	}

	private static void CollectDeclaredTypes( IReadOnlyList<LexToken> tokens, ISet<string> declaredTypes )
	{
		for ( int index = 0; index + 1 < tokens.Count; index++ )
		{
			bool isTypeKeyword = tokens[index].Text.Equals( "class", StringComparison.OrdinalIgnoreCase )
				|| tokens[index].Text.Equals( "struct", StringComparison.OrdinalIgnoreCase )
				|| tokens[index].Text.Equals( "enum", StringComparison.OrdinalIgnoreCase );

			if ( isTypeKeyword && IsIdentifier( tokens[index + 1].Text ) )
				declaredTypes.Add( tokens[index + 1].Text );
		}
	}

	private sealed class SymbolScan
	{
		private readonly IReadOnlyList<LexToken> tokens;
		private readonly string path;
		private readonly bool includeMembers;
		private readonly ICollection<string>? memberWarnings;
		private readonly IDictionary<string, string>? typeParents;
		private readonly Dictionary<string, SymbolDefinition> symbols = new( StringComparer.OrdinalIgnoreCase );
		private readonly Dictionary<string, ScopedScriptMember> members = new( StringComparer.OrdinalIgnoreCase );
		private readonly Stack<ScopeFrame> scopes = new();
		private int index;
		private int braceDepth;

		// The class or state named just before its opening brace.
		private string? pendingScope;

		private string? annotationScope;
		private ScriptSymbolKind? annotationKind;

		internal SymbolScan(
			IReadOnlyList<LexToken> tokens,
			string path,
			bool includeMembers,
			ICollection<string>? memberWarnings,
			IDictionary<string, string>? typeParents )
		{
			this.tokens = tokens;
			this.path = path;
			this.includeMembers = includeMembers;
			this.memberWarnings = memberWarnings;
			this.typeParents = typeParents;
		}

		internal IReadOnlyList<WitcherScriptFunctionSymbol> Run()
		{
			for ( index = 0; index < tokens.Count; index++ )
				ReadToken();

			return Results();
		}

		private void ReadToken()
		{
			string token = tokens[index].Text;

			switch ( token )
			{
				case "{":
					OpenBrace();
					return;

				case "}":
					CloseBrace();
					return;

				case ";":
					pendingScope = null;
					ClearAnnotation();
					return;
			}

			if ( token == "@" && index + 1 < tokens.Count )
				ReadAnnotation();
			else if ( !TryReadClassHeader( token ) && !TryReadStateHeader( token ) && !TryReadMember( token ) )
				TryRecordFunction( token );
		}

		private void ClearAnnotation()
		{
			annotationScope = null;
			annotationKind = null;
		}

		private void OpenBrace()
		{
			braceDepth++;

			if ( !string.IsNullOrWhiteSpace( pendingScope ) )
			{
				scopes.Push( new ScopeFrame( pendingScope, braceDepth ) );
				pendingScope = null;
			}
		}

		private void CloseBrace()
		{
			if ( scopes.Count > 0 && scopes.Peek().BraceDepth == braceDepth )
				scopes.Pop();

			braceDepth = Math.Max( 0, braceDepth - 1 );
			ClearAnnotation();
		}

		private void ReadAnnotation()
		{
			string annotation;

			if ( includeMembers && tokens[index + 1].Text.Equals( "addField", StringComparison.OrdinalIgnoreCase ) && TryReadAddedField() )
				return;

			annotation = tokens[++index].Text;

			if ( !IsFunctionAnnotation( annotation ) )
			{
				ClearAnnotation();

				return;
			}

			annotationScope = "Global";
			annotationKind = GetAnnotationKind( annotation, annotationScope );

			if ( index + 1 < tokens.Count && tokens[index + 1].Text == "(" )
				ReadAnnotationScope( annotation );
		}

		private void ReadAnnotationScope( string annotation )
		{
			index += 2;

			if ( index < tokens.Count && IsIdentifier( tokens[index].Text ) )
			{
				annotationScope = tokens[index].Text;
				annotationKind = GetAnnotationKind( annotation, annotationScope );
			}

			while ( index + 1 < tokens.Count && tokens[index + 1].Text != ")" )
				index++;

			if ( index + 1 < tokens.Count )
				index++;
		}

		private bool TryReadAddedField()
		{
			int end = index;

			while ( end < tokens.Count && tokens[end].Text != ";" )
				end++;

			if ( end == tokens.Count )
				return false;

			try
			{
				var parsed = WSCompiler.Core.Scripts.Standalone.Parser.ParseSource( JoinTokens( index, end ), false );
				var field = ( (WSCompiler.Core.Scripts.Standalone.FieldItem)parsed.Single() ).Decl;
				var type = new WSCompiler.Core.Scripts.Standalone.ClassDecl { Name = field.Annotation!.Class!, Fields = new() { field } };

				foreach ( var member in ScopedScriptMember.FromClass( type ) )
					members[member.Key] = member;
			}
			catch ( WSCompiler.Core.Scripts.Standalone.ScriptSyntaxException )
			{
			}

			index = end;

			return true;
		}

		private bool TryReadClassHeader( string token )
		{
			if ( !token.Equals( "class", StringComparison.OrdinalIgnoreCase ) || index + 1 >= tokens.Count || !IsIdentifier( tokens[index + 1].Text ) )
				return false;

			pendingScope = tokens[index + 1].Text;

			if ( typeParents != null && index + 3 < tokens.Count && tokens[index + 2].Text.Equals( "extends", StringComparison.OrdinalIgnoreCase ) )
				typeParents[pendingScope] = tokens[index + 3].Text;

			ClearAnnotation();

			return true;
		}

		// A state's functions belong to a scope named after its class and the state.
		private bool TryReadStateHeader( string token )
		{
			bool isStateHeader = token.Equals( "state", StringComparison.OrdinalIgnoreCase )
				&& index + 3 < tokens.Count
				&& IsIdentifier( tokens[index + 1].Text )
				&& tokens[index + 2].Text.Equals( "in", StringComparison.OrdinalIgnoreCase )
				&& IsIdentifier( tokens[index + 3].Text );

			if ( !isStateHeader )
				return false;

			pendingScope = tokens[index + 3].Text + "State" + tokens[index + 1].Text;
			ClearAnnotation();

			return true;
		}

		private bool TryReadMember( string token )
		{
			int start = index;
			int end;

			if ( !includeMembers || scopes.Count == 0 || scopes.Peek().BraceDepth != braceDepth )
				return false;

			if ( token is not ( "var" or "autobind" or "default" or "defaults" ) )
				return false;

			// Include the modifiers before var or autobind in the comparison.
			if ( token is "var" or "autobind" )
				start = DeclarationStart( index );

			if ( token == "defaults" && index + 1 < tokens.Count && tokens[index + 1].Text == "{" )
				end = DefaultsBlockEnd( index + 2 );
			else
				end = DeclarationEnd( index );

			if ( end >= tokens.Count )
				return false;

			AddMembers( JoinTokens( start, end ) );
			index = end;

			return true;
		}

		private int DeclarationStart( int position )
		{
			while ( position > 0 && tokens[position - 1].Text is not "{" and not "}" and not ";" )
				position--;

			return position;
		}

		private int DefaultsBlockEnd( int position )
		{
			int depth = 1;

			while ( position < tokens.Count && depth > 0 )
			{
				if ( tokens[position].Text == "{" )
					depth++;
				else if ( tokens[position].Text == "}" )
					depth--;

				if ( depth > 0 )
					position++;
			}

			return position;
		}

		private int DeclarationEnd( int position )
		{
			int depth = 0;

			while ( position < tokens.Count )
			{
				string next = tokens[position].Text;

				if ( depth == 0 && next is ";" or "}" )
					break;

				if ( next is "{" or "(" or "[" )
					depth++;
				else if ( next is "}" or ")" or "]" )
					depth--;

				position++;
			}

			return position;
		}

		private void AddMembers( string fragment )
		{
			try
			{
				var parsed = WSCompiler.Core.Scripts.Standalone.Parser.ParseSource( "class ScopedMember { " + fragment + " }", false );
				var declaration = ( (WSCompiler.Core.Scripts.Standalone.ClassItem)parsed.Single() ).Decl;

				foreach ( var member in ScopedScriptMember.FromClass( declaration, scopes.Peek().Name ) )
					members[member.Key] = member;
			}
			catch ( WSCompiler.Core.Scripts.Standalone.ScriptSyntaxException error )
			{
				memberWarnings?.Add( UiText.Format( "ScopedMembers", "{0}: a field or default declaration could not be checked ({1}).", Path.GetFileName( path ), error.Message ) );
			}
		}

		// Only an unambiguous, unannotated definition can be compared with vanilla.
		private void TryRecordFunction( string token )
		{
			bool isFunction = ( token.Equals( "function", StringComparison.OrdinalIgnoreCase ) || token.Equals( "event", StringComparison.OrdinalIgnoreCase ) )
				&& index + 1 < tokens.Count
				&& IsIdentifier( tokens[index + 1].Text );
			string scope;
			string key;
			ScriptSymbolKind kind;
			string? fingerprint;

			if ( !isFunction )
				return;

			scope = annotationScope ?? ( scopes.Count > 0 ? scopes.Peek().Name : "Global" );
			key = ScriptSymbolKey.Function( scope, tokens[index + 1].Text );
			kind = annotationKind ?? ( scope.Equals( "Global", StringComparison.OrdinalIgnoreCase ) ? ScriptSymbolKind.GlobalFunction : ScriptSymbolKind.ClassFunction );
			fingerprint = annotationKind == null ? CreateDefinitionFingerprint( tokens, index ) : null;

			if ( symbols.TryGetValue( key, out SymbolDefinition? existing ) && existing != null )
				symbols[key] = new SymbolDefinition( existing.Kind | kind, null, existing.Count + 1 );
			else
				symbols.Add( key, new SymbolDefinition( kind, fingerprint, 1 ) );

			ClearAnnotation();
		}

		private string JoinTokens( int start, int end )
		{
			return string.Join( " ", tokens.Skip( start ).Take( end - start + 1 ).Select( value => value.Text ) );
		}

		private IReadOnlyList<WitcherScriptFunctionSymbol> Results()
		{
			foreach ( var pair in members.ToArray() )
			{
				if ( pair.Value.IsDefault && members.TryGetValue( ScriptSymbolKey.Create( "Fields", pair.Value.Owner, pair.Value.Name ), out var field ) )
					members[pair.Key] = pair.Value with
					{
						Type = field.Type
					};
			}

			return symbols.OrderBy( pair => pair.Key, StringComparer.OrdinalIgnoreCase )
				.Select( pair => new WitcherScriptFunctionSymbol(
					pair.Key,
					pair.Value.Kind,
					pair.Value.Count == 1 ? pair.Value.Fingerprint : null ) )
				.Concat( members.Values.Select( member => new WitcherScriptFunctionSymbol( member.Key, member.Kind, Member: member ) ) )
				.ToArray();
		}
	}

	// Annotated members always count as mod changes.
	internal static IReadOnlyList<WitcherScriptFunctionSymbol> MarkVanillaEquivalent(
		IEnumerable<WitcherScriptFunctionSymbol> modSymbols,
		IEnumerable<WitcherScriptFunctionSymbol> vanillaSymbols )
	{
		Dictionary<string, WitcherScriptFunctionSymbol> vanillaByKey = ( vanillaSymbols
			?? Enumerable.Empty<WitcherScriptFunctionSymbol>() )
			.ToDictionary( symbol => symbol.Key, StringComparer.OrdinalIgnoreCase );

		return ( modSymbols ?? Enumerable.Empty<WitcherScriptFunctionSymbol>() )
			.Select( symbol => symbol with
			{
				IsVanillaEquivalent = symbol.Member is { } member
					? !member.IsAnnotation && vanillaByKey.TryGetValue( symbol.Key, out var original )
						&& original.Member is { } vanillaMember && member.Fingerprint( vanillaMember.Type ) == vanillaMember.Fingerprint( member.Type )
					: IsFingerprintableOrdinaryDefinition( symbol )
					&& vanillaByKey.TryGetValue( symbol.Key, out WitcherScriptFunctionSymbol? vanilla )
					&& vanilla != null
					&& string.Equals(
						symbol.DefinitionFingerprint,
						vanilla.DefinitionFingerprint,
						StringComparison.Ordinal )
			} )
			.ToArray();
	}

	private static bool IsFingerprintableOrdinaryDefinition( WitcherScriptFunctionSymbol symbol )
	{
		return !string.IsNullOrWhiteSpace( symbol.DefinitionFingerprint )
			&& ( symbol.Kind == ScriptSymbolKind.ClassFunction
				|| symbol.Kind == ScriptSymbolKind.GlobalFunction );
	}

	// Ignore spacing and comments, but include modifiers and the complete body.
	// Include each token's length in the hash so different token boundaries remain distinguishable.
	private static string? CreateDefinitionFingerprint( IReadOnlyList<LexToken> tokens, int functionIndex )
	{
		int start = functionIndex;

		while ( start > 0 && tokens[start - 1].Text is not "{" and not "}" and not ";" )
		{
			start--;
		}

		int bodyStart = -1;

		for ( int index = functionIndex; index < tokens.Count; index++ )
		{
			if ( tokens[index].Text is "{" or ";" )
			{
				bodyStart = index;

				break;
			}
		}

		if ( bodyStart < 0 )
		{
			return null;
		}

		int end = bodyStart;

		if ( tokens[bodyStart].Text == "{" )
		{
			int depth = 1;

			while ( ++end < tokens.Count )
			{
				if ( tokens[end].Text == "{" )
					depth++;
				else if ( tokens[end].Text == "}" && --depth == 0 )
					break;
			}

			if ( end >= tokens.Count )
			{
				return null;
			}
		}

		using IncrementalHash hash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );

		for ( int index = start; index <= end; index++ )
		{
			byte[] tokenBytes = StrictUtf8.GetBytes( tokens[index].Text );

			hash.AppendData( BitConverter.GetBytes( tokenBytes.Length ) );
			hash.AppendData( tokenBytes );
		}

		return Convert.ToHexString( hash.GetHashAndReset() );
	}

	private static string Decode( byte[] data )
	{
		if ( data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf )
		{
			return StrictUtf8.GetString( data, 3, data.Length - 3 );
		}

		if ( data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe )
		{
			return StrictUtf16LittleEndian.GetString( data, 2, data.Length - 2 );
		}

		if ( data.Length >= 2 && data[0] == 0xfe && data[1] == 0xff )
		{
			return StrictUtf16BigEndian.GetString( data, 2, data.Length - 2 );
		}

		return StrictUtf8.GetString( data );
	}

	internal static bool HaveEquivalentCode( string firstPath, string secondPath )
	{
		try
		{
			return Tokenize( Decode( File.ReadAllBytes( firstPath ) ), forMerge: true )
				.SequenceEqual( Tokenize( Decode( File.ReadAllBytes( secondPath ) ), forMerge: true ) );
		}
		catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException )
		{
			return false;
		}
	}

	// Merge comparisons must reject malformed text; otherwise an incomplete script could appear unchanged.
	private static IReadOnlyList<LexToken> Tokenize( string text, bool forMerge = false )
	{
		if ( forMerge && text.Contains( '\0' ) )
			throw new FormatException();

		List<LexToken> tokens = new();

		for ( int index = 0; index < text.Length; )
		{
			char current = text[index];

			if ( char.IsWhiteSpace( current ) )
			{
				index++;

				continue;
			}

			if ( current == '/' && index + 1 < text.Length && text[index + 1] == '/' )
			{
				index += 2;

				while ( index < text.Length && text[index] != '\r' && text[index] != '\n' )
					index++;

				continue;
			}

			if ( current == '/' && index + 1 < text.Length && text[index + 1] == '*' )
			{
				index += 2;

				while ( index + 1 < text.Length && ( text[index] != '*' || text[index + 1] != '/' ) )
					index++;

				if ( forMerge && index + 1 >= text.Length )
					throw new FormatException();

				index = Math.Min( text.Length, index + 2 );

				continue;
			}

			if ( current is '"' or '\'' )
			{
				int start = index;
				char quote = current;
				bool closed = false;

				index++;

				while ( index < text.Length )
				{
					if ( text[index] == '\\' && index + 1 < text.Length )
					{
						index += 2;

						continue;
					}

					if ( text[index++] == quote )
					{
						closed = true;

						break;
					}
				}

				if ( forMerge && !closed )
					throw new FormatException();

				tokens.Add( new LexToken( text[start..index] ) );

				continue;
			}

			if ( IsIdentifierStart( current ) )
			{
				int start = index++;

				while ( index < text.Length && IsIdentifierPart( text[index] ) )
					index++;

				tokens.Add( new LexToken( text[start..index] ) );

				continue;
			}

			if ( char.IsDigit( current ) || current == '.' && index + 1 < text.Length && char.IsDigit( text[index + 1] ) )
			{
				int start = index++;
				bool hexadecimal = current == '0' && index < text.Length && ( text[index] is 'x' or 'X' );

				while ( index < text.Length && ( IsIdentifierPart( text[index] ) || text[index] == '.'
					|| ( !hexadecimal && ( text[index] is '+' or '-' ) && ( text[index - 1] is 'e' or 'E' ) ) ) )
					index++;

				tokens.Add( new LexToken( text[start..index] ) );

				continue;
			}

			if ( forMerge && MergeOperators.FirstOrDefault( symbol => text.AsSpan( index ).StartsWith( symbol, StringComparison.Ordinal ) ) is string compound )
			{
				tokens.Add( new LexToken( compound ) );
				index += compound.Length;

				continue;
			}

			tokens.Add( new LexToken( current.ToString() ) );
			index++;
		}

		return tokens;
	}

	private static bool IsFunctionAnnotation( string value )
	{
		return value.Equals( "wrapMethod", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "replaceMethod", StringComparison.OrdinalIgnoreCase )
			|| value.Equals( "addMethod", StringComparison.OrdinalIgnoreCase );
	}

	private static ScriptSymbolKind GetAnnotationKind( string annotation, string scope )
	{
		if ( annotation.Equals( "wrapMethod", StringComparison.OrdinalIgnoreCase ) )
		{
			return ScriptSymbolKind.WrappedFunction;
		}

		if ( annotation.Equals( "addMethod", StringComparison.OrdinalIgnoreCase ) )
		{
			return ScriptSymbolKind.AddedFunction;
		}

		return scope.Equals( "Global", StringComparison.OrdinalIgnoreCase )
			? ScriptSymbolKind.GlobalReplacement
			: ScriptSymbolKind.ClassReplacement;
	}

	private static bool IsIdentifier( string value )
	{
		return !string.IsNullOrEmpty( value ) && IsIdentifierStart( value[0] )
			&& value.Skip( 1 ).All( IsIdentifierPart );
	}

	private static bool IsIdentifierStart( char value ) => char.IsLetter( value ) || value == '_';

	private static bool IsIdentifierPart( char value ) => char.IsLetterOrDigit( value ) || value == '_';

	private sealed record LexToken( string Text );

	private sealed record ScopeFrame( string Name, int BraceDepth );

	private sealed record SymbolDefinition( ScriptSymbolKind Kind, string? Fingerprint, int Count );
}

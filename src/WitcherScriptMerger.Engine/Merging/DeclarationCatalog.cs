#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WSCompiler;
using WSCompiler.Core.Scripts.Standalone;
using Index = WSCompiler.Core.Scripts.Standalone.Index;


namespace WitcherScriptMerger.Merging;

// Tracks declarations and their dependencies for merge planning. Type inference here is incomplete,
// so a missing dependency does not prove independence. The compiler checks the finished output.
internal sealed class DeclarationCatalog
{
	private readonly Dictionary<string, FunctionDecl> functions = new( StringComparer.Ordinal );

	private readonly Dictionary<string, TypeRef> globals = new( StringComparer.Ordinal );

	private readonly Dictionary<string, TypeRef> fields = new( StringComparer.Ordinal );

	private readonly Dictionary<string, List<MergeModule>> providers = new( StringComparer.Ordinal );

	private readonly HashSet<string> enumMembers = new( StringComparer.Ordinal );

	private readonly Dictionary<string, string?> parents;

	private readonly Dictionary<string, string?> machines;

	private readonly Dictionary<Item, IReadOnlyCollection<string>> uses = new( ReferenceEqualityComparer.Instance );

	private readonly Dictionary<Item, IReadOnlyCollection<string>> signatures = new( ReferenceEqualityComparer.Instance );

	public DeclarationCatalog(
		IReadOnlyList<SourceFile> vanilla,
		IReadOnlyList<MergeModule> modules )
	{
		foreach ( var (name, type) in new[] { ("theGame", "CR4Game"), ("thePlayer", "CR4Player"), ("theCamera", "CCameraDirector"),
			("theSound", "CScriptSoundSystem"), ("theDebug", "CDebugAttributesManager"), ("theTimer", "CTimerScriptKeyword"),
			("theInput", "CInputManager"), ("theTelemetry", "CR4TelemetryScriptProxy"), ("theHud", "CR4Hud") } )
			globals[name] = new( type );

		foreach ( var item in vanilla.Concat( modules.SelectMany( module => module.Files ) ).SelectMany( file => file.Items ) )
			switch ( item )
			{
				case ClassItem value:
					string owner = Symbols.ClassKey( value.Decl );
					foreach ( var function in value.Decl.Methods )
						functions.TryAdd( owner + "." + function.Name, function );
					foreach ( var field in value.Decl.Fields )
						foreach ( string name in field.Names )
							fields.TryAdd( owner + "." + name, field.Type );
					break;
				case FunctionItem value when value.Decl.Annotation?.Kind is not ( AnnotationKind.WrapMethod or AnnotationKind.ReplaceMethod ):
					functions.TryAdd( FunctionKey( value.Decl ), value.Decl );
					break;
				case EnumItem value:
					enumMembers.UnionWith( value.Decl.Members.Select( member => member.Name ) );
					break;
				case FieldItem { Decl.Annotation: null } value:
					foreach ( string name in value.Decl.Names )
						globals[name] = value.Decl.Type;
					break;
				case FieldItem { Decl.Annotation.Class: { } target } value:
					foreach ( string name in value.Decl.Names )
						fields.TryAdd( target + "." + name, value.Decl.Type );
					break;
			}

		var rtti = Rtti.LoadBuiltIn();

		foreach ( var type in rtti.Classes )
		{
			foreach ( var field in type.Properties )
				fields.TryAdd( type.Name + "." + field.Name, BlobReferences.Type( field.Type ) );
		}

		var hierarchy = new Symbols();
		hierarchy.AddTypeNames( vanilla.Concat( modules.SelectMany( module => module.Files ) ) );
		hierarchy.AddRttiClasses( rtti );
		hierarchy.ResolveStateParents();
		parents = hierarchy.Classes.ToDictionary( value => value.Key, value => value.Value.Parent, StringComparer.Ordinal );
		machines = hierarchy.Classes.ToDictionary( value => value.Key, value => value.Value.StateMachine, StringComparer.Ordinal );

		foreach ( var module in modules )
			foreach ( string symbol in module.Files.SelectMany( file => file.Items ).SelectMany( Defines ).Distinct() )
			{
				if ( !providers.TryGetValue( symbol, out var values ) )
					providers[symbol] = values = [];

				values.Add( module );
			}
	}

	// Prefer loose type definitions over copies carried in compiled blobs.
	public IReadOnlyList<MergeModule> Providers(
		string symbol )
	{
		if ( !providers.TryGetValue( symbol, out var values ) )
			return [];

		if ( symbol.StartsWith( "type:", StringComparison.Ordinal ) && values.Any( module => !module.IsBlob ) )
			return values.Where( module => !module.IsBlob ).ToArray();

		return values;
	}

	public static IEnumerable<string> Declares(
		Item item )
	{
		if ( item is ClassItem type )
		{
			string owner = Symbols.ClassKey( type.Decl );
			yield return "type:" + owner;

			foreach ( var method in type.Decl.Methods )
				yield return "function:" + owner + "." + method.Name;

			foreach ( var field in type.Decl.Fields )
				foreach ( string name in field.Names )
					yield return "field:" + owner + "." + name;
		}
		else if ( item is FunctionItem function )
			yield return "function:" + FunctionKey( function.Decl );
		else
			foreach ( string symbol in Defines( item ) )
				yield return symbol;
	}

	// Imports and wrappers introduce no new declaration for dependency lookup.
	public static IEnumerable<string> Defines(
		Item item )
	{
		switch ( item )
		{
			case ClassItem value:
				string owner = Symbols.ClassKey( value.Decl );
				if ( !value.Decl.IsImport )
					yield return "type:" + owner;
				foreach ( var function in value.Decl.Methods.Where( function => !function.Specifiers.IsImport ) )
					yield return "function:" + owner + "." + function.Name;
				foreach ( var field in value.Decl.Fields.Where( field => !field.Specifiers.IsImport ) )
					foreach ( string name in field.Names )
						yield return "field:" + owner + "." + name;
				break;
			case EnumItem value:
				yield return "enum:" + value.Decl.Name;
				foreach ( var member in value.Decl.Members )
					yield return "member:" + member.Name;
				break;
			case FunctionItem value when !value.Decl.Specifiers.IsImport && value.Decl.Annotation?.Kind != AnnotationKind.WrapMethod:
				yield return "function:" + FunctionKey( value.Decl );
				break;
			case FieldItem value:
				foreach ( string name in value.Decl.Names )
					yield return "field:" + ( value.Decl.Annotation?.Class is { } target ? target + "." : "" ) + name;
				break;
		}
	}

	public bool WrapsNative( Item item ) => item is FunctionItem { Decl.Annotation.Kind: AnnotationKind.WrapMethod or AnnotationKind.ReplaceMethod } value
		&& FindFunction( value.Decl.Annotation.Class ?? "", value.Decl.Name )?.Specifiers.IsImport == true;

	// Reference declarations need only signature dependencies. Extended enums also depend on their existing definitions.
	public IEnumerable<string> Uses( Item item ) => Uses( item, signaturesOnly: false );

	public IEnumerable<string> PhaseDependencies( Item item ) => item is EnumItem value
		? Uses( item ).Append( "enum:" + value.Decl.Name ) : Uses( item );

	public IEnumerable<string> SignatureUses( Item item ) => Uses( item, signaturesOnly: true );

	private IEnumerable<string> Uses(
		Item item,
		bool signaturesOnly )
	{
		var cache = signaturesOnly ? signatures : uses;

		if ( !cache.TryGetValue( item, out var dependencies ) )
			cache[item] = dependencies = ReadUses( item, signaturesOnly );

		return dependencies;
	}

	private IReadOnlyCollection<string> ReadUses(
		Item item,
		bool signaturesOnly )
	{
		var result = new HashSet<string>( StringComparer.Ordinal );
		switch ( item )
		{
			case ClassItem value:
				string owner = Symbols.ClassKey( value.Decl );
				if ( value.Decl.Parent is { } parent )
					result.Add( "type:" + parent );
				if ( value.Decl.StateMachineOf is { } machine )
					result.Add( "type:" + machine );
				foreach ( var field in value.Decl.Fields )
					ReadField( field, owner );
				foreach ( var function in value.Decl.Methods )
					ReadFunction( function, owner );
				if ( !signaturesOnly )
					foreach ( var assignment in value.Decl.Defaults )
						ReadExpr( assignment.Value, owner, [] );
				break;
			case FunctionItem value:
				if ( value.Decl.Annotation?.Class is { Length: > 0 } target )
					result.Add( "type:" + target );
				if ( value.Decl.Annotation?.Kind is AnnotationKind.WrapMethod or AnnotationKind.ReplaceMethod )
					result.Add( "function:" + FunctionKey( value.Decl ) );
				ReadFunction( value.Decl, value.Decl.Annotation?.Class ?? "" );
				break;
			case FieldItem value:
				if ( value.Decl.Annotation?.Class is { Length: > 0 } targetClass )
					result.Add( "type:" + targetClass );
				ReadField( value.Decl, value.Decl.Annotation?.Class ?? "" );
				break;
		}

		return result;

		void ReadType(
			TypeRef? type )
		{
			if ( type is null )
				return;

			result.Add( "type:" + type.Name );
			result.Add( "enum:" + type.Name );

			foreach ( var argument in type.Args )
				ReadType( argument );
		}
		void ReadField(
			FieldDecl field,
			string owner )
		{
			ReadType( field.Type );

			if ( field.Initializer is not null )
				ReadExpr( field.Initializer, owner, [] );
		}
		void ReadFunction(
			FunctionDecl function,
			string owner )
		{
			ReadType( function.ReturnType );
			var variables = new Dictionary<string, TypeRef>( StringComparer.Ordinal );

			foreach ( var parameter in function.Params )
			{
				ReadType( parameter.Type );
				variables[parameter.Name] = parameter.Type;
			}

			if ( !signaturesOnly )
				foreach ( var statement in function.Body ?? [] )
					ReadStatement( statement, owner, variables );
		}
		void ReadStatement(
			Stmt statement,
			string owner,
			Dictionary<string, TypeRef> variables )
		{
			switch ( statement.Kind )
			{
				case VarStmt value:
					ReadField( value.Decl, owner );
					foreach ( string name in value.Decl.Names )
						variables[name] = value.Decl.Type;
					break;
				case ExprStmt value:
					ReadExpr( value.Expr, owner, variables );
					break;
				case IfStmt value:
					ReadExpr( value.Cond, owner, variables );
					ReadStatement( value.Then, owner, variables );
					if ( value.Else is not null )
						ReadStatement( value.Else, owner, variables );
					break;
				case WhileStmt value:
					ReadExpr( value.Cond, owner, variables );
					ReadStatement( value.Body, owner, variables );
					break;
				case DoWhileStmt value:
					ReadStatement( value.Body, owner, variables );
					ReadExpr( value.Cond, owner, variables );
					break;
				case ForStmt value:
					if ( value.Init is not null )
						ReadExpr( value.Init, owner, variables );
					if ( value.Cond is not null )
						ReadExpr( value.Cond, owner, variables );
					if ( value.Step is not null )
						ReadExpr( value.Step, owner, variables );
					ReadStatement( value.Body, owner, variables );
					break;
				case SwitchStmt value:
					ReadExpr( value.Value, owner, variables );
					foreach ( var child in value.Items )
						if ( child is CaseItem label )
							ReadExpr( label.Value, owner, variables );
						else if ( child is StmtItem body )
							ReadStatement( body.Stmt, owner, variables );
					break;
				case ReturnStmt { Value: { } value }:
					ReadExpr( value, owner, variables );
					break;
				case DeleteStmt value:
					ReadExpr( value.Value, owner, variables );
					break;
				case BlockStmt value:
					foreach ( var child in value.Statements )
						ReadStatement( child, owner, variables );
					break;
			}
		}
		TypeRef? TypeOf(
			Expr expression,
			string owner,
			Dictionary<string, TypeRef> variables,
			int depth = 0 )
		{
			if ( depth > 64 )
				throw new InvalidDataException( "Expression type nesting is too deep." );

			return expression.Kind switch
			{
				Ident value => variables.GetValueOrDefault( value.Name ) ?? globals.GetValueOrDefault( value.Name ) ?? FindField( owner, value.Name ),
				ThisExpr => new( owner ),
				ParentExpr or VirtualParentExpr => machines.GetValueOrDefault( owner ) is { } machine ? new( machine ) : null,
				SuperExpr => parents.GetValueOrDefault( owner ) is { } parent ? new( parent ) : null,
				Cast value => value.Type,
				New value => value.Type,
				Member value => TypeOf( value.Object, owner, variables, depth + 1 ) is { } receiver ? FindField( receiver.Name, value.Name ) : null,
				Call { Callee.Kind: Ident function } => FindFunction( owner, function.Name )?.ReturnType,
				Call { Callee.Kind: Member member } => TypeOf( member.Object, owner, variables, depth + 1 ) is { } receiver
					? FindFunction( receiver.Name, member.Name )?.ReturnType : null,
				Index value => TypeOf( value.Array, owner, variables, depth + 1 )?.Args.LastOrDefault(),
				Ternary value => TypeOf( value.Then, owner, variables, depth + 1 ),
				_ => null
			};
		}
		void ReadExpr(
			Expr expression,
			string owner,
			Dictionary<string, TypeRef> variables )
		{
			switch ( expression.Kind )
			{
				case Sequence value:
					foreach ( var child in value.Values )
						ReadExpr( child, owner, variables );
					break;
				case DefaultArray value:
					foreach ( var child in value.Values )
						ReadExpr( child, owner, variables );
					break;
				case Postfix value:
					ReadExpr( value.Operand, owner, variables );
					break;
				case Ident value when enumMembers.Contains( value.Name ):
					result.Add( "member:" + value.Name );
					break;
				case Ident value:
					if ( !variables.ContainsKey( value.Name ) )
					{
						if ( globals.ContainsKey( value.Name ) )
							result.Add( "field:" + value.Name );
						else if ( FindField( owner, value.Name ) is not null )
							result.Add( "field:" + FieldOwner( owner, value.Name ) + "." + value.Name );
					}
					break;
				case Cast value:
					ReadType( value.Type );
					ReadExpr( value.Operand, owner, variables );
					break;
				case New value:
					ReadType( value.Type );
					if ( value.Owner is not null )
						ReadExpr( value.Owner, owner, variables );
					break;
				case Call value:
					if ( value.Callee.Kind is Ident call )
					{
						string functionOwner = FindOwner( owner, call.Name );
						result.Add( "function:" + ( functionOwner.Length == 0 ? "" : functionOwner + "." ) + call.Name );
					}
					else if ( value.Callee.Kind is Member method && TypeOf( method.Object, owner, variables ) is { } receiver )
					{
						string functionOwner = FindOwner( receiver.Name, method.Name );
						result.Add( "function:" + ( functionOwner.Length == 0 ? receiver.Name : functionOwner ) + "." + method.Name );
					}
					ReadExpr( value.Callee, owner, variables );
					foreach ( var argument in value.Args.OfType<Expr>() )
						ReadExpr( argument, owner, variables );
					break;
				case Member value:
					if ( TypeOf( value.Object, owner, variables ) is { } fieldReceiver )
						result.Add( "field:" + FieldOwner( fieldReceiver.Name, value.Name ) + "." + value.Name );
					ReadExpr( value.Object, owner, variables );
					break;
				case Index value:
					ReadExpr( value.Array, owner, variables );
					ReadExpr( value.Idx, owner, variables );
					break;
				case Unary value:
					ReadExpr( value.Operand, owner, variables );
					break;
				case Binary value:
					ReadExpr( value.Left, owner, variables );
					ReadExpr( value.Right, owner, variables );
					break;
				case Assign value:
					ReadExpr( value.Target, owner, variables );
					ReadExpr( value.Value, owner, variables );
					break;
				case Ternary value:
					ReadExpr( value.Cond, owner, variables );
					ReadExpr( value.Then, owner, variables );
					ReadExpr( value.Else, owner, variables );
					break;
			}
		}
	}

	private static string FunctionKey( FunctionDecl value ) => ( value.Annotation?.Class is { Length: > 0 } owner ? owner + "." : "" ) + value.Name;

	public string ResolveMethod(
		string owner,
		string name )
	{
		string target = FindOwner( owner, name );

		return "function:" + ( target.Length == 0 ? owner : target ) + "." + name;
	}

	private FunctionDecl? FindFunction(
		string owner,
		string name )
	{
		string target = FindOwner( owner, name );

		return functions.GetValueOrDefault( ( target.Length == 0 ? "" : target + "." ) + name );
	}

	private string FindOwner(
		string owner,
		string name )
	{
		for ( int depth = 0; owner.Length > 0 && depth <= 64; depth++ )
		{
			if ( functions.ContainsKey( owner + "." + name ) )
				return owner;

			owner = parents.GetValueOrDefault( owner ) ?? "";
		}

		return "";
	}

	private TypeRef? FindField(
		string owner,
		string name )
		=> fields.GetValueOrDefault( FieldOwner( owner, name ) + "." + name );

	private string FieldOwner(
		string owner,
		string name )
	{
		for ( int depth = 0; owner.Length > 0 && depth <= 64; depth++ )
		{
			if ( fields.ContainsKey( owner + "." + name ) )
				return owner;

			owner = parents.GetValueOrDefault( owner ) ?? "";
		}

		return "";
	}
}

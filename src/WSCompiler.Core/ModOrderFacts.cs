namespace WSCompiler;

// Null means no numeric priority was assigned.
public readonly record struct ModOrderSource( string Name, bool IsModIo, int? Priority );

public enum ModOrderRelation
{
	FirstLoadsAhead,
	SecondLoadsAhead,
	SameMod,
	Unknown
}

public enum ModOrderUncertainty
{
	None,
	TiedPriorities,
	NonAsciiName,
	MissingModIoPriority,
	TiedModIoPriorities
}

public readonly record struct ModOrderFact(
	ModOrderRelation Relation,
	ModOrderUncertainty Uncertainty = ModOrderUncertainty.None );

// Shared ordering rules keep SMR and the Inspector consistent. Both verify the cases in tests/load-order-cases.txt.
public static class ModOrderFacts
{
	// Folder names break ties for file precedence.
	public static ModOrderFact FilePrecedence( ModOrderSource first, ModOrderSource second )
	{
		if ( first.IsModIo || second.IsModIo )
			return ModIoOrder( first, second );

		if ( first.Priority != second.Priority )
			return ByPriority( first, second );

		return ByName( first, second );
	}

	// Execution order for tied priorities and non-ASCII names has not been verified, so leave those cases unknown.
	public static ModOrderFact ExecutionOrder( ModOrderSource first, ModOrderSource second )
	{
		if ( first.IsModIo || second.IsModIo )
			return ModIoOrder( first, second );

		if ( first.Priority != second.Priority )
			return ByPriority( first, second );

		if ( first.Priority.HasValue )
			return new( ModOrderRelation.Unknown, ModOrderUncertainty.TiedPriorities );

		if ( !IsAscii( first.Name ) || !IsAscii( second.Name ) )
			return new( ModOrderRelation.Unknown, ModOrderUncertainty.NonAsciiName );

		return ByName( first, second );
	}

	// Mod.io mods take precedence over conventional mods. Ordering between Mod.io mods requires distinct priorities.
	private static ModOrderFact ModIoOrder( ModOrderSource first, ModOrderSource second )
	{
		if ( first.IsModIo != second.IsModIo )
			return Ahead( first.IsModIo );

		if ( !first.Priority.HasValue || !second.Priority.HasValue )
			return new( ModOrderRelation.Unknown, ModOrderUncertainty.MissingModIoPriority );

		if ( first.Priority == second.Priority )
			return new( ModOrderRelation.Unknown, ModOrderUncertainty.TiedModIoPriorities );

		return Ahead( first.Priority < second.Priority );
	}

	// An assigned number outranks no number at all, and a smaller number outranks a larger one.
	private static ModOrderFact ByPriority( ModOrderSource first, ModOrderSource second )
	{
		if ( first.Priority.HasValue != second.Priority.HasValue )
			return Ahead( first.Priority.HasValue );

		return Ahead( first.Priority < second.Priority );
	}

	// Ignore case when identifying and ordering mod names.
	private static ModOrderFact ByName( ModOrderSource first, ModOrderSource second )
	{
		int comparison = string.CompareOrdinal( first.Name.ToLowerInvariant(), second.Name.ToLowerInvariant() );

		return comparison == 0 ? new( ModOrderRelation.SameMod ) : Ahead( comparison < 0 );
	}

	private static ModOrderFact Ahead( bool firstIsAhead ) =>
		new( firstIsAhead ? ModOrderRelation.FirstLoadsAhead : ModOrderRelation.SecondLoadsAhead );

	private static bool IsAscii( string name ) => name.All( character => character <= 127 );
}

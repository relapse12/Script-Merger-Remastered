#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WitcherScriptMerger.Tools;

namespace WitcherScriptMerger.XmlCoverage;

// Checks whether merged XML preserves each source's changes to vanilla.
// Request review when the comparison cannot establish the result.
public sealed class XmlCoverageAnalyzer
{
	private const long DefaultMaxCharactersInDocument = 256L * 1024L * 1024L;
	private const int DefaultMaxElementDepth = 256;
	private const char OccurrenceSeparator = '\u001f';
	private static readonly string[] StableIdentityAttributes = { "name", "id", "key" };

	private readonly long _maxCharactersInDocument;
	private readonly int _maxElementDepth;

	public XmlCoverageAnalyzer(
		long maxCharactersInDocument = DefaultMaxCharactersInDocument,
		int maxElementDepth = DefaultMaxElementDepth )
	{
		if ( maxCharactersInDocument <= 0 )
		{
			throw new ArgumentOutOfRangeException( nameof( maxCharactersInDocument ) );
		}

		if ( maxElementDepth <= 0 )
		{
			throw new ArgumentOutOfRangeException( nameof( maxElementDepth ) );
		}

		_maxCharactersInDocument = maxCharactersInDocument;
		_maxElementDepth = maxElementDepth;
	}

	public XmlCoverageResult AnalyzeFiles(
		string vanillaPath,
		string candidateWinnerPath,
		IEnumerable<string> sourcePaths )
	{
		string normalizedVanillaPath = NormalizeRequiredPath( vanillaPath, nameof( vanillaPath ) );
		string normalizedCandidatePath = NormalizeRequiredPath( candidateWinnerPath, nameof( candidateWinnerPath ) );
		string[] normalizedSourcePaths = ( sourcePaths ?? throw new ArgumentNullException( nameof( sourcePaths ) ) )
			.Select( ( path, index ) => NormalizeRequiredPath( path, $"sourcePaths[{index}]" ) )
			.ToArray();

		if ( normalizedSourcePaths.Length == 0 )
		{
			throw new ArgumentException(
				UiText.T( "XmlCoverageAnalyzer", "At least one source XML file is required." ),
				nameof( sourcePaths ) );
		}

		ParsedXml? vanilla = TryLoad( normalizedVanillaPath, out string? vanillaError );
		ParsedXml? winner = TryLoad( normalizedCandidatePath, out string? winnerError );
		List<XmlCoverageSourceResult> sourceResults = new List<XmlCoverageSourceResult>();

		foreach ( string sourcePath in normalizedSourcePaths )
		{
			List<XmlCoverageFinding> findings = new List<XmlCoverageFinding>();

			if ( vanilla == null )
			{
				findings.Add( Review( sourcePath, "/", UiText.Format( "XmlCoverageAnalyzer", "Vanilla XML could not be analyzed securely: {0}", vanillaError ) ) );
			}

			if ( winner == null )
			{
				findings.Add( Review( sourcePath, "/", UiText.Format( "XmlCoverageAnalyzer", "Candidate winner XML could not be analyzed securely: {0}", winnerError ) ) );
			}

			ParsedXml? source = TryLoad( sourcePath, out string? sourceError );

			if ( source == null )
			{
				findings.Add( Review( sourcePath, "/", UiText.Format( "XmlCoverageAnalyzer", "Source XML could not be analyzed securely: {0}", sourceError ) ) );
			}

			if ( vanilla != null && winner != null && source != null )
			{
				AnalyzeDocument( vanilla, winner, source, sourcePath, findings );
			}

			if ( findings.Count == 0 )
			{
				findings.Add( Covered( sourcePath, "/", UiText.T( "XmlCoverageAnalyzer", "The source has no semantic changes relative to vanilla." ) ) );
			}

			sourceResults.Add( new XmlCoverageSourceResult( sourcePath, findings ) );
		}

		return new XmlCoverageResult( normalizedVanillaPath, normalizedCandidatePath, sourceResults );
	}

	private void AnalyzeDocument(
		ParsedXml vanilla,
		ParsedXml winner,
		ParsedXml source,
		string sourcePath,
		List<XmlCoverageFinding> findings )
	{
		if ( vanilla.UnsupportedConstruct != null )
		{
			findings.Add( Review( sourcePath, "/", UiText.Format( "XmlCoverageAnalyzer", "Vanilla XML uses an unsupported construct: {0}", vanilla.UnsupportedConstruct ) ) );

			return;
		}

		if ( winner.UnsupportedConstruct != null )
		{
			findings.Add( Review( sourcePath, "/", UiText.Format( "XmlCoverageAnalyzer", "Candidate winner XML uses an unsupported construct: {0}", winner.UnsupportedConstruct ) ) );

			return;
		}

		if ( source.UnsupportedConstruct != null )
		{
			findings.Add( Review( sourcePath, "/", UiText.Format( "XmlCoverageAnalyzer", "Source XML uses an unsupported construct: {0}", source.UnsupportedConstruct ) ) );

			return;
		}

		XElement vanillaRoot = vanilla.Document.Root!;
		XElement sourceRoot = source.Document.Root!;
		XElement winnerRoot = winner.Document.Root!;
		string rootPath = "/" + DisplayName( sourceRoot.Name );

		if ( sourceRoot.Name != vanillaRoot.Name )
		{
			findings.Add( Review( sourcePath, rootPath, UiText.T( "XmlCoverageAnalyzer", "The source replaces the document root; root topology changes require review." ) ) );

			return;
		}

		if ( winnerRoot.Name != sourceRoot.Name )
		{
			findings.Add( NotCovered( sourcePath, rootPath, UiText.T( "XmlCoverageAnalyzer", "The candidate winner has a different document root." ) ) );

			return;
		}

		AnalyzeElement( vanillaRoot, sourceRoot, winnerRoot, rootPath, sourcePath, findings );
	}

	// Mixed text and child elements need review because this comparison cannot establish their combined meaning.
	private void AnalyzeElement(
		XElement vanilla,
		XElement source,
		XElement winner,
		string path,
		string sourcePath,
		List<XmlCoverageFinding> findings )
	{
		if ( SemanticallyEqual( vanilla, source ) )
		{
			return;
		}

		if ( HasMixedContent( vanilla ) || HasMixedContent( source ) )
		{
			findings.Add( Review( sourcePath, path, UiText.T( "XmlCoverageAnalyzer", "Changed mixed text-and-element content cannot be matched safely." ) ) );

			return;
		}

		AnalyzeAttributes( vanilla, source, winner, path, sourcePath, findings );
		AnalyzeDirectText( vanilla, source, winner, path, sourcePath, findings );
		AnalyzeChildren( vanilla, source, winner, path, sourcePath, findings );
	}

	private static void AnalyzeAttributes(
		XElement vanilla,
		XElement source,
		XElement winner,
		string path,
		string sourcePath,
		List<XmlCoverageFinding> findings )
	{
		Dictionary<XName, string> vanillaAttributes = GetAttributes( vanilla );
		Dictionary<XName, string> sourceAttributes = GetAttributes( source );
		Dictionary<XName, string> winnerAttributes = GetAttributes( winner );

		foreach ( XName attributeName in vanillaAttributes.Keys.Union( sourceAttributes.Keys ).OrderBy( ExpandedName, StringComparer.Ordinal ) )
		{
			vanillaAttributes.TryGetValue( attributeName, out string? vanillaValue );
			sourceAttributes.TryGetValue( attributeName, out string? sourceValue );

			bool vanillaHas = vanillaAttributes.ContainsKey( attributeName );
			bool sourceHas = sourceAttributes.ContainsKey( attributeName );

			if ( vanillaHas == sourceHas && string.Equals( vanillaValue, sourceValue, StringComparison.Ordinal ) )
			{
				continue;
			}

			bool winnerHas = winnerAttributes.TryGetValue( attributeName, out string? winnerValue );
			string attributePath = path + "/@" + DisplayName( attributeName );

			if ( winnerHas == sourceHas && ( !sourceHas || string.Equals( winnerValue, sourceValue, StringComparison.Ordinal ) ) )
			{
				findings.Add( Covered( sourcePath, attributePath, sourceHas
					? UiText.T( "XmlCoverageAnalyzer", "The candidate winner contains the source attribute value." )
					: UiText.T( "XmlCoverageAnalyzer", "The candidate winner preserves the source attribute deletion." ) ) );
			}
			else
			{
				findings.Add( NotCovered( sourcePath, attributePath, sourceHas
					? UiText.T( "XmlCoverageAnalyzer", "The candidate winner does not contain the source attribute value." )
					: UiText.T( "XmlCoverageAnalyzer", "The candidate winner still contains an attribute deleted by the source." ) ) );
			}
		}
	}

	private static void AnalyzeDirectText(
		XElement vanilla,
		XElement source,
		XElement winner,
		string path,
		string sourcePath,
		List<XmlCoverageFinding> findings )
	{
		string vanillaText = GetDirectText( vanilla );
		string sourceText = GetDirectText( source );

		if ( string.Equals( vanillaText, sourceText, StringComparison.Ordinal ) )
		{
			return;
		}

		if ( string.Equals( GetDirectText( winner ), sourceText, StringComparison.Ordinal ) )
		{
			findings.Add( Covered( sourcePath, path + "/text()", UiText.T( "XmlCoverageAnalyzer", "The candidate winner contains the source text value." ) ) );
		}
		else
		{
			findings.Add( NotCovered( sourcePath, path + "/text()", UiText.T( "XmlCoverageAnalyzer", "The candidate winner does not contain the source text value." ) ) );
		}
	}

	private void AnalyzeChildren(
		XElement vanilla,
		XElement source,
		XElement winner,
		string path,
		string sourcePath,
		List<XmlCoverageFinding> findings )
	{
		List<XElement> vanillaChildren = vanilla.Elements().ToList();
		List<XElement> sourceChildren = source.Elements().ToList();
		List<XElement> winnerChildren = winner.Elements().ToList();

		if ( SemanticSequenceEqual( vanillaChildren, sourceChildren ) )
		{
			return;
		}

		XName[] names = vanillaChildren.Select( item => item.Name )
			.Union( sourceChildren.Select( item => item.Name ) )
			.OrderBy( ExpandedName, StringComparer.Ordinal )
			.ToArray();
		Dictionary<XName, IdentityPlan> plans = new Dictionary<XName, IdentityPlan>();

		foreach ( XName name in names )
		{
			List<XElement> vanillaGroup = vanillaChildren.Where( item => item.Name == name ).ToList();
			List<XElement> sourceGroup = sourceChildren.Where( item => item.Name == name ).ToList();
			List<XElement> winnerGroup = winnerChildren.Where( item => item.Name == name ).ToList();
			IdentityPlan plan = SelectIdentityPlan( vanillaGroup, sourceGroup, winnerGroup );

			plans.Add( name, plan );
			AnalyzeChildGroup( vanillaGroup, sourceGroup, winnerGroup, plan, path, sourcePath, findings );
		}

		AnalyzeTopology( vanillaChildren, sourceChildren, winnerChildren, plans, path, sourcePath, findings );
	}

	private void AnalyzeChildGroup(
		IReadOnlyList<XElement> vanilla,
		IReadOnlyList<XElement> source,
		IReadOnlyList<XElement> winner,
		IdentityPlan plan,
		string parentPath,
		string sourcePath,
		List<XmlCoverageFinding> findings )
	{
		string elementName;
		string groupPath;
		bool winnerExactlyMatchesSource;

		if ( SemanticSequenceEqual( vanilla, source ) )
			return;

		elementName = DisplayName( ( source.FirstOrDefault() ?? vanilla.First() ).Name );
		groupPath = parentPath + "/" + elementName;
		winnerExactlyMatchesSource = SemanticSequenceEqual( source, winner );

		if ( plan.Kind == IdentityKind.Ambiguous )
		{
			if ( winnerExactlyMatchesSource && AllSemanticallyEqual( source ) )
				findings.Add( Covered( sourcePath, groupPath, UiText.T( "XmlCoverageAnalyzer", "The candidate winner contains the source's exact group of indistinguishable repeated children." ) ) );
			else
				findings.Add( Review( sourcePath, groupPath, plan.Reason ) );

			return;
		}

		if ( winnerExactlyMatchesSource )
			findings.Add( Covered( sourcePath, groupPath, UiText.T( "XmlCoverageAnalyzer", "The candidate winner contains the source's exact changed child group." ) ) );
		else if ( plan.Kind == IdentityKind.Singleton )
			AnalyzeSingleton( vanilla, source, winner, groupPath, sourcePath, findings );
		else
			AnalyzeKeyedGroup( vanilla, source, winner, plan, parentPath, elementName, sourcePath, findings );
	}

	private void AnalyzeSingleton(
		IReadOnlyList<XElement> vanilla,
		IReadOnlyList<XElement> source,
		IReadOnlyList<XElement> winner,
		string groupPath,
		string sourcePath,
		List<XmlCoverageFinding> findings )
	{
		XElement? vanillaElement = vanilla.SingleOrDefault();
		XElement? sourceElement = source.SingleOrDefault();
		XElement? winnerElement = winner.SingleOrDefault();

		if ( vanillaElement == null && sourceElement != null )
		{
			findings.Add( Review( sourcePath, groupPath, UiText.T( "XmlCoverageAnalyzer", "The source inserts an unkeyed element; positional identity is not safe." ) ) );
		}
		else if ( vanillaElement != null && sourceElement == null )
		{
			findings.Add( winnerElement == null
				? Covered(
					sourcePath,
					groupPath,
					UiText.T( "XmlCoverageAnalyzer", "The candidate winner preserves the source element deletion." ) )
				: NotCovered(
					sourcePath,
					groupPath,
					UiText.T( "XmlCoverageAnalyzer", "The candidate winner still contains an element deleted by the source." ) ) );
		}
		else if ( vanillaElement != null && sourceElement != null )
		{
			if ( winnerElement == null )
				findings.Add( NotCovered( sourcePath, groupPath, UiText.T( "XmlCoverageAnalyzer", "The candidate winner is missing an element changed by the source." ) ) );
			else
				AnalyzeElement( vanillaElement, sourceElement, winnerElement, groupPath, sourcePath, findings );
		}
	}

	private void AnalyzeKeyedGroup(
		IReadOnlyList<XElement> vanilla,
		IReadOnlyList<XElement> source,
		IReadOnlyList<XElement> winner,
		IdentityPlan plan,
		string parentPath,
		string elementName,
		string sourcePath,
		List<XmlCoverageFinding> findings )
	{
		Dictionary<string, XElement> vanillaByKey = IndexByKey( vanilla, plan );
		Dictionary<string, XElement> sourceByKey = IndexByKey( source, plan );
		Dictionary<string, XElement> winnerByKey = IndexByKey( winner, plan );

		foreach ( string key in vanillaByKey.Keys.Except( sourceByKey.Keys ).OrderBy( item => item, StringComparer.Ordinal ) )
		{
			string keyedPath = KeyedPath( parentPath, elementName, plan, key );

			findings.Add( !winnerByKey.ContainsKey( key )
				? Covered(
					sourcePath,
					keyedPath,
					UiText.T( "XmlCoverageAnalyzer", "The candidate winner preserves the source keyed-subtree deletion." ) )
				: NotCovered(
					sourcePath,
					keyedPath,
					UiText.T( "XmlCoverageAnalyzer", "The candidate winner still contains a keyed subtree deleted by the source." ) ) );
		}

		foreach ( string key in sourceByKey.Keys.Except( vanillaByKey.Keys ).OrderBy( item => item, StringComparer.Ordinal ) )
		{
			string keyedPath = KeyedPath( parentPath, elementName, plan, key );

			if ( !winnerByKey.TryGetValue( key, out XElement? winnerElement ) )
				findings.Add( NotCovered( sourcePath, keyedPath, UiText.T( "XmlCoverageAnalyzer", "The candidate winner is missing a keyed subtree added by the source." ) ) );
			else if ( SemanticallyEqual( sourceByKey[key], winnerElement ) )
				findings.Add( Covered( sourcePath, keyedPath, UiText.T( "XmlCoverageAnalyzer", "The candidate winner contains the exact keyed subtree added by the source." ) ) );
			else
				findings.Add( NotCovered( sourcePath, keyedPath, UiText.T( "XmlCoverageAnalyzer", "A keyed subtree added by the source exists in the winner but is not an exact semantic match." ) ) );
		}

		foreach ( string key in vanillaByKey.Keys.Intersect( sourceByKey.Keys ).OrderBy( item => item, StringComparer.Ordinal ) )
		{
			XElement vanillaElement = vanillaByKey[key];
			XElement sourceElement = sourceByKey[key];
			string keyedPath;

			if ( SemanticallyEqual( vanillaElement, sourceElement ) )
				continue;

			keyedPath = KeyedPath( parentPath, elementName, plan, key );

			if ( !winnerByKey.TryGetValue( key, out XElement? winnerElement ) )
				findings.Add( NotCovered( sourcePath, keyedPath, UiText.T( "XmlCoverageAnalyzer", "The candidate winner is missing a keyed subtree changed by the source." ) ) );
			else
				AnalyzeElement( vanillaElement, sourceElement, winnerElement, keyedPath, sourcePath, findings );
		}
	}

	// Child order can matter; preserve ordering changes made by the source.
	private static void AnalyzeTopology(
		IReadOnlyList<XElement> vanilla,
		IReadOnlyList<XElement> source,
		IReadOnlyList<XElement> winner,
		IReadOnlyDictionary<XName, IdentityPlan> plans,
		string path,
		string sourcePath,
		List<XmlCoverageFinding> findings )
	{
		List<string>? vanillaTokens = TryGetTokens( vanilla, plans );
		List<string>? sourceTokens = TryGetTokens( source, plans );
		List<string>? winnerTokens = TryGetTokens( winner, plans, ignoreUnknownNames: true );

		if ( vanillaTokens == null || sourceTokens == null )
		{
			if ( HaveSameNameCounts( vanilla, source )
				&& !vanilla.Select( item => item.Name ).SequenceEqual( source.Select( item => item.Name ) ) )
			{
				findings.Add( Review( sourcePath, path, UiText.T( "XmlCoverageAnalyzer", "The source reorders children whose stable identity cannot be proven." ) ) );
			}

			return;
		}

		HashSet<string> common = vanillaTokens.Intersect( sourceTokens, StringComparer.Ordinal ).ToHashSet( StringComparer.Ordinal );

		if ( !vanillaTokens.Where( common.Contains ).SequenceEqual( sourceTokens.Where( common.Contains ), StringComparer.Ordinal ) )
		{
			findings.Add( Review( sourcePath, path, UiText.T( "XmlCoverageAnalyzer", "The source reorders existing children; order semantics require review." ) ) );

			return;
		}

		bool hasAddition = sourceTokens.Except( vanillaTokens, StringComparer.Ordinal ).Any();

		if ( !hasAddition || winnerTokens == null )
		{
			return;
		}

		HashSet<string> sourceTokenSet = sourceTokens.ToHashSet( StringComparer.Ordinal );
		List<string> winnerProjection = winnerTokens.Where( sourceTokenSet.Contains ).ToList();

		if ( winnerProjection.Count == sourceTokens.Count
			&& !winnerProjection.SequenceEqual( sourceTokens, StringComparer.Ordinal ) )
		{
			findings.Add( Review( sourcePath, path, UiText.T( "XmlCoverageAnalyzer", "The winner places a source-added keyed subtree in a different relative order." ) ) );
		}
	}

	// Match repeated element names by a stable identity. Ambiguous matches require review.
	private static IdentityPlan SelectIdentityPlan(
		IReadOnlyList<XElement> vanilla,
		IReadOnlyList<XElement> source,
		IReadOnlyList<XElement> winner )
	{
		IReadOnlyList<XElement>[] documents = { vanilla, source, winner };

		foreach ( string candidate in StableIdentityAttributes )
		{
			bool valid = true;
			bool hasDuplicates = false;

			foreach ( IReadOnlyList<XElement> elements in documents )
			{
				List<string> values = new List<string>();

				foreach ( XElement element in elements )
				{
					if ( !TryGetIdentityAttribute( element, candidate, out string? value ) )
					{
						valid = false;

						break;
					}

					values.Add( value! );
				}

				if ( !valid )
				{
					break;
				}

				hasDuplicates |= values.Count != values.Distinct( StringComparer.Ordinal ).Count();
			}

			if ( valid && documents.Sum( items => items.Count ) > 0 )
			{
				if ( !hasDuplicates )
				{
					return IdentityPlan.Keyed( candidate, usesOccurrenceOrdinal: false );
				}

				if ( CanUseOccurrenceOrdinal( documents, candidate ) )
				{
					return IdentityPlan.Keyed( candidate, usesOccurrenceOrdinal: true );
				}
			}
		}

		if ( documents.All( items => items.Count <= 1 ) )
		{
			return IdentityPlan.Singleton();
		}

		return IdentityPlan.Ambiguous(
			"Sibling elements do not have a unique stable name, id, or key attribute; positional matching is unsafe." );
	}

	private ParsedXml? TryLoad( string path, out string? error )
	{
		try
		{
			XmlReaderSettings settings = new XmlReaderSettings
			{
				CheckCharacters = true,
				CloseInput = false,
				DtdProcessing = DtdProcessing.Prohibit,
				IgnoreComments = false,
				IgnoreProcessingInstructions = false,
				IgnoreWhitespace = false,
				MaxCharactersFromEntities = 1024,
				MaxCharactersInDocument = _maxCharactersInDocument,
				XmlResolver = null
			};
			using FileStream stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read );
			XDocument document = GameXmlReader.Load( stream, settings, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo );

			if ( document.Root == null )
			{
				throw new XmlException( UiText.T( "XmlCoverageAnalyzer", "The document has no root element." ) );
			}

			int maxDepth = MeasureDepth( document.Root );

			if ( maxDepth > _maxElementDepth )
			{
				throw new XmlException( UiText.F( "XmlCoverageAnalyzer", $"Element depth {maxDepth} exceeds the configured limit {_maxElementDepth}." ) );
			}

			string? unsupported = document.DescendantNodes()
				.OfType<XProcessingInstruction>()
				.Select( item => "processing instruction '" + item.Target + "'" )
				.FirstOrDefault();

			error = null;

			return new ParsedXml( document, unsupported );
		}
		catch ( Exception exception ) when ( exception is IOException
			or UnauthorizedAccessException
			or XmlException
			or DecoderFallbackException
			or InvalidOperationException )
		{
			error = exception.Message;

			return null;
		}
	}

	// Use an explicit stack so deeply nested XML cannot exhaust the call stack.
	private static int MeasureDepth( XElement root )
	{
		int maximum = 0;
		Stack<(XElement Element, int Depth)> pending = new Stack<(XElement, int)>();

		pending.Push( (root, 1) );

		while ( pending.Count > 0 )
		{
			(XElement element, int depth) = pending.Pop();
			maximum = Math.Max( maximum, depth );

			foreach ( XElement child in element.Elements() )
			{
				pending.Push( (child, depth + 1) );
			}
		}

		return maximum;
	}

	private static bool TryGetIdentityAttribute( XElement element, string localName, out string? value )
	{
		XAttribute[] matches = element.Attributes()
			.Where( item => !item.IsNamespaceDeclaration
				&& item.Name.NamespaceName.Length == 0
				&& string.Equals( item.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase ) )
			.ToArray();

		if ( matches.Length == 1 && !string.IsNullOrEmpty( matches[0].Value ) )
		{
			value = matches[0].Value;

			return true;
		}

		value = null;

		return false;
	}

	private static Dictionary<string, XElement> IndexByKey( IEnumerable<XElement> elements, IdentityPlan plan )
	{
		Dictionary<string, XElement> result = new( StringComparer.Ordinal );
		Dictionary<string, int> occurrences = new( StringComparer.Ordinal );

		foreach ( XElement element in elements )
		{
			string value = GetIdentityValue( element, plan.AttributeName! );
			string key = value;

			if ( plan.UsesOccurrenceOrdinal )
			{
				occurrences.TryGetValue( value, out int occurrence );
				occurrence++;
				occurrences[value] = occurrence;
				key = value + OccurrenceSeparator + occurrence.ToString( System.Globalization.CultureInfo.InvariantCulture );
			}

			result.Add( key, element );
		}

		return result;
	}

	// Use occurrence order to distinguish repeated keys only when their counts agree across documents.
	private static bool CanUseOccurrenceOrdinal(
		IReadOnlyList<XElement>[] documents,
		string attributeName )
	{
		HashSet<string> duplicatedValues = documents
			.SelectMany( document => document
				.GroupBy( element => GetIdentityValue( element, attributeName ), StringComparer.Ordinal )
				.Where( group => group.Count() > 1 )
				.Select( group => group.Key ) )
			.ToHashSet( StringComparer.Ordinal );

		foreach ( string value in duplicatedValues )
		{
			int? expectedCount = null;

			foreach ( IReadOnlyList<XElement> document in documents )
			{
				XElement[] group = document
					.Where( element => GetIdentityValue( element, attributeName ).Equals( value, StringComparison.Ordinal ) )
					.ToArray();

				expectedCount ??= group.Length;

				if ( group.Length != expectedCount.Value
					|| group.Length < 2
					|| group.Skip( 1 ).Any( element => !SemanticallyEqual( group[0], element ) ) )
				{
					return false;
				}
			}
		}

		return duplicatedValues.Count > 0;
	}

	private static string GetIdentityValue( XElement element, string attributeName )
	{
		if ( !TryGetIdentityAttribute( element, attributeName, out string? value ) )
		{
			throw new InvalidOperationException( UiText.T( "XmlCoverageAnalyzer", "A previously validated XML identity attribute is missing." ) );
		}

		return value!;
	}

	private static List<string>? TryGetTokens(
		IReadOnlyList<XElement> elements,
		IReadOnlyDictionary<XName, IdentityPlan> plans,
		bool ignoreUnknownNames = false )
	{
		List<string> tokens = new List<string>( elements.Count );
		Dictionary<string, int> occurrences = new( StringComparer.Ordinal );

		foreach ( XElement element in elements )
		{
			if ( !plans.TryGetValue( element.Name, out IdentityPlan? plan ) )
			{
				if ( ignoreUnknownNames )
				{
					continue;
				}

				return null;
			}

			if ( plan.Kind == IdentityKind.Ambiguous )
			{
				return null;
			}

			string name = ExpandedName( element.Name );

			if ( plan.Kind == IdentityKind.Keyed )
			{
				string identity = GetIdentityValue( element, plan.AttributeName! );
				string token = "K|" + name + "|" + plan.AttributeName + "|" + identity;

				if ( plan.UsesOccurrenceOrdinal )
				{
					string occurrenceKey = name + "|" + plan.AttributeName + "|" + identity;

					occurrences.TryGetValue( occurrenceKey, out int occurrence );
					occurrence++;
					occurrences[occurrenceKey] = occurrence;
					token += "|#" + occurrence.ToString( System.Globalization.CultureInfo.InvariantCulture );
				}

				tokens.Add( token );
			}
			else
			{
				tokens.Add( "S|" + name );
			}
		}

		return tokens;
	}

	private static bool HaveSameNameCounts( IReadOnlyList<XElement> left, IReadOnlyList<XElement> right )
	{
		return left.GroupBy( item => item.Name ).ToDictionary( group => group.Key, group => group.Count() )
			.OrderBy( item => ExpandedName( item.Key ), StringComparer.Ordinal )
			.SequenceEqual(
				right.GroupBy( item => item.Name ).ToDictionary( group => group.Key, group => group.Count() )
					.OrderBy( item => ExpandedName( item.Key ), StringComparer.Ordinal ) );
	}

	private static bool SemanticallyEqual( XElement left, XElement right )
	{
		return string.Equals(
			Canonicalize( left, GetInheritedWhitespacePreservation( left ) ),
			Canonicalize( right, GetInheritedWhitespacePreservation( right ) ),
			StringComparison.Ordinal );
	}

	private static bool SemanticSequenceEqual( IEnumerable<XElement> left, IEnumerable<XElement> right )
	{
		return left.Select( item => Canonicalize( item, GetInheritedWhitespacePreservation( item ) ) )
			.SequenceEqual(
				right.Select( item => Canonicalize( item, GetInheritedWhitespacePreservation( item ) ) ),
				StringComparer.Ordinal );
	}

	private static bool AllSemanticallyEqual( IReadOnlyList<XElement> elements )
	{
		return elements.Count > 0 && elements.Skip( 1 ).All( element => SemanticallyEqual( elements[0], element ) );
	}

	// Include text lengths so adjoining values cannot produce an ambiguous comparison string.
	private static string Canonicalize( XElement element, bool preserveWhitespace )
	{
		bool preserveHere = ResolveWhitespacePreservation( element, preserveWhitespace );
		StringBuilder builder = new StringBuilder();

		builder.Append( 'E' );
		AppendLengthPrefixed( builder, ExpandedName( element.Name ) );

		foreach ( XAttribute attribute in element.Attributes()
			.Where( item => !item.IsNamespaceDeclaration )
			.OrderBy( item => ExpandedName( item.Name ), StringComparer.Ordinal ) )
		{
			builder.Append( 'A' );
			AppendLengthPrefixed( builder, ExpandedName( attribute.Name ) );
			AppendLengthPrefixed( builder, attribute.Value );
		}

		builder.Append( 'C' );

		StringBuilder text = new StringBuilder();

		foreach ( XNode node in element.Nodes() )
		{
			switch ( node )
			{
				case XElement child:
					AppendCanonicalText( builder, text, preserveHere );
					builder.Append( Canonicalize( child, preserveHere ) );
					break;

				case XText textNode:
					text.Append( textNode.Value );
					break;
			}
		}

		AppendCanonicalText( builder, text, preserveHere );

		return builder.Append( 'Z' ).ToString();
	}

	private static void AppendCanonicalText( StringBuilder target, StringBuilder text, bool preserveWhitespace )
	{
		if ( text.Length > 0 && ( preserveWhitespace || !string.IsNullOrWhiteSpace( text.ToString() ) ) )
		{
			target.Append( 'T' );
			AppendLengthPrefixed( target, text.ToString() );
		}

		text.Clear();
	}

	private static void AppendLengthPrefixed( StringBuilder target, string value )
	{
		target.Append( value.Length ).Append( ':' ).Append( value );
	}

	private static bool ResolveWhitespacePreservation( XElement element, bool inherited )
	{
		XAttribute? space = element.Attribute( XNamespace.Xml + "space" );

		if ( space == null )
		{
			return inherited;
		}

		return string.Equals( space.Value, "preserve", StringComparison.Ordinal );
	}

	private static string GetDirectText( XElement element )
	{
		bool preserve = ResolveWhitespacePreservation( element, GetInheritedWhitespacePreservation( element ) );

		return string.Concat( element.Nodes()
			.OfType<XText>()
			.Where( item => preserve || !string.IsNullOrWhiteSpace( item.Value ) )
			.Select( item => item.Value ) );
	}

	private static bool GetInheritedWhitespacePreservation( XElement element )
	{
		foreach ( XElement ancestor in element.Ancestors() )
		{
			XAttribute? space = ancestor.Attribute( XNamespace.Xml + "space" );

			if ( space != null )
			{
				return string.Equals( space.Value, "preserve", StringComparison.Ordinal );
			}
		}

		return false;
	}

	private static bool HasMixedContent( XElement element )
	{
		return element.Elements().Any() && GetDirectText( element ).Length > 0;
	}

	private static Dictionary<XName, string> GetAttributes( XElement element )
	{
		return element.Attributes()
			.Where( item => !item.IsNamespaceDeclaration )
			.ToDictionary( item => item.Name, item => item.Value );
	}

	private static string NormalizeRequiredPath( string path, string parameterName )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
		{
			throw new ArgumentException( UiText.T( "XmlCoverageAnalyzer", "An XML file path is required." ), parameterName );
		}

		return Path.GetFullPath( path );
	}

	private static string ExpandedName( XName name )
	{
		return name.NamespaceName.Length == 0 ? name.LocalName : "{" + name.NamespaceName + "}" + name.LocalName;
	}

	private static string DisplayName( XName name ) => ExpandedName( name );

	private static string KeyedPath( string parentPath, string elementName, IdentityPlan plan, string key )
	{
		string value = key;
		string ordinal = string.Empty;
		int separator = key.LastIndexOf( OccurrenceSeparator );

		if ( plan.UsesOccurrenceOrdinal && separator >= 0 )
		{
			value = key[..separator];
			ordinal = "[" + key[( separator + 1 )..] + "]";
		}

		return parentPath + "/" + elementName + "[@" + plan.AttributeName + "='" +
			value.Replace( "'", "&apos;", StringComparison.Ordinal ) + "']" + ordinal;
	}

	private static XmlCoverageFinding Covered( string sourcePath, string location, string message )
		=> new XmlCoverageFinding( sourcePath, location, XmlCoverageStatus.Covered, message );

	private static XmlCoverageFinding Review( string sourcePath, string location, string message )
		=> new XmlCoverageFinding( sourcePath, location, XmlCoverageStatus.ReviewRequired, message );

	private static XmlCoverageFinding NotCovered( string sourcePath, string location, string message )
		=> new XmlCoverageFinding( sourcePath, location, XmlCoverageStatus.NotCovered, message );

	private sealed class ParsedXml
	{
		public XDocument Document { get; }

		public string? UnsupportedConstruct { get; }

		public ParsedXml( XDocument document, string? unsupportedConstruct )
		{
			Document = document;
			UnsupportedConstruct = unsupportedConstruct;
		}
	}

	private enum IdentityKind
	{
		Keyed,
		Singleton,
		Ambiguous
	}

	private sealed class IdentityPlan
	{
		public IdentityKind Kind { get; }

		public string? AttributeName { get; }

		public bool UsesOccurrenceOrdinal { get; }

		public string Reason { get; }

		private IdentityPlan( IdentityKind kind, string? attributeName, bool usesOccurrenceOrdinal, string reason )
		{
			Kind = kind;
			AttributeName = attributeName;
			UsesOccurrenceOrdinal = usesOccurrenceOrdinal;
			Reason = reason;
		}

		public static IdentityPlan Keyed( string attributeName, bool usesOccurrenceOrdinal )
			=> new IdentityPlan( IdentityKind.Keyed, attributeName, usesOccurrenceOrdinal, string.Empty );

		public static IdentityPlan Singleton() => new IdentityPlan( IdentityKind.Singleton, null, false, string.Empty );

		public static IdentityPlan Ambiguous( string reason ) => new IdentityPlan( IdentityKind.Ambiguous, null, false, reason );
	}
}

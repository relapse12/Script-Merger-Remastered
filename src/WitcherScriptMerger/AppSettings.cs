using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace WitcherScriptMerger;

// Portable settings stored beside the application. Embedded defaults fill missing keys;
// legacy settings are imported when no portable file exists.
internal class AppSettings : IEngineSettings
{
	internal const string PortableFileName = "ScriptMergerRemastered.settings.xml";
	internal const string DefaultsResourceName = "ScriptMergerRemastered.DefaultSettings";
	internal const string PriorityToolTipsKey = "PrioToolTips";
	private const string LegacyPriorityToolTipsKey = "PrioTooltips";
	private readonly Dictionary<string, string> _values = new( StringComparer.Ordinal );
	internal string FilePath { get; }

	public AppSettings() : this( Assembly.GetEntryAssembly().Location )
	{
	}

	// Imports legacy settings immediately. When ConflictListFontSize is absent, removes its
	// default so StyledTreeView uses the saved ConflictListSize preset.
	internal AppSettings( string assemblyPath )
	{
		FilePath = Path.Combine( Path.GetDirectoryName( Path.GetFullPath( assemblyPath ) ), PortableFileName );

		using ( Stream defaults = typeof( AppSettings ).Assembly.GetManifestResourceStream( DefaultsResourceName )
			?? throw new InvalidOperationException( UiText.T( "AppSettings", "The application's default settings are missing." ) ) )
		{
			ReadSettings( defaults, _values );
		}

		string savedPath = File.Exists( FilePath ) ? FilePath : FindLegacyConfiguration( assemblyPath );

		if ( savedPath != null )
		{
			Dictionary<string, string> saved = new( StringComparer.Ordinal );

			using ( Stream stream = File.OpenRead( savedPath ) )
			{
				ReadSettings( stream, saved );
			}

			// Older settings used a different name for this option.
			if ( saved.Remove( LegacyPriorityToolTipsKey, out string legacyToolTips )
				&& int.TryParse( legacyToolTips, out int choice ) && choice >= 0 && choice <= 3 )
			{
				saved[PriorityToolTipsKey] = choice.ToString();
			}

			if ( saved.ContainsKey( "ConflictListSize" ) && !saved.ContainsKey( "ConflictListFontSize" ) )
			{
				_values.Remove( "ConflictListFontSize" );
			}

			foreach ( var entry in saved )
			{
				_values[entry.Key] = entry.Value;
			}

			if ( !string.Equals( savedPath, FilePath, StringComparison.OrdinalIgnoreCase ) )
			{
				SaveToFile();
			}
		}
	}

	private static string FindLegacyConfiguration( string assemblyPath )
	{
		return new[] { assemblyPath + ".config", Path.ChangeExtension( assemblyPath, ".exe" ) + ".config" }
			.FirstOrDefault( File.Exists );
	}

	// Accepts the portable root or legacy appSettings section. Duplicate keys are rejected;
	// legacy entries without a value are treated as empty.
	private static void ReadSettings( Stream stream, Dictionary<string, string> destination )
	{
		using XmlReader reader = XmlReader.Create( stream, new XmlReaderSettings
		{
			DtdProcessing = DtdProcessing.Prohibit,
			XmlResolver = null
		} );
		XDocument document = XDocument.Load( reader );
		XElement settings = document.Root?.Name == "configuration"
			? document.Root.Element( "appSettings" )
			: document.Root?.Name == "settings" ? document.Root : null;

		if ( settings == null )
		{
			throw new InvalidDataException( UiText.T( "AppSettings", "The settings file does not contain application settings." ) );
		}

		foreach ( XElement entry in settings.Elements( "add" ) )
		{
			string key = (string)entry.Attribute( "key" );
			string value = (string)entry.Attribute( "value" )
				?? ( document.Root.Name == "configuration" ? string.Empty : null );

			if ( string.IsNullOrEmpty( key ) || value == null || !destination.TryAdd( key, value ) )
			{
				throw new InvalidDataException( UiText.T( "AppSettings", "The settings file contains an invalid or duplicate setting." ) );
			}
		}
	}

	public void Set( string key, object value )
	{
		_values[CanonicalKey( key )] = value.ToString();
	}

	public T Get<T>( string key )
	{
		try
		{
			return (T)typeof( T ).GetMethod( "Parse", new[] { typeof( string ) } )
				.Invoke( null, new object[] { Get( key ) } );
		}
		catch
		{
			return default;
		}
	}

	private static string CanonicalKey( string key ) => key == LegacyPriorityToolTipsKey ? PriorityToolTipsKey : key;

	public string Get( string key ) => _values.TryGetValue( CanonicalKey( key ), out string value ) ? value : string.Empty;

	// Saves and shows any failure in the main window. Before that window exists, the failure is thrown.
	public void Save()
	{
		try
		{
			SaveToFile();
		}
		catch ( Exception ex )
		{
			if ( Program.MainFrm == null )
			{
				throw;
			}

			Program.MainFrm.ShowError( UiText.Format( "AppSettings", "Failed to save settings:\n\n{0}\n\n{1}", FilePath, ex.Message ) );
		}
	}

	// Write the complete settings file before replacing the saved copy.
	internal void SaveToFile()
	{
		string temporaryPath = FilePath + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";

		try
		{
			using ( FileStream stream = new( temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None ) )
			{
				using ( XmlWriter writer = XmlWriter.Create( stream, new XmlWriterSettings
				{
					Encoding = new UTF8Encoding( false ),
					Indent = true,
					CloseOutput = false
				} ) )
				{
					new XDocument( new XElement( "settings", _values.OrderBy( entry => entry.Key, StringComparer.Ordinal )
						.Select( entry => new XElement( "add", new XAttribute( "key", entry.Key ),
							new XAttribute( "value", entry.Value ) ) ) ) ).Save( writer );
				}

				stream.Flush( flushToDisk: true );
			}

			File.Move( temporaryPath, FilePath, overwrite: true );
		}
		finally
		{
			if ( File.Exists( temporaryPath ) )
			{
				File.Delete( temporaryPath );
			}
		}
	}
}

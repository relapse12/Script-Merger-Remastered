namespace WitcherScriptMerger;

internal interface IEngineSettings
{
	string Get( string key );

	T Get<T>( string key );
}

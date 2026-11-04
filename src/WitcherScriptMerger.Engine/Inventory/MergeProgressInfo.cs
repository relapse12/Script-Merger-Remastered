using System.ComponentModel;

namespace WitcherScriptMerger.Inventory;

public class MergeProgressInfo : INotifyPropertyChanged
{
	private string _currentAction;

	private string _currentPhase;

	private int _currentMergeNum;

	private int _totalMergeCount;

	private string _currentFileName;

	private int _currentFileNum;

	private int _totalFileCount;

	public string CurrentAction
	{
		get
		{
			return _currentAction;
		}
		set
		{
			Set( ref _currentAction, value );
		}
	}

	public string CurrentPhase
	{
		get
		{
			return _currentPhase;
		}
		set
		{
			Set( ref _currentPhase, value );
		}
	}

	public int CurrentMergeNum
	{
		get
		{
			return _currentMergeNum;
		}
		set
		{
			_currentMergeNum = value;
			UpdatePhase();
		}
	}

	public int TotalMergeCount
	{
		get
		{
			return _totalMergeCount;
		}
		set
		{
			_totalMergeCount = value;
			UpdatePhase();
		}
	}

	public string CurrentFileName
	{
		get
		{
			return _currentFileName;
		}
		set
		{
			_currentFileName = value;
			UpdatePhase();
		}
	}

	public int CurrentFileNum
	{
		get
		{
			return _currentFileNum;
		}
		set
		{
			_currentFileNum = value;
			UpdatePhase();
		}
	}

	public int TotalFileCount
	{
		get
		{
			return _totalFileCount;
		}
		set
		{
			_totalFileCount = value;
			UpdatePhase();
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	protected virtual void OnPropertyChanged()
	{
		this.PropertyChanged?.Invoke( this, null );
	}

	private void Set<T>( ref T property, T value )
	{
		property = value;
		OnPropertyChanged();
	}

	private void UpdatePhase()
	{
		CurrentPhase = UiText.Format(
			"MergeProgressInfo",
			"Resolving mod conflict{0}\nFile{1}: {2}",
			( ( TotalMergeCount > 1 ) ? UiText.F( "MergeProgressInfo", $" {CurrentMergeNum} of {TotalMergeCount}" ) : "" ),
			( ( TotalFileCount > 1 && TotalFileCount != TotalMergeCount ) ? UiText.F( "MergeProgressInfo", $" {CurrentFileNum} of {TotalFileCount}" ) : "" ),
			CurrentFileName );
	}
}

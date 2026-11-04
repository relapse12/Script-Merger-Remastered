using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace WitcherScriptMerger.Theming;

public class ColorFader
{
	private Timer fadeTimer;

	private Color targetColor;

	private readonly Control outputControl;

	private readonly PropertyInfo colorProperty;

	private readonly string colorPropertyName;

	private readonly int fadeInDuration = 700;

	private readonly int fadeOutDuration = 700;

	private int stepR;

	private int stepG;

	private int stepB;

	public Color OutputColor
	{
		get
		{
			return GetNestedColorProperty( outputControl, colorPropertyName );
		}
		set
		{
			SetNestedProperty( outputControl, colorPropertyName, value );
		}
	}

	public ColorFader( Control control, string colorPropertyName, int fadeInDuration = 700, int fadeOutDuration = 700 )
	{
		colorProperty = FindNestedProperty( control.GetType(), colorPropertyName );

		if ( (object)colorProperty == null || colorProperty.PropertyType != typeof( Color ) )
		{
			throw new ArgumentException(
				UiText.Format( "ColorFader", "Invalid color property name or unsupported type: {0}", colorPropertyName ),
				"colorPropertyName" );
		}

		outputControl = control;
		this.fadeInDuration = fadeInDuration;
		this.fadeOutDuration = fadeOutDuration;
		this.colorPropertyName = colorPropertyName;
	}

	public static Color GetNestedColorProperty( object target, string propertyFullName )
	{
		string[] propertyNames = propertyFullName.Split( '.' );
		int lastIndex = propertyNames.Length - 1;
		PropertyInfo property;

		for ( int i = 0; i < lastIndex; i++ )
		{
			property = target.GetType().GetProperty( propertyNames[i], BindingFlags.Instance | BindingFlags.Public );

			if ( property == null )
			{
				throw new ArgumentException( UiText.Format( "ColorFader", "Invalid property name: {0}", propertyFullName ) );
			}

			target = property.GetValue( target );
		}

		property = target.GetType().GetProperty( propertyNames[lastIndex], BindingFlags.Instance | BindingFlags.Public );

		if ( !( property != null ) || !( property.PropertyType == typeof( Color ) ) )
		{
			throw new ArgumentException( UiText.Format( "ColorFader", "Invalid property name or unsupported type: {0}", propertyFullName ) );
		}

		return (Color)property.GetValue( target );
	}

	internal void Stop()
	{
		fadeTimer?.Stop();
		fadeTimer?.Dispose();
		fadeTimer = null;
	}

	private static void SetNestedProperty( object target, string propertyFullName, Color newColor )
	{
		string[] propertyNames = propertyFullName.Split( '.' );
		int lastIndex = propertyNames.Length - 1;
		PropertyInfo property;

		for ( int i = 0; i < lastIndex; i++ )
		{
			property = target.GetType().GetProperty( propertyNames[i], BindingFlags.Instance | BindingFlags.Public );

			if ( property == null )
			{
				throw new ArgumentException( UiText.Format( "ColorFader", "Invalid property name: {0}", propertyFullName ) );
			}

			target = property.GetValue( target );
		}

		property = target.GetType().GetProperty( propertyNames[lastIndex], BindingFlags.Instance | BindingFlags.Public );

		if ( property != null && property.PropertyType == typeof( Color ) )
		{
			property.SetValue( target, newColor );

			return;
		}

		throw new ArgumentException( UiText.Format( "ColorFader", "Invalid color property name or unsupported type: {0}", propertyFullName ) );
	}

	private static PropertyInfo FindNestedProperty( Type type, string propertyFullName )
	{
		string[] array = propertyFullName.Split( '.' );
		PropertyInfo nestedProperty = null;
		Type currentType = type;
		string[] array2 = array;

		foreach ( string propertyName in array2 )
		{
			nestedProperty = currentType.GetProperty( propertyName, BindingFlags.Instance | BindingFlags.Public );

			if ( nestedProperty == null )
			{
				return null;
			}

			currentType = nestedProperty.PropertyType;
		}

		return nestedProperty;
	}

	public void StartFadeIn( Color targetColor )
	{
		this.targetColor = targetColor;
		StartFadeEffect( fadeInDuration );
	}

	public void StartFadeOut( Color targetColor )
	{
		this.targetColor = targetColor;
		StartFadeEffect( fadeOutDuration );
	}

	private void CalculateSteps( int fadeDuration )
	{
		int steps = fadeDuration / fadeTimer.Interval;

		stepR = ( targetColor.R - OutputColor.R ) / steps;
		stepG = ( targetColor.G - OutputColor.G ) / steps;
		stepB = ( targetColor.B - OutputColor.B ) / steps;
	}

	private void StartFadeEffect( int fadeDuration )
	{
		fadeTimer?.Stop();
		fadeTimer = new Timer();
		CalculateSteps( fadeDuration );

		int maxStep = Math.Max( Math.Abs( stepR ), Math.Max( Math.Abs( stepG ), Math.Abs( stepB ) ) );

		maxStep = Math.Max( maxStep, 1 );
		fadeTimer.Interval = fadeDuration / maxStep;
		fadeTimer.Tick += FadeEffectTick;
		fadeTimer.Start();
	}

	private void FadeEffectTick( object sender, EventArgs e )
	{
		int r = OutputColor.R + stepR;
		int g = OutputColor.G + stepG;
		int b = OutputColor.B + stepB;

		if ( ( stepR > 0 && r >= targetColor.R ) || ( stepR < 0 && r <= targetColor.R ) )
		{
			r = targetColor.R;
		}

		if ( ( stepG > 0 && g >= targetColor.G ) || ( stepG < 0 && g <= targetColor.G ) )
		{
			g = targetColor.G;
		}

		if ( ( stepB > 0 && b >= targetColor.B ) || ( stepB < 0 && b <= targetColor.B ) )
		{
			b = targetColor.B;
		}

		r = Math.Max( Math.Min( r, 255 ), 0 );
		g = Math.Max( Math.Min( g, 255 ), 0 );
		b = Math.Max( Math.Min( b, 255 ), 0 );
		OutputColor = Color.FromArgb( r, g, b );

		if ( r == targetColor.R && g == targetColor.G && b == targetColor.B )
		{
			fadeTimer.Stop();
		}
	}
}

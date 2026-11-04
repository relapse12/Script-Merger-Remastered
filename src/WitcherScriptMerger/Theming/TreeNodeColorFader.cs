using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace WitcherScriptMerger.Theming;

public class TreeNodeColorFader
{
	private Timer fadeTimer;

	private Color targetColor;

	private bool isFadingIn;

	private bool isFadingOut;

	public TreeNode OutputNode;

	private readonly PropertyInfo colorProperty;

	private int stepR;

	private int stepG;

	private int stepB;

	public Color OutputColor
	{
		get
		{
			return (Color)colorProperty.GetValue( OutputNode );
		}
		set
		{
			colorProperty.SetValue( OutputNode, value );
		}
	}

	public TreeNodeColorFader( TreeNode node, string colorPropertyName )
	{
		colorProperty = node.GetType().GetProperty( colorPropertyName, BindingFlags.Instance | BindingFlags.Public );
		OutputNode = node;
	}

	internal void Stop()
	{
		fadeTimer?.Stop();
		fadeTimer?.Dispose();
		fadeTimer = null;
		isFadingIn = isFadingOut = false;
	}

	public void StartFadeIn( Color targetColor )
	{
		isFadingOut = false;

		if ( !isFadingIn )
		{
			this.targetColor = targetColor;
			StartFadeEffect( 200, fadeIn: true );
		}
	}

	public void StartFadeOut( Color targetColor )
	{
		isFadingIn = false;

		if ( !isFadingOut )
		{
			this.targetColor = targetColor;
			StartFadeEffect( 600, fadeIn: false );
		}
	}

	private void CalculateSteps( int fadeDuration )
	{
		int steps = fadeDuration / fadeTimer.Interval;

		stepR = ( targetColor.R - OutputColor.R ) / steps;
		stepG = ( targetColor.G - OutputColor.G ) / steps;
		stepB = ( targetColor.B - OutputColor.B ) / steps;
	}

	// The timer interval comes from the largest color step, so the fade takes about fadeDuration.
	private void StartFadeEffect( int fadeDuration, bool fadeIn )
	{
		fadeTimer?.Stop();
		fadeTimer = new Timer();
		CalculateSteps( fadeDuration );

		int maxStep = Math.Max( Math.Abs( stepR ), Math.Max( Math.Abs( stepG ), Math.Abs( stepB ) ) );

		maxStep = Math.Max( maxStep, 1 );
		fadeTimer.Interval = fadeDuration / maxStep;
		fadeTimer.Tick += delegate
		{
			FadeEffectTick( fadeIn );
		};
		fadeTimer.Start();

		if ( fadeIn )
		{
			isFadingIn = true;
		}
		else
		{
			isFadingOut = true;
		}
	}

	private void FadeEffectTick( bool fadeIn )
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

			if ( fadeIn )
			{
				isFadingIn = false;
			}
			else
			{
				isFadingOut = false;
			}
		}
	}
}

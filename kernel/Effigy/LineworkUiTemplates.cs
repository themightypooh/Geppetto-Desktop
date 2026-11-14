namespace Effigy;

/// <summary>
/// Built-in UI designer starters. The Templates menu calls these, and the kernel tests bake them.
/// </summary>
public static class LineworkUiTemplates
{
	/// <summary>
	/// A plain form: label, text field, two sliders, a checked and an unchecked box, a dropdown, and two buttons.
	/// Sits centered on the 960x540 designer screen. ProgressValue is 0..1 (a checkbox is on at 0.5 and above).
	/// </summary>
	public static LineworkUiNode BasicControls()
	{
		var root = new LineworkUiNode
		{
			Name = "BasicControls",
			Tag = LineworkUiTag.Panel,
			Direction = LineworkFlexDirection.Column,
			JustifyContent = LineworkJustify.Center,
			AlignItems = LineworkAlign.Center
		};

		var card = Group( "ControlsCard", 510f, 12f );
		card.Width = 420f;
		card.BackgroundColor = "#1c2028";
		card.BorderColor = "#3c4454";
		card.BorderWidth = 1f;
		card.BorderRadius = 8f;
		card.Padding = new LineworkInsets( 16f );
		root.AddChild( card );

		var header = Group( "Header", 58f, 4f );
		header.AddChild( TextLabel( "Title", "Controls", 18f, 700, "#f4f6f8", 32f ) );
		header.AddChild( TextLabel( "Hint", "Starter. Change these, then bake.", 13f, 400, "#9aa3b2", 22f ) );
		card.AddChild( header );

		var nameGroup = Group( "NameGroup", 70f, 4f );
		nameGroup.AddChild( FieldLabel( "NameLabel", "Name" ) );
		nameGroup.AddChild( new LineworkUiNode
		{
			Name = "NameField",
			Tag = LineworkUiTag.TextEntry,
			Placeholder = "Player name",
			BindingExpr = "@PlayerName",
			Height = 44f,
			FontSize = 14f,
			TextColor = "#e8eaed",
			BackgroundColor = "#12141a",
			BorderColor = "#3c4454",
			BorderWidth = 1f,
			BorderRadius = 4f,
			Padding = new LineworkInsets( 10f, 0f, 10f, 0f )
		} );
		card.AddChild( nameGroup );

		card.AddChild( SliderGroup( "VolumeGroup", "VolumeLabel", "Volume", "VolumeSlider", 0.65f, "@Volume" ) );
		card.AddChild( SliderGroup( "BrightnessGroup", "BrightnessLabel", "Brightness", "BrightnessSlider", 0.35f, "@Brightness" ) );

		var toggles = Group( "ToggleGroup", 64f, 8f );
		toggles.AddChild( Check( "PlaySounds", "Play sounds", true ) );
		toggles.AddChild( Check( "Fullscreen", "Fullscreen", false ) );
		card.AddChild( toggles );

		var quality = Group( "QualityGroup", 70f, 4f );
		quality.AddChild( FieldLabel( "QualityLabel", "Quality" ) );
		quality.AddChild( new LineworkUiNode
		{
			Name = "QualityDropdown",
			Tag = LineworkUiTag.Dropdown,
			Text = "High",
			Height = 44f,
			FontSize = 14f,
			TextColor = "#e8eaed",
			BackgroundColor = "#12141a",
			BorderColor = "#3c4454",
			BorderWidth = 1f,
			BorderRadius = 4f,
			Padding = new LineworkInsets( 10f, 0f, 28f, 0f )
		} );
		card.AddChild( quality );

		var actions = Group( "Actions", 36f, 8f, LineworkFlexDirection.Row );
		actions.JustifyContent = LineworkJustify.FlexEnd;
		actions.AddChild( Button( "CancelButton", "Cancel", "#2a3140", "#5c677a", "#e8eaed", "@(() => Cancel())" ) );
		actions.AddChild( Button( "ApplyButton", "Apply", "#3b6fd8", "#3b6fd8", "#ffffff", "@(() => Apply())" ) );
		card.AddChild( actions );

		return root;
	}

	private static LineworkUiNode Group( string name, float height, float gap, LineworkFlexDirection direction = LineworkFlexDirection.Column )
	{
		return new LineworkUiNode
		{
			Name = name,
			Tag = LineworkUiTag.Panel,
			Direction = direction,
			Height = height,
			Gap = gap,
			AlignItems = direction == LineworkFlexDirection.Row ? LineworkAlign.Center : LineworkAlign.Stretch
		};
	}

	private static LineworkUiNode TextLabel( string name, string text, float fontSize, int weight, string color, float height )
	{
		return new LineworkUiNode
		{
			Name = name,
			Tag = LineworkUiTag.Label,
			Text = text,
			FontSize = fontSize,
			FontWeight = weight,
			TextColor = color,
			Height = height
		};
	}

	private static LineworkUiNode FieldLabel( string name, string text )
		=> TextLabel( name, text, 13f, 600, "#c5ccd6", 22f );

	private static LineworkUiNode SliderGroup( string groupName, string labelName, string label, string sliderName, float value, string binding )
	{
		var group = Group( groupName, 54f, 4f );
		group.AddChild( FieldLabel( labelName, label ) );
		group.AddChild( new LineworkUiNode
		{
			Name = sliderName,
			Tag = LineworkUiTag.Slider,
			Direction = LineworkFlexDirection.Row,
			AlignItems = LineworkAlign.Center,
			Gap = 8f,
			Height = 28f,
			ProgressValue = value,
			ProgressColor = "#7eb6ff",
			MinValue = 0f,
			MaxValue = 100f,
			StepValue = 1f,
			BindingExpr = binding,
			TextColor = "#e8eaed"
		} );
		return group;
	}

	private static LineworkUiNode Check( string name, string text, bool on )
	{
		return new LineworkUiNode
		{
			Name = name,
			Tag = LineworkUiTag.Checkbox,
			Text = text,
			Direction = LineworkFlexDirection.Row,
			AlignItems = LineworkAlign.Center,
			Gap = 8f,
			Height = 28f,
			FontSize = 14f,
			TextColor = "#e8eaed",
			ProgressValue = on ? 1f : 0f,
			ProgressColor = "#7eb6ff"
		};
	}

	private static LineworkUiNode Button( string name, string text, string background, string border, string textColor, string onClick )
	{
		return new LineworkUiNode
		{
			Name = name,
			Tag = LineworkUiTag.Button,
			Text = text,
			Width = 112f,
			Height = 36f,
			FontSize = 15f,
			FontWeight = 600,
			TextColor = textColor,
			TextAlign = "center",
			BackgroundColor = background,
			BorderColor = border,
			BorderWidth = 1f,
			BorderRadius = 4f,
			OnClickExpr = onClick
		};
	}
}

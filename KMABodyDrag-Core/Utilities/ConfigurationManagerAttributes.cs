// Optional ConfigurationManager display overrides for settings.
// Null fields keep ConfigurationManager's defaults; assigned fields override them.
// Reference: https://github.com/BepInEx/BepInEx.ConfigurationManager
#pragma warning disable 0169, 0414, 0649

//====================[ ConfigurationManagerAttributes ]====================
internal sealed class ConfigurationManagerAttributes
{
    // Show ranged values as percentages.
    public bool? ShowRangeAsPercent;

    // Custom editor that replaces ConfigurationManager's default editor.
    public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;

    // Whether to show this setting in the settings screen.
    public bool? Browsable;

    // Setting category; null places it directly under the plugin.
    public string Category;

    // Value used by the setting's Default button.
    public object DefaultValue;

    // Hides the Reset button even when DefaultValue is available.
    public bool? HideDefaultButton;

    // Hides the setting name, usually to give a custom editor more room.
    public bool? HideSettingName;

    // Optional hover description; prefer setting the description when creating the setting.
    public string Description;

    // Display name of the setting.
    public string DispName;

    // Relative order within the category; higher values appear higher in the list.
    public int? Order;

    // Show the value without allowing edits.
    public bool? ReadOnly;

    // Hide by default unless advanced settings are shown or the setting is searched.
    public bool? IsAdvanced;

    // Converts setting values to text for built-in editor text boxes.
    public System.Func<object, string> ObjToStr;

    // Converts text box input to the setting's value type.
    public System.Func<string, object> StrToObj;
}

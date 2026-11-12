using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using BiliIndexTranslate.Core.Localization;

namespace BiliIndexTranslate.App.Localization;

/// <summary>
/// The interface language in effect and the text for it. XAML reads text through <see cref="TrExtension"/>,
/// which binds to the indexer, so every window follows a language change without being reopened.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private Loc() { }

    /// <summary>"en" or "zh-CN". Until the settings are loaded this follows the Windows display language.</summary>
    public string Language { get; private set; } = InterfaceLanguage.Resolve(InterfaceLanguage.Auto, CultureInfo.CurrentUICulture.Name);

    public string this[string key] => UiText.Translate(Language, key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Text for <paramref name="key"/> in the current language, with {0}, {1} ... filled in.</summary>
    public static string T(string key, params object?[] args) => UiText.Translate(Instance.Language, key, args);

    /// <summary>Applies the interface language setting ("auto", "en" or "zh-CN"). Call on the UI thread.</summary>
    public void Apply(string? setting)
    {
        var next = InterfaceLanguage.Resolve(setting, CultureInfo.CurrentUICulture.Name);
        if (next == Language) return;
        Language = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    /// <summary>For text chosen in code (a toggle's label, say): binds it like XAML does, so it also follows a language change.</summary>
    public static void Bind(DependencyObject target, DependencyProperty property, string key)
        => BindingOperations.SetBinding(target, property, CreateBinding(key));

    internal static Binding CreateBinding(string key) => new($"[{key}]") { Source = Instance, Mode = BindingMode.OneWay };
}

/// <summary>XAML: <c>Text="{loc:Tr Settings_Title}"</c>.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.CreateBinding(Key).ProvideValue(serviceProvider);
}

using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using LocalBack.Core.Util;

namespace LocalBack.App.Localization;

/// <summary>
/// All UI text, by key, from Localization/Strings.&lt;lang&gt;.json (embedded). Switching language at run time
/// re-reads every binding made with <see cref="TExtension"/>; view models call <see cref="T"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly Loc Instance = new();

    /// <summary>(code, name in its own language)</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Available = new[] { ("en", "English"), ("pl", "Polski") };

    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallback = new();
    private string _language = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc()
    {
        _fallback = Load("en");
        _strings = _fallback;
    }

    public string Language => _language;
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>Picks the language: an explicit code, or null to follow the Windows display language.</summary>
    public void Apply(string? code)
    {
        var lang = code ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (!Available.Any(a => a.Code == lang)) lang = "en";
        _language = lang;
        _strings = lang == "en" ? _fallback : Load(lang);
        Culture = CultureInfo.GetCultureInfo(lang == "pl" ? "pl-PL" : "en-GB");
        Format.Culture = Culture;
        Format.Today = this["time.today"];
        Format.Yesterday = this["time.yesterday"];
        Format.PluralProvider = (n, one, many) => Plural(n, one, many);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    public string this[string key] => _strings.TryGetValue(key, out var s) ? s : _fallback.TryGetValue(key, out var f) ? f : key;

    /// <summary>Text for a key, with {0}-style arguments.</summary>
    public static string T(string key, params object[] args)
    {
        var s = Instance[key];
        return args.Length == 0 ? s : string.Format(Instance.Culture, s, args);
    }

    /// <summary>
    /// "3 files" with the right plural for the language. Keys are <c>key.one</c>, <c>key.few</c> (Polish 2–4), <c>key.many</c>.
    /// Falls back to the English words given by Core callers when the key is not in the table.
    /// </summary>
    public string Plural(int n, string one, string many)
    {
        var key = "plural." + many;
        var form = _language == "pl" ? PolishForm(n) : n == 1 ? "one" : "many";
        if (_strings.TryGetValue($"{key}.{form}", out var s) || _strings.TryGetValue($"{key}.many", out s))
            return string.Format(Culture, s, n);
        return $"{n} {(n == 1 ? one : many)}";
    }

    private static string PolishForm(int n)
    {
        if (n == 1) return "one";
        int m10 = n % 10, m100 = n % 100;
        return m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? "few" : "many";
    }

    private static Dictionary<string, string> Load(string lang)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"LocalBack.App.Localization.Strings.{lang}.json");
        if (stream == null) return new();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }
}

/// <summary>XAML: <c>Text="{l:T some.key}"</c>. A binding to <see cref="Loc.Instance"/>, so the text follows language changes.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    public TExtension() { }
    public TExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}

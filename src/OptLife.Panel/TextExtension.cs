using System.Windows.Markup;

namespace OptLife.Panel;

/// <summary>
/// A text written in both languages right where it is shown: <c>Content="{l:T PANORAMICA, OVERVIEW}"</c>
/// (quote with ' when the text has a comma, and write \' for an apostrophe). Read once, when the window
/// is built: the language is chosen at start-up.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string it, string en) => (It, En) = (it, en);

    [ConstructorArgument("it")] public string It { get; set; } = "";
    [ConstructorArgument("en")] public string En { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(It, En);
}

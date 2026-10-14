using Avalonia.Data;
using Avalonia.Data.Converters;

namespace ZillaTerm.App.Views;

/// <summary>
/// Liaison d'une colonne de tableau (DataGrid) en lecture seule. Le DataGrid d'Avalonia lie ses cellules dans les deux
/// sens par défaut, même en lecture seule : sans <see cref="BindingMode.OneWay"/>, une valeur convertie pour l'affichage
/// (« ✓ ») serait réécrite dans l'objet affiché (le droit d'un membre de safe remis à faux).
/// </summary>
internal static class GridBinding
{
    public static Binding OneWay(string path, IValueConverter? converter = null) =>
        new(path) { Mode = BindingMode.OneWay, Converter = converter };
}

using System.Windows;

namespace CATRA.UI.Helpers;

/// <summary>
/// A <see cref="Freezable"/>-based proxy that carries an arbitrary object
/// (<see cref="Data"/>) into a resource dictionary. It is used to bridge a
/// binding source into visual trees that do not inherit the normal
/// <c>DataContext</c> — most notably a <see cref="System.Windows.Controls.ContextMenu"/>,
/// which lives in its own name/data-context scope. By exposing the host
/// <see cref="System.Windows.Controls.UserControl"/> through a static resource,
/// menu items can bind to the control's dependency-property commands.
/// </summary>
public sealed class BindingProxy : Freezable
{
    /// <summary>Identifies the <see cref="Data"/> dependency property.</summary>
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new PropertyMetadata(null));

    /// <summary>The object exposed to bindings that reference this proxy.</summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <inheritdoc />
    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

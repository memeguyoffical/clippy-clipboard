using System.Drawing;
using System.Windows.Forms;

namespace Clippy.App.Ui;

/// <summary>Context menu painted with the app palette instead of the light system one.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    internal DarkMenuRenderer() : base(new DarkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextFaint;
        base.OnRenderItemText(e);
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.SurfaceRaised;

        public override Color MenuBorder => Theme.Border;

        public override Color MenuItemBorder => Theme.Border;

        public override Color MenuItemSelected => Theme.Selection;

        public override Color MenuItemSelectedGradientBegin => Theme.Selection;

        public override Color MenuItemSelectedGradientEnd => Theme.Selection;

        public override Color ImageMarginGradientBegin => Theme.SurfaceRaised;

        public override Color ImageMarginGradientMiddle => Theme.SurfaceRaised;

        public override Color ImageMarginGradientEnd => Theme.SurfaceRaised;

        public override Color SeparatorDark => Theme.Border;

        public override Color SeparatorLight => Theme.Border;
    }
}

using System.Drawing;
using System.Windows.Forms;

namespace ExplorerOne;

internal sealed partial class TrayApplicationContext
{
    private static Icon LoadApplicationIcon()
    {
        using var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("ExplorerOne.AppIcon.ico");
        if (stream is null)
            return (Icon)SystemIcons.Application.Clone();

        using var icon = new Icon(stream, SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }
}

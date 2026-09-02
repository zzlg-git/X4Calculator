using Microsoft.Win32;
using System.IO;

namespace X4Calculator.UI.Services;

public sealed class X4GameDirectoryPicker : IX4GameDirectoryPicker
{
    public string? PickGameDirectory(string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 X4 Foundations 游戏目录",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

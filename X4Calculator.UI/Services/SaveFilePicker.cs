using Microsoft.Win32;

namespace X4Calculator.UI.Services;

public sealed class SaveFilePicker : ISaveFilePicker
{
    public string? PickGzipSave()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 X4 存档",
            Filter = "X4 压缩存档 (*.xml.gz)|*.xml.gz",
            DefaultExt = ".xml.gz",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

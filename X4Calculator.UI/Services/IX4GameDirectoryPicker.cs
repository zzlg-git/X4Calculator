namespace X4Calculator.UI.Services;

public interface IX4GameDirectoryPicker
{
    string? PickGameDirectory(string? initialDirectory = null);
}

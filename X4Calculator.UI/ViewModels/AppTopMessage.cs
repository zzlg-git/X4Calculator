namespace X4Calculator.UI.ViewModels;

/// <summary>应用标题栏中央显示的错误或帮助信息。</summary>
public sealed record AppTopMessage(string Text, AppTopMessageKind Kind);

public enum AppTopMessageKind
{
    Error,
    Help
}

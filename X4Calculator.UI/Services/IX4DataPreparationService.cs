using X4Calculator.Core.Data;

namespace X4Calculator.UI.Services;

public interface IX4DataPreparationService
{
    Task<string?> FindGameDirectoryAsync(CancellationToken cancellationToken);
    bool IsGameDirectory(string gameDirectory);
    IReadOnlyList<X4ContentPackage> DiscoverContent(string gameDirectory);
    X4ExtractionPlan CreatePlan(string gameDirectory, IEnumerable<string> selectedPackageIds);
    Task ExtractAsync(
        X4ExtractionPlan plan,
        string destinationDirectory,
        IProgress<X4ExtractionProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class X4DataPreparationService : IX4DataPreparationService
{
    private readonly X4InstallationLocator _locator;
    private readonly X4CatalogExtractor _extractor;

    public X4DataPreparationService()
        : this(new X4InstallationLocator(), new X4CatalogExtractor())
    {
    }

    public X4DataPreparationService(X4InstallationLocator locator, X4CatalogExtractor extractor)
    {
        _locator = locator;
        _extractor = extractor;
    }

    public Task<string?> FindGameDirectoryAsync(CancellationToken cancellationToken) =>
        Task.Run(() => _locator.FindInstallation(), cancellationToken);

    public bool IsGameDirectory(string gameDirectory) =>
        X4InstallationLocator.IsGameDirectory(gameDirectory);

    public IReadOnlyList<X4ContentPackage> DiscoverContent(string gameDirectory) =>
        _extractor.DiscoverContent(gameDirectory);

    public X4ExtractionPlan CreatePlan(string gameDirectory, IEnumerable<string> selectedPackageIds) =>
        _extractor.CreatePlan(gameDirectory, selectedPackageIds);

    public Task ExtractAsync(
        X4ExtractionPlan plan,
        string destinationDirectory,
        IProgress<X4ExtractionProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => _extractor.ExtractAsync(plan, destinationDirectory, progress, cancellationToken),
            cancellationToken);
}

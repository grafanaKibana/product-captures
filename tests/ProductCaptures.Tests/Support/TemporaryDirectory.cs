namespace ProductCaptures.Tests.Support;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("product-captures-tests-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

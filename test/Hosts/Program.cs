using ProductCaptures;
using System.Text.Json;

if (args.Length == 2 && args[0] == "--quartz")
{
    Console.WriteLine(await QuartzHost.PrepareAsync(Config.Load(args[1])));
    return;
}

var root = Path.Combine(Path.GetTempPath(), "capture-host-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    File.WriteAllText(Path.Combine(root, "package.json"), "{\"name\":\"quartz-test\",\"quartz\":{\"name\":\"test\"}}");
    File.WriteAllText(Path.Combine(root, "package-lock.json"), "{}");
    Directory.CreateDirectory(Path.Combine(root, "fixtures"));
    File.WriteAllText(Path.Combine(root, "fixtures", "index.md"), "# Fixture");
    Config Config() => new()
    {
        Root = root,
        Name = "Test",
        Quartz = new()
        {
            Content = "fixtures"
        }
    };
    void Reject(string name, Action action)
    {
        try
        {
            action();
            throw new Exception("Accepted " + name);
        }
        catch (ArgumentException)
        {
            Console.WriteLine("PASS " + name);
        }
    }

    QuartzHost.Validate(Config());
    Console.WriteLine("PASS minimal Quartz defaults");
    var invalid = Config();
    invalid.Quartz!.Revision = "main";
    Reject("unpinned revision", () => QuartzHost.Validate(invalid));
    invalid = Config();
    invalid.Quartz!.Upstream = "file:///tmp/repo";
    Reject("non-HTTPS upstream", () => QuartzHost.Validate(invalid));
    invalid = Config();
    invalid.Quartz!.Content = "../outside";
    Reject("escaping fixture", () => QuartzHost.Validate(invalid));
    invalid = Config();
    invalid.Quartz!.Inputs = ["package.json"];
    Reject("missing source lock", () => QuartzHost.Validate(invalid));
    invalid = Config();
    invalid.Quartz!.Inputs = ["package.json", "package-lock.json", "../outside"];
    Reject("escaping plugin input", () => QuartzHost.Validate(invalid));
    invalid = Config();
    invalid.Quartz!.Configuration = JsonSerializer.SerializeToElement("invalid");
    Reject("nonobject configuration", () => QuartzHost.Validate(invalid));
    var packagePath = Path.Combine(root, "package.json");
    var packageText = File.ReadAllText(packagePath);
    File.WriteAllText(packagePath, "{}");
    Reject("missing plugin metadata", () => QuartzHost.Validate(Config()));
    File.WriteAllText(packagePath, packageText);
    var outside = Path.Combine(root, "outside.md");
    File.WriteAllText(outside, "outside");
    File.CreateSymbolicLink(Path.Combine(root, "fixtures", "link.md"), outside);
    Reject("symlink fixture tree", () => QuartzHost.Validate(Config()));
    File.Delete(Path.Combine(root, "fixtures", "link.md"));
    var host = Path.Combine(root, ".product-captures-cache", "quartz");
    Directory.CreateDirectory(host);
    File.WriteAllText(Path.Combine(host, "unowned"), "keep");
    try
    {
        await QuartzHost.PrepareAsync(Config());
        throw new Exception("Accepted nonempty host");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("non-empty"))
    {
        Console.WriteLine("PASS nonempty host refusal");
    }

    if (File.ReadAllText(Path.Combine(host, "unowned")) != "keep")
    {
        throw new Exception("Host refusal changed existing file");
    }

    if (File.ReadAllText(Path.Combine(root, "package-lock.json")) != "{}")
    {
        throw new Exception("Source lock changed");
    }

    Console.WriteLine("PASS source lock remains unchanged");
    File.Delete(Path.Combine(host, "unowned"));
    await Processes.RunAsync("git", ["init", "-q"], host);
    await Processes.RunAsync("git", ["remote", "add", "origin", "https://example.invalid/other.git"], host);
    await Processes.RunAsync("git", ["-c", "user.name=Host Test", "-c", "user.email=host@example.invalid", "-c", "core.hooksPath=/dev/null", "commit", "--allow-empty", "-qm", "Fixture"], host);
    try
    {
        await QuartzHost.PrepareAsync(Config());
        throw new Exception("Accepted wrong revision");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("different revision"))
    {
        Console.WriteLine("PASS mismatched cached revision");
    }

    var sameRevision = Config();
    sameRevision.Quartz!.Revision = (await Processes.RunAsync("git", ["rev-parse", "HEAD"], host)).Trim();
    try
    {
        await QuartzHost.PrepareAsync(sameRevision);
        throw new Exception("Accepted wrong origin");
    }
    catch (InvalidOperationException e) when (e.Message.Contains("unexpected origin"))
    {
        Console.WriteLine("PASS mismatched cached origin");
    }

    if (Directory.Exists(Path.Combine(root, "docs/assets/product-captures")))
    {
        throw new Exception("Failure wrote PNG output");
    }

    Console.WriteLine("PASS failed host writes no output");
}
finally
{
    Directory.Delete(root, true);
}

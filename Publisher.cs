using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProductCaptures;

public static class Publisher
{
    static string Repository()
    {
        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "";
        if (!Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new ArgumentException("GITHUB_REPOSITORY must be owner/repository");
        return repo;
    }
    static void Target(ActionOptions options)
    {
        _ = Repository();
        if (options.ReleaseTag.Length > 0 && (!Regex.IsMatch(options.ReleaseTag, @"^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$") || options.ReleaseTag.Contains(".."))) throw new ArgumentException("Invalid release tag");
    }
    public static string[] Images(Config config, ActionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();
        _ = Paths.Within(workspace, config.OutputDirectory);
        if (Processes.Commit(config.Root, cancellationToken) is null) throw new ArgumentException("Publication requires a Git source checkout");
        var manifestPath = Paths.Within(config.Root, Path.Combine(config.OutputDirectory, "manifest.json"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)); var m = manifest.RootElement;
        if (m.GetProperty("version").GetInt32() != 2 || m.GetProperty("tool").GetString() != Program.Version || m.GetProperty("configHash").GetString() != config.ConfigHash || m.GetProperty("mode").GetString() != (options.Options.ReuseScreenshots ? "reuse" : "fresh") || m.GetProperty("source").GetString() != Processes.Commit(config.Root, cancellationToken)) throw new ArgumentException("Capture manifest does not match this config, source checkout or capture mode");
        if (!m.GetProperty("outputs").EnumerateArray().Select(x => x.GetString()).SequenceEqual(CaptureEngine.OwnedFiles(config))) throw new ArgumentException("Capture manifest has unexpected files");
        return CaptureEngine.OwnedFiles(config).Select(file => { var path = Paths.Within(config.Root, Path.Combine(config.OutputDirectory, file)); _ = CaptureEngine.ReadImage(path); return path; }).ToArray();
    }
    public static async Task ReleaseAsync(Config config, ActionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Target(options); var images = Images(config, options, cancellationToken).Where(p => Path.GetDirectoryName(p) == config.OutputDirectory);
        await Processes.RunAsync("gh", new[] { "release", "upload", options.ReleaseTag }.Concat(images).Concat(["--repo", Repository(), "--clobber"]), config.Root, cancellationToken: cancellationToken);
    }
    static async Task<JsonElement> Api(CancellationToken cancellationToken, string root, string endpoint, object? body = null, string? method = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = new List<string> { "api", "--method", method ?? (body is null ? "GET" : "POST"), $"repos/{Repository()}/{endpoint}" };
        if (body is not null) args.AddRange(["--input", "-"]);
        var text = await Processes.RunAsync("gh", args, root, input: body is null ? null : JsonSerializer.Serialize(body), cancellationToken: cancellationToken);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text).RootElement.Clone();
    }
    static async Task<JsonElement?> Optional(CancellationToken cancellationToken, string root, string endpoint)
    { try { return await Api(cancellationToken, root, endpoint); } catch (InvalidOperationException error) when (error.Message.Contains("HTTP 404")) { return null; } }
    public static async Task PullRequestAsync(Config config, ActionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Target(options); var images = Images(config, options, cancellationToken); var root = config.Root;
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();
        var repo = await Api(cancellationToken, root, ""); var baseBranch = repo.GetProperty("default_branch").GetString()!;
        var version = options.ReleaseTag.Length > 0 ? options.ReleaseTag : Processes.Commit(root, cancellationToken)?[..12] ?? "manual";
        var branch = "docs/product-captures-" + Regex.Replace(version, "[^A-Za-z0-9._-]", "-");
        var message = $"docs: refresh product captures for {version}";
        var baseRef = await Api(cancellationToken, root, "git/ref/heads/" + Uri.EscapeDataString(baseBranch));
        var branchRef = await Optional(cancellationToken, root, "git/ref/heads/" + Uri.EscapeDataString(branch));
        var parent = (branchRef ?? baseRef).GetProperty("object").GetProperty("sha").GetString();
        var commit = await Api(cancellationToken, root, "git/commits/" + parent);
        if (branchRef is not null && !commit.GetProperty("message").GetString()!.StartsWith(message, StringComparison.Ordinal)) throw new InvalidOperationException("Existing image branch is not owned by this run");
        var entries = new List<object>();
        foreach (var image in images)
        {
            var relative = Path.GetRelativePath(workspace, image).Replace('\\', '/');
            var blob = await Api(cancellationToken, root, "git/blobs", new { content = Convert.ToBase64String(await File.ReadAllBytesAsync(image, cancellationToken)), encoding = "base64" });
            var existing = await Optional(cancellationToken, root, "contents/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)) + "?ref=" + Uri.EscapeDataString(branchRef is null ? baseBranch : branch));
            if (existing?.GetProperty("sha").GetString() != blob.GetProperty("sha").GetString()) entries.Add(new { path = relative, mode = "100644", type = "blob", sha = blob.GetProperty("sha").GetString() });
        }
        if (entries.Count == 0) { Console.WriteLine("Generated images already match target branch."); return; }
        var tree = await Api(cancellationToken, root, "git/trees", new { base_tree = commit.GetProperty("tree").GetProperty("sha").GetString(), tree = entries });
        var next = await Api(cancellationToken, root, "git/commits", new { message, tree = tree.GetProperty("sha").GetString(), parents = new[] { parent } });
        if (branchRef is null) await Api(cancellationToken, root, "git/refs", new { @ref = "refs/heads/" + branch, sha = next.GetProperty("sha").GetString() });
        else await Api(cancellationToken, root, "git/refs/heads/" + Uri.EscapeDataString(branch), new { sha = next.GetProperty("sha").GetString(), force = false }, "PATCH");
        var pulls = await Api(cancellationToken, root, "pulls?state=open&head=" + Uri.EscapeDataString(Repository().Split('/')[0] + ":" + branch));
        var pull = pulls.GetArrayLength() > 0 ? pulls[0] : await Api(cancellationToken, root, "pulls", new { title = message, head = branch, @base = baseBranch, body = $"Regenerated the configured light and dark images from {version}.\n\nOnly the owned PNGs are included. Review before merging; this workflow never merges the PR." });
        Console.WriteLine("Image update PR: " + pull.GetProperty("html_url").GetString());
    }
}

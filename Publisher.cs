using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProductCaptures;

public static class Publisher
{
    private static string Repository()
    {
        string repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "";
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
        {
            throw new ArgumentException("GITHUB_REPOSITORY must be owner/repository");
        }

        return repository;
    }

    private static void ValidateTarget(ActionOptions options)
    {
        _ = Repository();
        if (options.ReleaseTag.Length > 0
            && (!Regex.IsMatch(options.ReleaseTag, @"^[A-Za-z0-9][A-Za-z0-9._/-]{0,127}$")
                || options.ReleaseTag.Contains("..")))
        {
            throw new ArgumentException("Invalid release tag");
        }
    }

    public static string[] Images(
        Config config,
        ActionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        _ = Paths.Within(workspace, config.OutputDirectory);
        if (Processes.Commit(config.Root, cancellationToken) is null)
        {
            throw new ArgumentException("Publication requires a Git source checkout");
        }

        string manifestPath = Paths.Within(
            config.Root,
            Path.Combine(config.OutputDirectory, "manifest.json"));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        ValidateManifest(manifest.RootElement, config, options, cancellationToken);

        var images = new List<string>();
        foreach (string file in CaptureEngine.OwnedFiles(config))
        {
            string path = Paths.Within(
                config.Root,
                Path.Combine(config.OutputDirectory, file));
            _ = CaptureEngine.ReadImage(path);
            images.Add(path);
        }

        return images.ToArray();
    }

    private static void ValidateManifest(
        JsonElement manifest,
        Config config,
        ActionOptions options,
        CancellationToken cancellationToken)
    {
        if (manifest.GetProperty("version").GetInt32() != 2
            || manifest.GetProperty("tool").GetString() != Program.Version
            || manifest.GetProperty("configHash").GetString() != config.ConfigHash
            || manifest.GetProperty("mode").GetString()
                != (options.Options.ReuseScreenshots ? "reuse" : "fresh")
            || manifest.GetProperty("source").GetString()
                != Processes.Commit(config.Root, cancellationToken))
        {
            throw new ArgumentException("Capture manifest does not match this config, source checkout or capture mode");
        }

        var outputs = manifest.GetProperty("outputs")
            .EnumerateArray()
            .Select(output => output.GetString());
        if (!outputs.SequenceEqual(CaptureEngine.OwnedFiles(config)))
        {
            throw new ArgumentException("Capture manifest has unexpected files");
        }
    }

    public static async Task ReleaseAsync(
        Config config,
        ActionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(options);
        IEnumerable<string> images = Images(config, options, cancellationToken)
            .Where(path => Path.GetDirectoryName(path) == config.OutputDirectory);
        string[] upload = ["release", "upload", options.ReleaseTag];
        IEnumerable<string> arguments = upload
            .Concat(images)
            .Concat(["--repo", Repository(), "--clobber"]);
        await Processes.RunAsync(
            "gh", arguments, config.Root, cancellationToken: cancellationToken);
    }

    private static async Task<JsonElement> ApiAsync(
        CancellationToken cancellationToken,
        string root,
        string endpoint,
        object? body = null,
        string? method = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> arguments =
        [
            "api",
            "--method",
            method ?? (body is null ? "GET" : "POST"),
            $"repos/{Repository()}/{endpoint}"
        ];
        if (body is not null)
        {
            arguments.AddRange(["--input", "-"]);
        }

        string text = await Processes.RunAsync(
            "gh",
            arguments,
            root,
            input: body is null ? null : JsonSerializer.Serialize(body),
            cancellationToken: cancellationToken);
        using JsonDocument response = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(text) ? "null" : text);
        return response.RootElement.Clone();
    }

    private static async Task<JsonElement?> OptionalAsync(
        CancellationToken cancellationToken,
        string root,
        string endpoint)
    {
        try
        {
            return await ApiAsync(cancellationToken, root, endpoint);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("HTTP 404"))
        {
            return null;
        }
    }

    public static async Task PullRequestAsync(
        Config config,
        ActionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(options);
        string[] images = Images(config, options, cancellationToken);
        string root = config.Root;
        string workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
            ?? Directory.GetCurrentDirectory();
        JsonElement repository = await ApiAsync(cancellationToken, root, "");
        string baseBranch = repository.GetProperty("default_branch").GetString()!;
        string version = options.ReleaseTag.Length > 0
            ? options.ReleaseTag
            : Processes.Commit(root, cancellationToken)?[..12] ?? "manual";
        string branch = "docs/product-captures-"
            + Regex.Replace(version, "[^A-Za-z0-9._-]", "-");
        string message = $"docs: refresh product captures for {version}";

        JsonElement baseRef = await ApiAsync(
            cancellationToken, root, "git/ref/heads/" + Uri.EscapeDataString(baseBranch));
        JsonElement? branchRef = await OptionalAsync(
            cancellationToken, root, "git/ref/heads/" + Uri.EscapeDataString(branch));
        string? parent = (branchRef ?? baseRef)
            .GetProperty("object").GetProperty("sha").GetString();
        JsonElement commit = await ApiAsync(
            cancellationToken, root, "git/commits/" + parent);
        if (branchRef is not null
            && !commit.GetProperty("message").GetString()!
                .StartsWith(message, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Existing image branch is not owned by this run");
        }

        List<object> entries = await CreateImageEntriesAsync(
            root, workspace, images, branchRef is null ? baseBranch : branch,
            cancellationToken);
        if (entries.Count == 0)
        {
            Console.WriteLine("Generated images already match target branch.");
            return;
        }

        string? nextCommit = await CommitImagesAsync(
            root, commit, parent, message, entries, cancellationToken);
        await UpdateBranchAsync(
            root, branch, branchRef is null, nextCommit, cancellationToken);
        JsonElement pull = await FindOrCreatePullRequestAsync(
            root, branch, baseBranch, message, version, cancellationToken);
        Console.WriteLine("Image update PR: " + pull.GetProperty("html_url").GetString());
    }

    private static async Task<List<object>> CreateImageEntriesAsync(
        string root,
        string workspace,
        string[] images,
        string targetBranch,
        CancellationToken cancellationToken)
    {
        var entries = new List<object>();
        foreach (string image in images)
        {
            string relative = Path.GetRelativePath(workspace, image).Replace('\\', '/');
            JsonElement blob = await ApiAsync(cancellationToken, root, "git/blobs", new
            {
                content = Convert.ToBase64String(
                    await File.ReadAllBytesAsync(image, cancellationToken)),
                encoding = "base64"
            });
            string contentsPath = string.Join(
                '/', relative.Split('/').Select(Uri.EscapeDataString));
            JsonElement? existing = await OptionalAsync(
                cancellationToken,
                root,
                "contents/" + contentsPath + "?ref=" + Uri.EscapeDataString(targetBranch));
            if (existing?.GetProperty("sha").GetString()
                != blob.GetProperty("sha").GetString())
            {
                entries.Add(new
                {
                    path = relative,
                    mode = "100644",
                    type = "blob",
                    sha = blob.GetProperty("sha").GetString()
                });
            }
        }

        return entries;
    }

    private static async Task<string?> CommitImagesAsync(
        string root,
        JsonElement parentCommit,
        string? parent,
        string message,
        List<object> entries,
        CancellationToken cancellationToken)
    {
        JsonElement tree = await ApiAsync(cancellationToken, root, "git/trees", new
        {
            base_tree = parentCommit.GetProperty("tree").GetProperty("sha").GetString(),
            tree = entries
        });
        string?[] parents = [parent];
        JsonElement commit = await ApiAsync(cancellationToken, root, "git/commits", new
        {
            message,
            tree = tree.GetProperty("sha").GetString(),
            parents
        });
        return commit.GetProperty("sha").GetString();
    }

    private static async Task UpdateBranchAsync(
        string root,
        string branch,
        bool create,
        string? commit,
        CancellationToken cancellationToken)
    {
        if (create)
        {
            await ApiAsync(cancellationToken, root, "git/refs", new
            {
                @ref = "refs/heads/" + branch,
                sha = commit
            });
        }
        else
        {
            await ApiAsync(
                cancellationToken,
                root,
                "git/refs/heads/" + Uri.EscapeDataString(branch),
                new
                {
                    sha = commit,
                    force = false
                },
                "PATCH");
        }
    }

    private static async Task<JsonElement> FindOrCreatePullRequestAsync(
        string root,
        string branch,
        string baseBranch,
        string message,
        string version,
        CancellationToken cancellationToken)
    {
        string head = Uri.EscapeDataString(Repository().Split('/')[0] + ":" + branch);
        JsonElement pulls = await ApiAsync(
            cancellationToken, root, "pulls?state=open&head=" + head);
        if (pulls.GetArrayLength() > 0)
        {
            return pulls[0];
        }

        return await ApiAsync(cancellationToken, root, "pulls", new
        {
            title = message,
            head = branch,
            @base = baseBranch,
            body = $"Regenerated the configured light and dark images from {version}.\n\n"
                + "Only the owned PNGs are included. Review before merging; "
                + "this workflow never merges the PR."
        });
    }
}

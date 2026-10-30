# Product Captures

Capture a product in light and dark themes and compose two matching README images. C# owns capture, composition, host preparation, and publication; Microsoft Playwright supplies Chromium. An optional Node adapter handles the upstream Obsidian launcher.

Each run creates four PNGs in the configured output directory:

- `captures/overview-light.png` and `captures/overview-dark.png`: raw screenshots.
- `hero.png` and `hero-dark.png`: finished images.

A source-free configuration (`"capture": null`) creates only the finished pair. `manifest.json` records the source commit, configuration hash, and owned filenames. A failed fresh capture never falls back to existing images. Files are replaced only after both themes finish successfully.

## Run locally

Install the .NET 10 SDK and clone this repository. Run the tool from its checkout, passing your product configuration:

```sh
cd /path/to/product-captures
dotnet run -- install --with-deps
dotnet run -- --config /path/to/product/product-captures.json --dry-run
dotnet run -- --config /path/to/product/product-captures.json
```

The first .NET command restores NuGet dependencies and builds the tool. Dry-run validates the configuration and prints the file plan; it starts no product command, server, or browser and writes nothing to the consumer repository. SDK restore and build can still write the tool's own build cache. Building from the tool checkout also keeps a product's `global.json` SDK pin from affecting the capture tool.

Use `--reuse-screenshots` to explicitly compose from the saved raw pair. Use `--url https://preview.example` to capture a running preview; this skips local product build and host preparation. Both themes always run. Paths resolve from the configuration file and must remain inside its repository directory; managed paths cannot contain symlinks.

Node 22 is needed for Obsidian, Quartz, or your own Node build commands. Obsidian on Linux also needs a display; the Action supplies Xvfb and Openbox. Local desktop runs use the available display.

## Add to a repository

Copy [example.product-captures.json](example.product-captures.json) to `product-captures.json`, then add this manual workflow.

`.github/workflows/product-captures.yml`:

```yaml
name: Product captures
on:
  workflow_dispatch:
    inputs:
      dry_run:
        type: boolean
        default: true
      reuse_screenshots:
        type: boolean
        default: false
      url:
        type: string
        default: ''
      update_pr:
        type: boolean
        default: true
permissions:
  contents: write
  pull-requests: write
jobs:
  capture:
    uses: grafanaKibana/product-captures/.github/workflows/capture.yml@v2
    with:
      dry-run: ${{ inputs.dry_run }}
      reuse-screenshots: ${{ inputs.reuse_screenshots }}
      url: ${{ inputs.url }}
      update-pr: ${{ inputs.update_pr }}
```

Open **Actions → Product captures → Run workflow**. Leave `dry_run` enabled to validate the configuration and preview the plan. Turn it off to capture both themes; `update_pr` controls whether the finished images open a review-only PR. Captures run only when this workflow is dispatched manually.

GitHub enables manual dispatch after the workflow exists on the repository's default branch. To preview from the command line:

```sh
gh workflow run product-captures.yml --repo owner/repo -f dry_run=true
```

The write permissions support opening an image PR. For artifact-only runs, disable `update_pr` and use `contents: read`.

The shared workflow uses its own revision's capture Action and checks out the caller's source without persisted credentials. Dry-run performs configuration validation within the manual workflow. Add `.product-captures-cache/` to the consumer's `.gitignore` when using Obsidian or Quartz.

## Use the images in a README

```html
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/product-captures/hero-dark.png">
  <img src="docs/assets/product-captures/hero.png" alt="Product overview">
</picture>
```

Adjust both paths if you configure a different output directory. GitHub selects the matching image for the reader's theme.

## Configuration

Unknown keys, duplicate JSON keys, and malformed values fail validation. Every supported key appears below; omitted optional objects use their defaults. There are no separate theme configurations: storage and image inputs select the appropriate theme during each run.

| Key | Type and default | Meaning |
| --- | --- | --- |
| `name` | required string | Product heading. |
| `subtitle` | string; `for Obsidian` for Obsidian, `for Quartz` for Quartz, otherwise empty | Platform label. Explicit empty string hides it. |
| `output` | string; `docs/assets/product-captures` | Destination directory. |
| `appearance` | object; defaults below | One composition for both themes. |
| `capture` | object; web defaults below, or `null` | One screenshot source for both themes; null omits the panel. |
| `build` | command string or command object; absent | Product build before fresh capture. |
| `server` | object; absent | Static directory or command server for a local web capture. |
| `quartz` | object; absent | Prepare a pinned Quartz host and install the consumer plugin. |

### Appearance

Obsidian and Quartz use the photographic setting, with fixed purple and blue identity colors respectively across both themes. Websites use the optical background. Official Obsidian marks and the local font and photo plate remain unchanged. Every layout keeps a frontal, undistorted source panel.

| Key | Type and default | Meaning |
| --- | --- | --- |
| `layout` | string; `column` for plugins, `top` for websites | `column`: name beside panel; `horizontal`: heading and platform above panel; `centered`: contained panel beneath centered heading; `top`: panel extends toward the bottom edge. |
| `width` | integer; `1600` | Finished image width, 1–8192 pixels. |
| `height` | integer; `900` for column/horizontal, `1200` for centered, `1100` for top | Finished image height, 1–8192 pixels. |
| `background` | hex color; `#fcfcfb` | Light website paper. |
| `darkBackground` | hex color; purple `#251a35`, Quartz `#18283e`, website `#10141a` | Dark background and photographic shadow. |
| `tint` | hex color; purple `#684487`, Quartz `#466e9b`, website `#dfe8e4` | Photo overlay or optical folds tint. |
| `shade` | hex color; purple `#442b62`, Quartz `#243d66` | Photographic shade. |
| `glow` | hex color; purple `#c5a3f1`, Quartz `#abc9ec` | Dark-theme photographic highlight. |
| `ink` | hex color; `#252527` | Light website heading color; dark and photographic headings use `#f2f4f6`. |
| `imageZoom` | number; `1` | Source width multiplier in cropped column/horizontal layouts; greater than 0, at most 10. |
| `imageOffsetX` | finite number; `0` | Horizontal source offset in cropped column/horizontal layouts. |
| `imageFit` | `crop` or `contain`; `crop` | Column/horizontal fit; centered always contains and top uses edge bleed. |
| `frameHeight` | fraction; `0.65` | Horizontal panel's maximum artboard height. |
| `edgeBleed` | fraction; `0.06` | Extra panel extent in top layout. |
| `fadeStart` | fraction; `0.67` | Website panel fade begins. |
| `fadeEnd` | fraction; `1` | Website panel fade finishes. |
| `blurStart` | fraction; `0.62` | Website panel softening begins. |
| `backdropPosition` | `left`, `center`, or `right`; `center` | Photo plate alignment. |

Fractions run from 0 to 1. Colors accept `#RGB`, `#RGBA`, `#RRGGBB`, or `#RRGGBBAA`. Fade start must not exceed fade end. Source-free column and horizontal layouts center the identity.

### Capture

| Key | Type and default | Meaning |
| --- | --- | --- |
| `type` | `web`, `obsidian`, or `image`; `web` | Screenshot provider. |
| `url` | string; `/` | Relative web path or absolute HTTP URL. |
| `viewport` | object; `{ "width": 1440, "height": 900 }` for web, height `1000` for Obsidian/Quartz | Raw screenshot `width` and `height`; each integer 1–8192. |
| `ready` | string; absent | CSS selector to wait for after actions. |
| `waitFor` | string; absent | JavaScript predicate to wait for after actions. |
| `css` | string; absent | CSS injected before the screenshot. |
| `storage` | object; `{}` | Local-storage values set before navigation. String values replace `$theme`; `{ "light": "…", "dark": "…" }` selects a value. |
| `actions` | array; `[]` | Ordered `click`, `fill`, or `scroll` actions. |
| `vault` | string; `docs/capture-fixtures/vault` | Obsidian source vault copied into an isolated capture vault. |
| `note` | string; `Product showcase.md` | Note opened in Obsidian. |
| `pluginDir` | string; `.` | Obsidian plugin distribution directory. |
| `appVersion` | string; `1.14.5` | Pinned Obsidian x.y.z release. |
| `installerVersion` | string; `1.14.5` | Pinned Obsidian installer x.y.z release. |
| `images` | object; required for image provider | `{ "light": "path/to/light.png", "dark": "path/to/dark.png" }`; local PNG inputs. |

Action objects support `type` (default `click`), `selector` (required for click/fill), `value` (required string for fill), and `x`/`y` (numbers, default 0, for scroll). Scroll applies to `selector` or the page when omitted. Example:

```json
{
  "storage": { "theme": "$theme", "mode": { "light": "day", "dark": "night" } },
  "actions": [
    { "type": "click", "selector": "#details" },
    { "type": "fill", "selector": "#search", "value": "example" },
    { "type": "scroll", "y": 120 }
  ]
}
```

### Build, server, and Quartz

`build` accepts a shell command string or `{ "command": "npm run build", "cwd": ".", "timeoutMs": 600000 }`. Its `command` is required; `cwd` defaults to the configuration directory and `timeoutMs` to 600000 milliseconds. Commands run through the platform shell.

`server` accepts either `{ "directory": "dist", "port": 4180 }` or `{ "command": "npm run dev -- --port 4180", "url": "http://127.0.0.1:4180", "timeoutMs": 60000 }`. Choose `directory` or `command`; the latter requires `url`. `port` defaults to 4180 and `timeoutMs` to 60000. Command servers require a loopback HTTP URL and are stopped after capture.

Quartz defaults to the pinned host in `.product-captures-cache/quartz`, builds an isolated plugin copy, installs it, and serves the generated site. Its fields are:

| Key | Type and default | Meaning |
| --- | --- | --- |
| `pluginDir` | string; `.` | Plugin source directory. Its package.json must declare `quartz.name`; optional integer `quartz.defaultOrder` defaults to 10. |
| `content` | string; `docs/capture-fixtures` | Fixture content directory. |
| `title` | string; product name | Host page title. |
| `options` | object; `{}` | Plugin options passed to Quartz. |
| `revision` | string; `97a2d05f80c4c50534959b1d0d41cc4b3895625e` | Pinned 40-character host commit. |
| `upstream` | string; `https://github.com/jackyzha0/quartz.git` | HTTPS host Git repository. |
| `inputs` | string array; existing entries from `src`, `types`, `package.json`, `package-lock.json`, `tsconfig.json`, `tsconfig.build.json`, `tsup.config.ts`, `LICENSE` | Files copied into the isolated plugin; package.json and package-lock.json are required. |
| `build` | command string or command object; `npm run build` | Plugin build in the isolated copy; command cwd defaults to that copy. |
| `configuration` | object; `{}` | Overrides merged into the host's Quartz configuration. |

### Action interface

Both `actions/capture` and `.github/workflows/capture.yml` accept exactly:

| Input | Default | Meaning |
| --- | --- | --- |
| `config` | `product-captures.json` | Consumer configuration path. |
| `dry-run` | `true` | Validate and print the plan. |
| `reuse-screenshots` | `false` | Recompose the saved raw pair. |
| `url` | empty | Preview origin override. |
| `release-tag` | empty | Upload the finished pair to this existing release; capture checks out that tag. |
| `update-pr` | `false` | Open a review-only PR containing only owned PNGs. |

The validation workflow accepts only `config`. The Action passes the caller's GitHub token only to publication commands. Release uploads attach the finished pair; image PRs contain all four owned PNGs, or two for source-free configurations. Publication never approves or merges a PR.

## Development and releases

The solution contains two .NET projects:

| Project | Purpose |
| --- | --- |
| `ProductCaptures` | The CLI, capture providers, rendering and publication. |
| `ProductCaptures.Tests` | xUnit tests for configuration, composition and host behavior, organized in separate folders. |

Host providers remain part of the CLI; they do not need a separate production assembly. Node integration and adapter tests live in `tests/node`, use Node's built-in runner and strict assertions, and require no JavaScript test framework.

```sh
dotnet build ProductCaptures.slnx
dotnet bin/Debug/net10.0/ProductCaptures.dll install
dotnet test --solution ProductCaptures.slnx --no-build
node --test tests/node/*.test.mjs
```

Use Node 22 and the .NET 10 SDK. The test project uses xUnit v3 (4.0.2), FluentAssertions 8.11.0 and Microsoft.Testing.Platform. Browser tests use isolated contexts; filesystem and process tests own their temporary resources and clean them up on failure. The production project still pins Microsoft.Playwright 1.62.0, and the small host adapter pins `obsidian-launcher` 3.4.0.

Before publication, verify formatting with `dotnet format <project> --verify-no-changes --no-restore` for `ProductCaptures.csproj` and `tests/ProductCaptures.Tests/ProductCaptures.Tests.csproj`, then run both test suites and the generic browser capture in CI. The SDK analyzers and `.editorconfig` enforce the configured style. CI also checks the same-revision reusable workflows.

Version 2 is a breaking configuration and Action-interface change; 2.0.1 is a compatible readability patch. After the candidate's current-SHA hosted checks pass, create its immutable version tag and publish with an explicit matching title, for example `gh release create v2.0.1 --title v2.0.1 --notes-file release-notes.md`. Release titles contain only the version tag. Advance the compatible floating `v2` ref only after those checks pass. Preserve existing immutable version tags for rollback.

Authored toolkit code uses MIT. Fonts, official branding, dependencies, and image provenance retain their own terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

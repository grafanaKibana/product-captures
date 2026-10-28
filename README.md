# Product captures

Capture the real product with Playwright, then place the result in a reusable HTML/CSS composition. This shared Node 22 runtime works locally, through reusable GitHub workflows, and as a composite action. Consumer repositories keep scenes, fixtures and images; the executable implementation stays here. It does not require a hosted image service or an AI image-generation API.

Two art-direction families ship: `porcelain` uses a magnified upright product crop, restrained white surface, and a separate platform lockup; `studio` uses a dark photographic environment, coherent lighting, and a product close-up. `column` and `horizontal` arrange plugin artwork; `site-top` arranges primary website artwork. Legacy `side`, `wide`, and `reverse` arrangements remain available. Existing `editorial`, `feature`, and `showcase` template IDs remain supported; `feature` compares two configured views. Only the product name and optional platform text are added to artwork. Plugin crops may be intentional; full-desktop layouts preserve the entire viewport. Screenshot rotation, skew, and perspective are never used.

## Run

After the initial `v1` release is published, clone that tested major version once; no npm registry publication or new credentials are required. Use absolute paths so the toolkit and product can live anywhere, including paths with spaces:

```sh
git clone --branch v1 --single-branch https://github.com/grafanaKibana/product-captures.git /absolute/path/to/product-captures
node /absolute/path/to/product-captures/cli.mjs --config /absolute/path/to/product/product-captures.json --dry-run
node /absolute/path/to/product-captures/cli.mjs --config /absolute/path/to/product/product-captures.json --list
npm ci --prefix /absolute/path/to/product-captures
node /absolute/path/to/product-captures/node_modules/playwright/cli.js install chromium
node /absolute/path/to/product-captures/cli.mjs --config /absolute/path/to/product/product-captures.json
```

Quote paths containing spaces. Dependencies and browser installation belong to the shared checkout; product preparation can run the product's own build commands in fresh mode.

To adopt a newer tested `v1`, use a clean dedicated toolkit checkout:

```sh
git -C /absolute/path/to/product-captures fetch origin tag v1 --force
git -C /absolute/path/to/product-captures switch --detach v1
```

For a fixed implementation, clone with `--branch v1.0.0` (or another published immutable version) instead. Maintainers testing an unreleased candidate should check out its full commit SHA explicitly. Reinstall shared dependencies/browser when the selected version changes them.


The planning commands do not install packages, start processes, use the network, check for built input files, or create directories. `--dry-run` is available before installing dependencies.

Options:

| Option | Purpose |
| --- | --- |
| `--config path.json` | Use another repository-owned configuration. |
| `--only hero,social` | Generate selected output IDs. |
| `--template editorial` | Override the selected compositions' template. |
| `--theme light` | Override capture and composition theme; also supports `dark`. |
| `--width 1600 --height 1000` | Override output dimensions. |
| `--scale 2` | Increase pixel density; supported range is 1–3. |
| `--base-url http://127.0.0.1:4179` | Replace the web origin and skip the managed server. Combine with `--skip-prepare` for a running preview. |
| `--skip-prepare` | Reuse an explicitly prebuilt product. |
| `--capture-mode existing` | Deliberately use configured source images rather than launch the product. |
| `--output-dir path` | Write somewhere else inside the configuration directory. |

Fresh capture failures are errors, never a reason to silently use old artwork. Existing-image mode is recorded explicitly in the output manifest. Outputs are staged until all requested captures and compositions succeed. The manifest is the final completion marker. Keep finished PNGs in Git; review generated-image PRs before merging.

## Configure scenes

See `example.product-captures.json`. All paths are relative to the config file, and managed outputs stay inside that directory. IDs use lowercase words separated by hyphens. Config files are trusted source code: prepare/server commands and JavaScript readiness expressions must be reviewed with the repository.

Each `web` capture has a URL, viewport, color scheme, optional `localStorage`, capture-only `styles`, interactions, and a ready selector or expression. A stored value of `$theme` uses the effective capture theme. Capture dependencies wait for fonts and visible images; animations and carets are disabled for the screenshot.

Each `obsidian` capture specifies a dedicated fixture vault, note, local built plugin directory, and pinned application/installer versions. `obsidian-launcher` downloads the real app and installs the plugin in a disposable copied vault with isolated user configuration. Playwright connects to that instance over a loopback debugging port. The personal app and vault are not targeted. `PRODUCT_CAPTURE_OBSIDIAN_CACHE` optionally shares downloaded installers between local repositories; the default cache stays inside the repository. Linux requires Xvfb and a window manager; the Action installs and manages them. Obsidian-specific permission interactions, such as allowing a Mermaid diagram in the trusted disposable fixture, belong in that scene's explicit actions.

The `image` provider reads an authored local image directly, suitable for an exported document or an intentionally static source. It is identified as existing media in provenance.

`project.title` is the product name. `project.subtitle` is optional; use a short platform line such as `for Obsidian`, or leave it empty. Artwork has no marketing headlines, footer captions, or appearance labels. `outputs` select source IDs, template, and dimensions.

Set `project.artDirection` to `porcelain` or `studio`, `project.layout` to `site-top`, `column`, `horizontal`, `desktop-centered`, or a legacy layout, and optional `imageZoom` to control the feature scale. Optional `imageOffsetX` shifts the focal crop horizontally in pixels normalized to a 1600px artboard; zero leaves it unchanged. Outputs can override those choices for social crops or comparisons. Optional `output.frameHeight` limits a wide frame to a fraction of the artboard height (0.1–0.7), allowing a clean crop between content blocks. `project.subtitle` is the optional platform lockup; an empty string omits it. The bundled photographic plate is decorative generated imagery, stored once with provenance; ordinary regeneration makes no image-generation calls. Product imagery comes from the explicitly configured source: real capture or an authored still image. Preview GIFs require real product capture. Fonts, plate, and captures are embedded before rendering, with no remote render assets.

Use curated fixtures with meaningful content and compare artwork at its intended display size.

## Quartz fixture hosts

A web scene can build a pinned Quartz host from consumer-owned plugin source and authored notes. Add a `quartzHost` declaration alongside `captures` and `outputs`; adapt the paths, plugin name/options, and expected markup to your plugin:

```json
{
  "prepare": {"command": "node \"$PRODUCT_CAPTURE_TOOL_ROOT/prepare-quartz.mjs\" --config product-captures.json", "cwd": "."},
  "server": {"command": "node \"$PRODUCT_CAPTURE_TOOL_ROOT/serve.mjs\" --dir .product-captures-cache/quartz/public --port 4179", "cwd": ".", "url": "http://127.0.0.1:4179"},
  "quartzHost": {
    "upstream": "https://github.com/jackyzha0/quartz.git",
    "revision": "97a2d05f80c4c50534959b1d0d41cc4b3895625e",
    "cacheDir": ".product-captures-cache",
    "hostDir": "quartz",
    "plugin": {
      "directory": "plugin",
      "name": "example",
      "inputs": ["src", "package.json", "package-lock.json"],
      "buildArgs": ["run", "build"],
      "options": {},
      "order": 10
    },
    "fixtures": [{"source": "fixtures/capture-note.md", "destination": "index.md"}],
    "configuration": {"pageTitle": "Example", "analytics": null, "enablePopovers": false, "theme": {"fontOrigin": "local"}},
    "assertions": {"file": "public/index.html", "includes": ["example-rendered-marker"], "excludes": ["example-unrendered-marker"]},
    "route": "/",
    "readySelector": "main"
  }
}
```

Commands receive `PRODUCT_CAPTURE_TOOL_ROOT` from the running shared toolkit, overriding an inherited value. Keep product paths relative to the config directory and quote the shared script paths. The runtime owns the isolated cache, copies only declared plugin inputs and fixture mappings, installs/builds there, refreshes staged sources on each run, and checks required/forbidden literal HTML markers before writing its success receipt. `plugin.inputs` must include `package.json` and `package-lock.json` plus all build inputs. `configuration` supports host overrides such as `pageTitle`, `baseUrl`, `enablePopovers`, and `theme`; `plugin.options` remains consumer data. `route` and `readySelector` describe the fixture for tooling; web captures still declare their URL and readiness selector.

The host's origin and exact 40-character revision are verified before reuse. YAML is loaded from that pinned host's installed dependencies only during real preparation. Planning validates declarations and paths without installing packages or running the host. Keep the cache ignored; it must be separate from source inputs and fixture files. Shared Geist font bytes and the generic host style are staged centrally; authored notes, plugin settings and scene selectors stay in the consumer.

## GitHub Actions

Keep event triggers and manual choice declarations in the consumer. Call the shared workflow as a job:

```yaml
permissions:
  contents: read
jobs:
  captures:
    uses: grafanaKibana/product-captures/.github/workflows/capture.yml@v1
    with:
      config: product-captures.json
      dry-run: true
```

The read-only validation entry point is `grafanaKibana/product-captures/.github/workflows/validate.yml@v1`; it accepts only `config` and always plans without installing dependencies or publishing. Use it from a consumer `pull_request` workflow with `contents: read`. Capture workflows are for trusted release/manual configuration. Never run untrusted PR configuration with a write token or use `pull_request_target` for capture. These integrations target GitHub.com.

An existing release job can use the composite directly after its release creation step:

```yaml
- uses: grafanaKibana/product-captures/actions/capture@v1
  with:
    config: product-captures.json
    dry-run: 'false'
    release-tag: ${{ steps.release.outputs.tag_name }}
    update-pr: 'true'
```

Use the actual tag output of your release step. The caller source must already be checked out for a direct composite call. Reusable workflows own checkout, Node setup, execution and artifact handling. The nested action invoked internally is bound to the reusable workflow's revision. No `secrets: inherit`, central PAT or extra credential is required; the caller's `github.token` supplies publication authority. Direct action calls may explicitly supply its `token` input.

The Action has inputs matching the main CLI options, plus `release-tag` and `update-pr`. Defaults are dry-run enabled, fresh captures, and publication disabled. Pass the release's exact tag when attaching images. `update-pr: 'true'` opens a separate PNG-only documentation PR through GitHub's Git data API; it never edits the runner's index or auto-merges. Publishing requires `contents: write`, and image PRs also need `pull-requests: write` and repository permission for Actions to create PRs.

Validate inputs before any dependency installation. Real runs install pinned tool dependencies and the matching Chromium, perform the configured build, capture and export, then upload an artifact. Dry runs perform planning only. Output publication checks the completed manifest's configuration hash and capture mode and uploads only requested finished images.

The installer creates local `workflow_dispatch` metadata with dry-run, mode, output selection, template, theme, dimensions, scale, preview origin, output directory, skip-prepare, release tag, and PR controls. Reusable capture inputs use hyphenated names: `config`, `dry-run` (boolean, true), `capture-mode` (fresh), `only`, `template`, `theme`, `width`, `height`, `scale`, `base-url`, `output-dir` (empty strings), `skip-prepare` (boolean, false), `release-tag` (empty), and `update-pr` (boolean, false). The composite accepts the same controls as strings, plus `token`. Existing release workflows should call the action directly after a release is successfully created: releases created with `GITHUB_TOKEN` do not cause another `release.published` workflow run. An already-existing release is not a new release; use manual dispatch for intentional regeneration.

## Add another repository

```sh
node install.mjs --repo /path/to/repo --id my-project --title "My project" --url http://127.0.0.1:4179 --dry-run
node install.mjs --repo /path/to/repo --id my-project --title "My project" --url http://127.0.0.1:4179 --server "npm run preview" --prepare "npm ci && npm run build" --apply
```

The installer defaults to planning. It creates only `product-captures.json`, two thin workflow files, and `docs/product-captures.md`. It never copies runtime code, dependencies, assets or tests into a consumer. All destinations are preflighted before writes; identical generated files are preserved and conflicting authored files are refused. Existing workflows must be reviewed and edited explicitly. Use `--revision v1.0.0` or a full commit SHA to generate static immutable references instead of the default `v1`. The installer does not delete an old vendored installation: migrate and remove only a reviewed toolkit-owned file manifest.

Add scenes appropriate to the product, and wire the direct composite into token-based release jobs. Keep one capture path for each release to avoid duplicate publication.

## Versions and recovery

`v1` follows deliberately verified compatible releases; rerunning a consumer can receive a newer compatible implementation. Pin all workflow and direct-action references to the same immutable version or full SHA for reproducibility. Consumers retain their configs, authored fixture content, raw captures and finished PNGs. Review generated-image PRs before merge.

Maintainers run the contract/browser suites and cross-repository read-only dry-run plus fresh-render smoke at the candidate SHA before publishing an immutable version such as `v1.0.0`, then move `v1` to that tested commit. Never move an immutable version tag or advance `v1` on every main push. A regression can be contained by pinning callers to the last good immutable version or moving `v1` back to its recorded good commit; publish a corrected patch version after verification.

## Verify

```sh
npm run check
npm run test:browser
```

The contract tests exercise no-write planning, config/path validation, explicit source modes, failed-run preservation, manual argv handling, release/PR boundaries, installer portability and cancellation. The browser smoke test covers real local interaction and all three templates. Use a fixed OS, browser, fonts, application versions and fixture content for repeatable rendering; cross-platform pixel identity is not promised.

Authored toolkit code is MIT licensed; see `LICENSE`. Third-party assets and dependencies retain their own terms, detailed in `THIRD_PARTY_NOTICES.md`. Geist uses the bundled SIL Open Font License. Official Obsidian branding is excluded from the MIT grant. The fixed generated decorative plate retains its checked-in provenance.

## Aligned studio and full-desktop layouts

Plugin configs can use `layout: "column"` or `"horizontal"` with studio grading. The name and optional platform lockup sit around the screenshot; official Obsidian icon-and-wordmark SVGs are bundled unchanged. Their source URLs and hashes are recorded in `assets/obsidian-branding.provenance.json`. Photography tint never applies to the logo.

Website configs can use `site-top` or `desktop-centered` with abstract optical surfaces and lower fade/blur. A manual alternative is `--only horizontal --capture-mode existing` appended to the absolute CLI command above. Omit `--capture-mode existing` for a fresh product capture; manual workflows accept the same output IDs in `only`.

## Authored stills and site polish

Static plugin artwork may use authored or illustrative imagery through the existing `image` provider with an explicit local path. It is recorded as image media; it is never silently substituted after a failed fresh capture. Preview GIFs must show the real product; the still-image renderer does not generate or replace GIFs.

`site-top` places a title above a fuller website view, with `edgeBleed` controlling a small intentional overflow and the bottom fade/blur retained. `siteBackdrop` enables an abstract optical surface of directional light folds, graded with `siteTint`. Website compositions do not embed the studio photograph. The contained `desktop-centered` alternative remains available. Blue studio variants can set `studioTint`, `studioShade`, and `studioGlow`; these affect only the backdrop, never official platform branding or product imagery.

## Release posters and identity-only artwork

An `image` capture can point directly to an authored release PNG. Set `output.imageFit` to `"contain"` for `column` or `horizontal` layouts to display the whole poster without clipping; the default `"crop"` preserves existing screenshot framing. Release artwork is a versioned input: changing the configured poster regenerates the composed output on the next release or manual run.

Set `output.showProduct` to `false` and `output.captures` to `[]` to export the name, optional platform lockup, and backdrop without a product panel. When all outputs use this mode, `captures: []` is valid and no source app is launched. Omit prepare/server commands for a source-free configuration. This supports artwork that needs only a product identity and backdrop.

## Light and dark exports

Outputs can form a pair: the original base filename such as `hero.png` is light, and `hero-dark.png` is dark. Each output declares `theme: "light"` or `"dark"` plus `capturesByTheme: { "light": ["overview-light"], "dark": ["overview-dark"] }`. A normal release or unfiltered manual run generates both. The new-repo installer scaffolds both source scenes and three output pairs. Use real Obsidian captures for plugin fixtures, or source-free outputs when no product panel is needed.

`--only hero,hero-dark` selects a pair. `--theme dark` intentionally overrides the selected outputs' theme and resolves the dark source through `capturesByTheme`, including `--capture-mode existing`. The IDs and filenames remain those selected; use a separate `--output-dir` for exploratory overrides.

README heroes use GitHub's [documented picture/source pattern](https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/quickstart-for-writing-on-github#adding-an-image-to-suit-your-visitors):

```html
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/assets/product-captures/hero-dark.png">
  <source media="(prefers-color-scheme: light)" srcset="docs/assets/product-captures/hero.png">
  <img src="docs/assets/product-captures/hero.png" alt="Product overview">
</picture>
```

`fadeStart`, `fadeEnd`, and `blurStart` configure normalized 0–1 positions on a porcelain window. Defaults are 0.67, 1, and 0.62. Start must precede end; project values can be overridden per output. For example, 0.4, 0.9, and 0.38 dissolve gradually before the bottom edge.

Studio backdrops keep their configured product tint and original base exposure in both UI themes: Obsidian plugins stay purple, and Quartz stays blue. UI screenshots and lighting gradients may adapt. White lettering and the official white Obsidian lockup remain readable against that fixed dark studio ground. Website backgrounds retain their own theme treatment.

Identity-only column artwork centers its name and platform line across the full canvas. In the horizontal alternative, both lines are centered in separate rows above the environment. Product columns with screenshots keep their approved side-column alignment.

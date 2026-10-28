# Third-party notices and asset provenance

The MIT license covers authored toolkit code and documentation. It does not relicense the third-party materials listed here.

- `assets/Geist.ttf` is distributed under the SIL Open Font License 1.1, reproduced in `assets/Geist-LICENSE` with its copyright notice.
- `assets/obsidian-icon.svg`, `assets/obsidian-lockup-light.svg`, and `assets/obsidian-lockup-dark.svg` are unchanged official Obsidian branding. They remain subject to upstream rights and branding terms; they are not covered by the toolkit's MIT grant. Obsidian names and marks belong to their owner. Source URLs, acquisition date, byte counts and SHA256 hashes are recorded in `assets/obsidian-branding.provenance.json`. See https://obsidian.md/brand for upstream branding guidance. Inclusion does not imply endorsement or grant trademark rights.
- `assets/alpine-plate.png` is a fixed decorative image generated once with the built-in image tool for this toolkit's compositions. Its prompt, role and SHA256 are recorded in `assets/alpine-plate.provenance.json`. It is not claimed to depict a documented real location. Regeneration reuses this local plate without calling an image-generation service.
- Microsoft.Playwright 1.62.0 is an Apache-2.0 licensed NuGet dependency. Its downloaded browsers retain their upstream licenses and notices. The optional Node host adapter pins obsidian-launcher 3.4.0 in `adapters/package-lock.json`; that package and its dependencies retain their own licenses. Node is used only for upstream host lifecycle and Quartz tooling, not composition or the capture engine.

Consumer product images, authored fixtures and configurations belong in consumer repositories and are not distributed as toolkit assets.

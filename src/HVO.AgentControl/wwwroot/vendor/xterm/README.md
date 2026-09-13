# Vendored xterm.js assets

Browser terminal assets are committed so the portal never depends on a CDN at
runtime. They were obtained from the npm registry with `npm pack` and copied
byte-for-byte from the published tarballs.

| File | Package | Version |
| --- | --- | --- |
| `xterm.js` | `@xterm/xterm` | `5.5.0` |
| `xterm.css` | `@xterm/xterm` | `5.5.0` |
| `addon-fit.js` | `@xterm/addon-fit` | `0.10.0` |

Licenses are retained alongside the assets: `LICENSE-xterm` and
`LICENSE-addon-fit` (both MIT).

## Provenance

```text
npm pack @xterm/xterm@5.5.0 @xterm/addon-fit@0.10.0
```

- `@xterm/xterm@5.5.0` tarball integrity
  `sha512-hqJHYaQb5OptNunnyAnkHyM8aCjZ1MEIDTQu1iIbbTD/xops91NB5yq1ZK/dC2JDbVWtF23zUtl9JE2NqwT87A==`
- `@xterm/addon-fit@0.10.0` tarball integrity
  `sha512-UFYkDm4HUahf2lnEyHvio51TNGiLK66mqP2JoATy7hRZeXaGMRDr00JiSF7m63vR5WKATF605yEggJKsw0JpMQ==`
- Copied file digests (SHA-256):

```text
1f991ac3b4b283ebf96e60ae23a00a52765dd3a2e46fa6fdda9f1aab032f7495  xterm.js
ba8e6985669488981ccf40c0cefe3aba80722cb6c92de7ad628b0bd717faf2b6  xterm.css
bdaefa370b1bfc42ee88d46fe6072400902a4d4b2d45cd93438dda9b23c97089  addon-fit.js
```

The UMD builds register `window.Terminal` and `window.FitAddon.FitAddon`. They
are loaded as classic deferred scripts in `Components/App.razor` before the
external terminal module runs.

Source maps from the published packages are intentionally not vendored; they are
only fetched when a browser devtools session requests them.

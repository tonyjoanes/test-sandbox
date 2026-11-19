# Sample feed

Stands in for the Azure Artifacts feed in tests and the local demo: `<package>/<version>/`.
A real package also holds `app.zip` (or `site.zip`) next to `release-metadata.yaml`. These
hold only the metadata, which is all `validate`, `plan` and `bom` read. Pass it with
`--local-feed sample-feed`.

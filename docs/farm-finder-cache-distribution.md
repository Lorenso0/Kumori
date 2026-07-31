# Farm Finder pre-built cache

Kumori's **Fetch cache** button reads a small HTTPS manifest, verifies the
referenced SQLite database, and atomically replaces the local Farm Finder
database. If installation fails, the existing cache remains in place.

## Automated publishing

After the full index finishes, run:

```powershell
.\scripts\publish-farm-finder-cache.ps1 `
  -BaseUrl "https://cache.example.com/farm-finder/"
```

No standalone SQLite utility is required. The publisher locates Kumori's
database, creates a consistent compact snapshot, rejects incomplete or failed
index jobs, runs all client-compatible database checks, calculates SHA-256 and
size, and writes a timestamped two-file package to
`Desktop\farm-finder-publish\<timestamp>`.

If the server's web directory is available as a local or network path, publishing
can also be completed in the same command:

```powershell
.\scripts\publish-farm-finder-cache.ps1 `
  -BaseUrl "https://cache.example.com/farm-finder/" `
  -DeployDirectory "\\server\web\farm-finder"
```

The database is deployed first and `manifest.json` last. For S3, Cloudflare R2,
SFTP, or another remote service, connect the generated package to that
provider's uploader; the package creation and validation steps remain the same.

## Configure the app

Set `ManifestUrl` in
`src/Kumori.App/FarmFinder/FarmFinderCacheDistribution.cs` once the production
endpoint exists. The URL must be absolute and use HTTPS.

## Manifest format

Serve JSON using this version 1 format:

```json
{
  "formatVersion": 1,
  "databaseUrl": "https://cache.example.com/farm-finder/farm-2026-07-30.sqlite3",
  "sha256": "64 lowercase or uppercase hexadecimal characters",
  "sizeBytes": 123456789,
  "schemaVersion": 4,
  "generatedAt": "2026-07-30T12:00:00Z",
  "minimumAppVersion": "1.2.0"
}
```

`minimumAppVersion` is optional. Use it when a cache requires a newer Kumori
release even if the database schema number has not changed.

## Publishing rules

1. Finish the indexing job and checkpoint SQLite so the database is a single
   self-contained file. Do not publish `-wal` or `-shm` files.
2. Upload the database to a new immutable HTTPS URL.
3. Calculate SHA-256 and the exact byte size from the uploaded file.
4. Publish the manifest last. Updating the manifest is the release switch.
5. Keep the old database URL available while clients may still hold the old
   manifest in an HTTP cache.

The server should send `Content-Length` and may use normal HTTP caching for the
manifest. Prefer a short cache lifetime for the manifest and a long immutable
cache lifetime for versioned database files.

## Client checks

Before replacement, Kumori checks:

- HTTPS after redirects for both manifest and database
- manifest size and supported format
- maximum database size (2 GB) and exact downloaded byte count
- SHA-256 using a fixed-time digest comparison
- compatible Farm Finder schema and minimum app version
- non-future generation date and no downgrade from an installed server cache
- SQLite integrity, foreign keys, required tables, and non-empty core data
- absence of unfinished server-side index jobs

The download is written to a staging file and flushed to disk. The existing
database is replaced atomically and retained as `.previous`. If post-install
validation or repository reload fails, Kumori restores that previous database.

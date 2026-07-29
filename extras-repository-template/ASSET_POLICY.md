# Asset policy

- Presence in the owner's publishing library authorizes publication.
- Packages are complete `.kextra` archives stored inside two deterministic
  catalog bundles; partial and delta assets are not used.
- A changed catalog publication regenerates both bundles. Kumori downloads each
  required bundle once and extracts every declared complete entry from it.
- Published release assets and tags are immutable.
- Each bundle and contained package is limited to 256 MiB compressed. Each
  package is limited to 512 MiB expanded and 4,096 internal entries.
- Withdrawal stops new installation but does not delete historical release assets.
- Favorites, local tags, display-name overrides, and usage history are never published.

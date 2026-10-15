# Vendored copy

This folder is a copy of the aligner's own repository, kept in sync with it by hand.

- Upstream repository: https://github.com/Sparpp/typebeat-lyriclab
- Upstream commit: not recorded; fill in at the next sync.
- ALIGNER_VERSION: 11

`ALIGNER_VERSION` above is the value `align_lyrics.py` declares. The game's tests
(`LyricMapImporterTest.VendoredRecordNamesTheShippedAlignerVersion`) check that the two agree,
so a sync that changes the script's version updates this file in the same commit, with the
upstream commit it was taken from.

# Vendored copy

This folder is a copy of the aligner's own repository, kept in sync with it by hand.

- Upstream repository: https://github.com/Sparpp/typebeat-lyriclab
- Upstream commit: 70b9965 (branch claude/cool-tesla-yaiby3, "Aligner version 12: a full-mix letter
  model is the default engine"), applied on top of this copy's own changes since 1e4d76ee.
- ALIGNER_VERSION: 12

Changes made here and not upstream yet: the mirrored model table (`MODEL_FILES`, models from
dl.typebeat.sh first), version 12's four model files in that table, the install COMPONENTS
(`setup.ps1 -Component`, `setup.sh --component=`, `MODEL_COMPONENTS`: the aligner and Demucs are
installed apart), and an aligner run without Demucs reading the full mix instead of failing.

`ALIGNER_VERSION` above is the value `align_lyrics.py` declares. The game's tests
(`LyricMapImporterTest.VendoredRecordNamesTheShippedAlignerVersion`) check that the two agree,
so a sync that changes the script's version updates this file in the same commit, with the
upstream commit it was taken from.

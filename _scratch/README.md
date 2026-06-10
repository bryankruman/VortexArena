# `_scratch/` — local dev/test dump

Drop throwaway files here: test textures, before/after comparison captures,
one-off experiment images/models, anything you don't want in the project root.

Why this folder exists:

- **Godot skips it.** The `.gdignore` next to this file tells the editor not to
  import the contents, so you never again get a pile of `*.import` sidecars
  polluting the working tree (the thing that prompted this folder).
- **Git skips it.** Everything here except this README and `.gdignore` is
  git-ignored, so scratch files never get committed by accident.

Both marker files (`.gdignore`, `README.md`) **are** tracked, so a fresh
checkout already has the folder set up — just start dropping files in.

Capture screenshots? Those have their own home: `../screenshots/` (also
`.gdignore`'d). See `RUNNING.md` → *Visual capture*.

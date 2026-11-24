# MagmPaperEngine

The maintainer expects a GitHub release and a direct Windows installer link as part of MagmPaper implementation work, unless the request specifies a different delivery.

- Prepare the next version in `desktop/Directory.Build.props`, `RELEASE_NOTES.md`, and the installer references in `README.md`.
- Push the tested changes to `main` and push a new matching `v<version>` tag. The Windows release workflow runs the Windows checks and publishes the installer.
- Wait for the release to finish, confirm the installer is available, and include its direct download link in the final response.

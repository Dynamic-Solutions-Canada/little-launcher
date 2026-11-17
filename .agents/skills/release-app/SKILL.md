---
name: release-app
description: Version, tag, and publish a Little Launcher release when the user requests a release. Does not apply to local sideloaded test builds.
---

# Release Little Launcher

Read [versioning](../../../docs/versioning.md) and
[packaging](../../../docs/installer.md). Determine the release version from the user's
request and changes since the last tag; resolve ambiguous release scope before publication.
Review affected documentation through the [commit workflow](../commit/SKILL.md).

`Directory.Build.props` is the only version source. Update it, validate the affected code, and
commit the version and relevant docs. Create the matching annotated `vX.Y.Z` tag. When release
publication is authorized, push the intended branch and tag and verify the GitHub workflow result.
Do not repeat approval already given for that release.

The tag publishes portable artifacts and submits Store MSIX packages through separate workflows.
Verify both results; API pricing reported as `Base` still requires resolution in Partner Center;
never publish paid Store packages as public CI artifacts. A local four-part sideload version uses
the [rebuild workflow](../rebuild-desktop/SKILL.md) and is not a public release.

## Pending for the next release

- The first release after v1.43.0 stops sending website hostnames to Google's favicon service (FIX-061). It also makes new 1-way OneDrive share links view-only instead of editable. When it ships, tell the standards session so the website privacy page is updated to match both. Remove this line once that is done.

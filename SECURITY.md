# Security policy

## Supported versions

Only the latest release gets fixes. Installed copies update themselves, so a fix reaches people
as soon as it is released.

## Reporting a vulnerability

Please **don't open a public issue** for security problems. Report them privately through GitHub:
open the repository's **Security** tab and choose **Report a vulnerability**. You'll get a reply
within a week, and credit in the release notes if you'd like it.

Useful things to include: the version (Help → About), what an attacker can do, and the steps to
reproduce it.

## What is in scope

- The update pipeline: anything that would make an installed copy install a file the maintainers
  didn't release.
- The browser integration: a web page that can add downloads, read the download list, or reach the
  native-messaging host without the user's action.
- The installer and uninstaller: writing outside the chosen folder, or removing files they didn't create.
- File handling: a server that can make the app write outside the download folder (for example through
  a crafted file name or redirect).

## How releases are protected

- Every release's update information (`latest.json`) is signed with an ECDSA P-256 key. Its public half
  is compiled into the app, and the private half exists only in a GitHub Actions secret and the
  maintainer's offline backup.
- The installer is checked against the SHA-256 and size in the signed `latest.json` before it runs.
- Releases are built only by GitHub Actions from tagged commits on `main`, and each file carries a
  build provenance attestation (`gh attestation verify <file> --repo <owner>/<repo>`).

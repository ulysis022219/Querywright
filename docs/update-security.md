# Direct update trust and validation

Updates start only when the user clicks **Install update**. The installed extension
copies its bundled updater and installer into a unique directory below the user's
LocalApplicationData folder. It launches Windows PowerShell using a fixed system
path and `-File`; release text is never evaluated as a command. Only numeric stable
release tags are accepted. This follows the existing installer execution-policy
behavior; an execution policy is not a code-signing guarantee.

The helper retrieves the selected release from the fixed GitHub repository. It
requires a published, non-prerelease record with an exact matching tag and exactly
one `Querywright-ssms22.zip` asset at the expected repository download URL. A GitHub
SHA-256 asset digest is mandatory. Downloads use HTTPS with normal certificate
validation; redirects are limited to github.com and release-assets.githubusercontent.com,
with at most five redirects, a five-minute total timeout and a 100 MiB limit.

After verifying the digest, the helper extracts only the exact VSIX entry, limited
to 200 MiB. Downloaded PowerShell scripts are never executed. Package checks reject
unsafe Windows paths, duplicate names, oversized expanded contents, XML DTDs, an
unexpected extension ID, a mismatched release version or a different SSMS target.
XML parsing disables external resolution and limits manifest size.

A per-user-session mutex prevents concurrent direct updaters. Installation waits
for all processes from the originating SSMS installation; processes with unreadable
paths also prevent proceeding. The existing installer rechecks running processes,
validates the package again, selects the matching SSMS instance and checks the
VSIXInstaller exit code. The updater does not terminate SSMS or restart it automatically.
Failures remain visible in its console, and the existing installer reports its log.

The trust root remains GitHub HTTPS and the repository's release permissions. A
digest supplied by GitHub detects a mismatched download; it is not an independent
publisher signature and cannot defend against a compromised release account or
build pipeline. Likewise, these checks do not protect against an attacker already
able to modify files or processes as the current Windows user. No claim of zero
vulnerabilities or an independent security audit is made.

`scripts/Test-Update.ps1` exercises release validation, redirect restrictions,
download limits, checksum rejection, extraction, package identity/version and
malformed archives without network access or installation. It runs in Windows CI.
Real VSIX installation and SSMS behavior must be validated on Windows before release.

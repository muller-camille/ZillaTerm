# Build image for Anthropic's OSS Scanner (https://github.com/anthropics/oss-scanner).
# The scanner builds it with this repository as the build context (`COPY . /src`), with network access; the security
# analysis then runs in the finished image without Internet access, so everything the build and the tests need is
# fetched here.
#
# ZillaTerm is a Windows desktop application (WPF, .NET 10). Its protocol, parsing and cryptography code lives in
# src/ZillaTerm.Core (net10.0), which builds and runs its tests on Linux. The WPF application (src/ZillaTerm.App)
# builds here too (EnableWindowsTargeting) so that its code can be read and compiled, but it only runs on Windows.
FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip \
    EnableWindowsTargeting=true \
    NuGetAudit=false

# scanner contract: the checkout lives inside the image, at /src
COPY . /src
WORKDIR /src

# SSH.NET is the 2026.0.0 release plus two patches (it keeps the SFTP "longname" line, which carries the owner and group
# names; X11 forwarding for shells), built from a fork at a pinned commit by its release workflow and downloaded, checked
# by its pinned SHA-256 sum, into local-packages/ (see tools/sshnet-patched.sh and nuget.config). Then restore every package while the network is available and build the whole solution (Debug:
# symbols, no optimisation). Warnings are errors in this repository, as in its CI.
RUN bash tools/sshnet-patched.sh \
 && dotnet restore ZillaTerm.sln \
 && dotnet build ZillaTerm.sln -c Debug --no-restore

# Cheap check that the image works offline; a failing test does not fail the build.
RUN dotnet test tests/ZillaTerm.Core.Tests -c Debug --no-build \
    || echo "WARNING: ZillaTerm.Core.Tests reported failures"

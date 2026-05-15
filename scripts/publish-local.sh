#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${CONFIGURATION:-Release}"
output_dir="${DEN_PUBLISH_OUTPUT:-${repo_root}/artifacts/publish/DenPublish.Api}"

mkdir -p "${output_dir}"

dotnet restore "${repo_root}/DenPublish.slnx"
dotnet publish "${repo_root}/src/DenPublish.Api/DenPublish.Api.csproj" \
  --configuration "${configuration}" \
  --no-restore \
  --output "${output_dir}" \
  /p:UseAppHost=false

printf 'den-publish API published to %s\n' "${output_dir}"

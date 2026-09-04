.PHONY: check contracts

# The versioned terrabase bundle is not published yet. Keep the pin explicit
# and fail closed until a release artifact can be verified; never regenerate
# from a sibling checkout or silently claim contract parity.
CONTRACT_VERSION_FILE := CONTRACT_VERSION

check:
	dotnet format TerraScale.DotNet.slnx --verify-no-changes
	dotnet build TerraScale.DotNet.slnx --configuration Release
	dotnet test TerraScale.DotNet.slnx --configuration Release --no-build --no-progress

contracts:
	@test -f "$(CONTRACT_VERSION_FILE)" || { echo "missing pinned contract version: $(CONTRACT_VERSION_FILE)" >&2; exit 1; }
	@version=$$(tr -d '[:space:]' < "$(CONTRACT_VERSION_FILE)"); \
	case "$$version" in v[1-9]|v[1-9][0-9]*) ;; *) echo "invalid pinned contract version: $$version" >&2; exit 1 ;; esac; \
	echo "contract $$version is not published; refusing to download or regenerate unpublished artifacts" >&2; \
	exit 1
